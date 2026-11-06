using System.Text.Json;

namespace EDNexus.Plugins.Hosting;

/// <summary>
/// Vets a plugin's <c>&lt;entry&gt;.deps.json</c> before <see cref="System.Runtime.Loader.AssemblyDependencyResolver"/>
/// is allowed to read it. The resolver hands the file to the runtime's <em>native</em> host policy
/// library, which parses it without checking types: a <c>deps.json</c> that is syntactically valid
/// JSON but is missing a key it expects (<c>runtimeTarget</c>, a library's <c>type</c> or
/// <c>sha512</c>, ...) or has one of the wrong type does not surface as a managed exception. It aborts
/// the whole process with an uncatchable native <c>std::logic_error</c> (SIGABRT) or a segmentation
/// fault, so no <c>try/catch</c> in the host can contain it. Plain garbage is harmless (the native
/// parser reports that as a normal error and the resolver throws), which is why the fix cannot be
/// "catch the exception".
/// <para>
/// This check accepts only a document shaped like the ones the .NET SDK emits for a library: every
/// member the native reader dereferences is present and has the right JSON type. It is deliberately
/// stricter than the native reader (comments, trailing commas, duplicate keys, non-UTF-8 text,
/// absurd nesting and oversized files are all refused) and ignores keys the native reader does not
/// use. A plugin whose file fails here is <see cref="PluginLoadStatus.Rejected"/> rather than loaded
/// without its dependency map, because a plugin with a deps.json that is wrong is a broken or tampered
/// package and half-resolving its dependencies would only fail later and less clearly.
/// </para>
/// </summary>
internal static class PluginDepsJson
{
    /// <summary>The largest <c>.deps.json</c> accepted. SDK output is a few KiB; a plugin with hundreds of packages stays well under this.</summary>
    internal const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>The deepest JSON nesting accepted. SDK output nests 7 levels deep.</summary>
    internal const int MaxDepth = 16;

    /// <summary>
    /// The file <see cref="System.Runtime.Loader.AssemblyDependencyResolver"/> reads for
    /// <paramref name="entryAssemblyPath"/>: the entry assembly's name with its last extension
    /// (if any) replaced by <c>.deps.json</c>, beside it.
    /// </summary>
    internal static string PathFor(string entryAssemblyPath) => Path.ChangeExtension(entryAssemblyPath, ".deps.json");

    /// <summary>
    /// Checks the <c>.deps.json</c> the resolver would read for <paramref name="entryAssemblyPath"/>.
    /// Returns <see langword="null"/> when it is safe to hand to the resolver (including when there
    /// is no such file, in which case the resolver falls back to the assemblies beside the entry),
    /// otherwise a display-safe reason.
    /// </summary>
    internal static string? CheckFor(string entryAssemblyPath)
    {
        var path = PathFor(entryAssemblyPath);
        var name = Path.GetFileName(path);
        ReadOnlyMemory<byte> bytes;
        try
        {
            if (Directory.Exists(path))
                return $"'{name}' is a folder, not a file";
            if (!File.Exists(path))
                return null;

            // Read at most MaxBytes + 1 so a file that grows (or lies about its length) is still bounded.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes)
                return $"'{name}' is larger than the {MaxBytes / (1024 * 1024)} MiB limit";
            var buffer = new byte[Math.Min(MaxBytes + 1L, stream.Length + 1)];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0)
                read += n;
            if (read > MaxBytes)
                return $"'{name}' is larger than the {MaxBytes / (1024 * 1024)} MiB limit";
            bytes = buffer.AsMemory(0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"'{name}' could not be read ({ex.GetType().Name})";
        }

        return CheckContent(bytes) is { } problem ? $"'{name}' is not a usable dependency manifest: {problem}" : null;
    }

    /// <summary>Checks the bytes of a <c>.deps.json</c>; <see langword="null"/> means safe for the resolver.</summary>
    internal static string? CheckContent(ReadOnlyMemory<byte> utf8)
    {
        var head = utf8.Span;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            utf8 = utf8[3..];   // a UTF-8 byte-order mark is normal for hand-saved JSON; the runtime accepts it

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                MaxDepth = MaxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException ex)
        {
            return "it is not valid JSON (" + ex.GetType().Name + ")";
        }

        using (document)
        {
            try
            {
                return CheckRoot(document.RootElement);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                // A string that cannot be decoded (a lone surrogate escape, say) throws on read.
                return "it contains text that cannot be decoded";
            }
        }
    }

    private static string? CheckRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "the top level is not an object";
        if (HasDuplicateKeys(root))
            return "an object has the same key twice";

        // runtimeTarget: { "name": "<tfm>[/<rid>]", "signature": "..." }
        if (!root.TryGetProperty("runtimeTarget", out var runtimeTarget) || runtimeTarget.ValueKind != JsonValueKind.Object)
            return "'runtimeTarget' is missing or not an object";
        if (Str(runtimeTarget, "name") is null)
            return "'runtimeTarget.name' is missing or not a string";
        if (Present(runtimeTarget, "signature", out var signature) && signature.ValueKind != JsonValueKind.String)
            return "'runtimeTarget.signature' is not a string";

        // targets: { "<tfm>": { "<lib>/<version>": { dependencies, runtime, native, resources, runtimeTargets } } }
        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            return "'targets' is missing or not an object";
        foreach (var target in targets.EnumerateObject())
        {
            if (target.Value.ValueKind != JsonValueKind.Object)
                return $"target '{Show(target.Name)}' is not an object";
            foreach (var library in target.Value.EnumerateObject())
            {
                if (library.Value.ValueKind != JsonValueKind.Object)
                    return $"'{Show(library.Name)}' in target '{Show(target.Name)}' is not an object";
                if (CheckTargetLibrary(library.Value) is { } bad)
                    return $"'{Show(library.Name)}' in target '{Show(target.Name)}': {bad}";
            }
        }

        // libraries: { "<lib>/<version>": { type, serviceable, sha512, path, hashPath } }
        if (Present(root, "libraries", out var libraries))
        {
            if (libraries.ValueKind != JsonValueKind.Object)
                return "'libraries' is not an object";
            foreach (var library in libraries.EnumerateObject())
            {
                if (library.Value.ValueKind != JsonValueKind.Object)
                    return $"library '{Show(library.Name)}' is not an object";
                if (Str(library.Value, "type") is null)
                    return $"library '{Show(library.Name)}': 'type' is missing or not a string";
                if (Str(library.Value, "sha512") is null)
                    return $"library '{Show(library.Name)}': 'sha512' is missing or not a string";
                foreach (var key in (ReadOnlySpan<string>)["path", "hashPath"])
                    if (Present(library.Value, key, out var v) && v.ValueKind != JsonValueKind.String)
                        return $"library '{Show(library.Name)}': '{key}' is not a string";
                if (Present(library.Value, "serviceable", out var serviceable) && serviceable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return $"library '{Show(library.Name)}': 'serviceable' is not a boolean";
            }
        }
        return null;
    }

    private static string? CheckTargetLibrary(JsonElement library)
    {
        if (Present(library, "dependencies", out var dependencies))
        {
            if (dependencies.ValueKind != JsonValueKind.Object)
                return "'dependencies' is not an object";
            foreach (var dependency in dependencies.EnumerateObject())
                if (dependency.Value.ValueKind != JsonValueKind.String)
                    return $"dependency '{Show(dependency.Name)}' is not a string";
        }

        foreach (var section in (ReadOnlySpan<string>)["runtime", "native", "resources", "runtimeTargets"])
        {
            if (!Present(library, section, out var files))
                continue;
            if (files.ValueKind != JsonValueKind.Object)
                return $"'{section}' is not an object";
            foreach (var file in files.EnumerateObject())
            {
                if (file.Value.ValueKind != JsonValueKind.Object)
                    return $"'{section}' entry '{Show(file.Name)}' is not an object";
                foreach (var key in (ReadOnlySpan<string>)["assemblyVersion", "fileVersion", "locale", "rid", "assetType"])
                    if (Present(file.Value, key, out var v) && v.ValueKind != JsonValueKind.String)
                        return $"'{section}' entry '{Show(file.Name)}': '{key}' is not a string";
                // A runtime-specific asset is dereferenced by rid and asset type.
                if (section == "runtimeTargets" && (Str(file.Value, "rid") is null || Str(file.Value, "assetType") is null))
                    return $"'runtimeTargets' entry '{Show(file.Name)}' needs string 'rid' and 'assetType'";
            }
        }
        return null;
    }

    /// <summary>The string value of <paramref name="key"/>, or <see langword="null"/> when absent or not a string.</summary>
    private static string? Str(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Whether <paramref name="key"/> is present (an explicit <c>null</c> counts as present, then fails the type check that follows).</summary>
    private static bool Present(JsonElement obj, string key, out JsonElement value)
        => obj.TryGetProperty(key, out value);

    /// <summary>Untrusted text from the file, made safe to put in a one-line reason.</summary>
    private static string Show(string text) => TextRules.ForDisplay(text, 120);

    /// <summary>Whether any object anywhere in the document repeats a key (the native and managed readers could then disagree about which wins).</summary>
    private static bool HasDuplicateKeys(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    if (!seen.Add(property.Name) || HasDuplicateKeys(property.Value))
                        return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (HasDuplicateKeys(item))
                        return true;
                return false;
            default:
                return false;
        }
    }
}
