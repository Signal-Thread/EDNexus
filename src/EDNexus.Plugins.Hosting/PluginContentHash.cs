using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting;

/// <summary>
/// What <see cref="PluginHost"/> hands the consent callback for one plugin that is about to load:
/// the manifest, and hashes of the code and files that will actually run. A consent decision is
/// only meaningful for these exact contents, so a grant (see <see cref="PluginGrant"/>) records all
/// three and <see cref="PluginConsent.FromGrants"/> insists they all still match.
/// </summary>
/// <param name="Manifest">The parsed, validated <c>plugin.json</c>.</param>
/// <param name="EntryAssemblyHash">
/// Lowercase hex SHA-256 of the exact bytes of the entry assembly that will be loaded. The host
/// reads the assembly into memory once, hashes that image and loads that same image, so what was
/// approved is what runs (see <see cref="PluginContentHash.OfAssembly"/>).
/// </param>
/// <param name="FilesHash">
/// Lowercase hex SHA-256 over every other file in the plugin folder (see
/// <see cref="PluginContentHash.OfFolder"/> for the format and exclusions).
/// </param>
public sealed record PluginConsentRequest(PluginManifest Manifest, string EntryAssemblyHash, string FilesHash)
{
    /// <summary>
    /// Reads an installed plugin folder the way the host will, for a consent UI to show and then
    /// record: parses <c>plugin.json</c>, reads the entry assembly and hashes it and the other
    /// files. Pass the result to <see cref="PluginGrantStoreExtensions.Grant(IPluginGrantStore, PluginConsentRequest, IEnumerable{string})"/>
    /// so the grant is bound to exactly what the commander was shown. If the folder changes after this
    /// call the grant simply no longer matches and the plugin is denied until approved again.
    /// </summary>
    /// <param name="pluginDirectory">The plugin's folder, <c>&lt;root&gt;/&lt;id&gt;</c>.</param>
    /// <param name="request">The request, or <see langword="null"/> on failure.</param>
    /// <param name="problem">Why the folder could not be read; <see langword="null"/> on success.</param>
    public static bool TryRead(string pluginDirectory, out PluginConsentRequest? request, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(pluginDirectory);
        request = null;
        try
        {
            var dir = Path.GetFullPath(pluginDirectory);
            var parsed = PluginManifestParser.ParseFile(Path.Combine(dir, PluginManifestParser.FileName));
            if (parsed.Manifest is not { } manifest)
            {
                problem = "invalid manifest: " + string.Join("; ", parsed.Errors);
                return false;
            }
            if (PluginHost.ResolveEntryAssembly(dir, manifest.EntryAssembly, out var entryPath) is { } badPath)
            {
                problem = badPath;
                return false;
            }
            if (new FileInfo(entryPath!).Length > PluginHost.MaxEntryAssemblyBytes)
            {
                problem = "the entry assembly is larger than the host will load";
                return false;
            }
            var entryHash = PluginContentHash.OfAssembly(File.ReadAllBytes(entryPath!));
            var files = PluginContentHash.OfFolder(dir, entryPath);
            if (files.Hash is null)
            {
                problem = files.Problem;
                return false;
            }
            request = new PluginConsentRequest(manifest, entryHash, files.Hash);
            problem = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = "the plugin folder could not be read: " + ex.Message;
            return false;
        }
    }
}

/// <summary>The outcome of <see cref="PluginContentHash.OfFolder"/>.</summary>
/// <param name="Hash">The hash, or <see langword="null"/> when the folder cannot be hashed (a plugin like that is never offered for consent).</param>
/// <param name="Problem">Why not.</param>
/// <param name="Unreadable">True when the cause was an I/O failure rather than the folder's contents breaking a rule.</param>
public sealed record PluginFilesHashResult(string? Hash, string? Problem, bool Unreadable);

/// <summary>
/// Content hashes that bind a consent grant to a plugin's code, not just its <c>plugin.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Entry assembly</b> (<see cref="OfAssembly"/>): plain SHA-256 of the image bytes, so it can be
/// reproduced with <c>sha256sum</c>. This is the strong part: <see cref="PluginHost"/> hashes the
/// very <c>byte[]</c> it then loads, so nothing that happens on disk afterwards can change what was
/// approved.
/// </para>
/// <para>
/// <b>Everything else</b> (<see cref="OfFolder"/>): the plugin's dependency DLLs, native libraries,
/// <c>.deps.json</c> and any other file. These are still loaded <em>by path</em> (the load context
/// resolves them lazily) after the check, so this hash protects against a package being swapped or
/// updated between approvals (an uninstall followed by a trojanised package with a copied manifest,
/// a dependency replaced under an unchanged manifest) but cannot protect against a local actor with
/// write access to the plugin folder who races the load. That actor can already run code as the
/// user, so it crosses no trust boundary.
/// </para>
/// </remarks>
public static class PluginContentHash
{
    /// <summary>Name of the plugin's private storage folder, which is excluded from <see cref="OfFolder"/> (the plugin writes there at run time).</summary>
    public const string DataFolderName = "data";

    private const string FilesDomainTag = "EDNexus.PluginFiles.v1";

    /// <summary>Lowercase hex SHA-256 of <paramref name="image"/>.</summary>
    public static string OfAssembly(ReadOnlySpan<byte> image) => Convert.ToHexStringLower(SHA256.HashData(image));

    /// <summary>
    /// Hashes every file under <paramref name="directory"/> except the entry assembly, the manifest
    /// (<c>plugin.json</c>, bound by <see cref="PluginManifestFingerprint"/>) and the top-level
    /// <see cref="DataFolderName"/> folder.
    /// <para>
    /// <b>Format.</b> SHA-256 over: the ASCII tag <c>EDNexus.PluginFiles.v1</c> (int32 length prefix),
    /// the file count (int32), then for each file in ordinal order of its relative path
    /// (forward slashes, no leading slash): int32 UTF-8 path length, the path bytes, int64 content
    /// length, and the 32-byte SHA-256 of the content. Integers are little-endian. The order is
    /// sorted here, never the file system's enumeration order.
    /// </para>
    /// <para>
    /// <b>Links.</b> A symbolic link, junction or other reparse point anywhere in the folder (outside
    /// the excluded <c>data</c> folder, which is not entered) makes the folder unhashable: the loader
    /// would follow it to content that is not under the plugin folder, so no hash of the folder
    /// could vouch for it. Nothing is ever read through a link, and a plugin with one is refused
    /// before consent rather than offered for approval. (The folder passed in may itself be reached
    /// through a link; what is hashed is what is found inside it.)
    /// </para>
    /// <para>
    /// <b>Bounds.</b> At most <see cref="PluginPackageLimits.MaxEntries"/> files and folders,
    /// <see cref="PluginPackageLimits.MaxEntryBytes"/> per file, <see cref="PluginPackageLimits.MaxTotalUncompressedBytes"/>
    /// in total, and <see cref="PluginPathRules.MaxDepth"/> levels: the same limits a package can be
    /// installed under, so anything the installer accepts can be hashed. Files are streamed, never
    /// held in memory.
    /// </para>
    /// </summary>
    /// <param name="directory">The plugin folder.</param>
    /// <param name="entryAssemblyPath">Full path of the entry assembly (hashed separately), or null to include everything.</param>
    /// <param name="limits">Bounds; default <see cref="PluginPackageLimits.Default"/>.</param>
    public static PluginFilesHashResult OfFolder(string directory, string? entryAssemblyPath, PluginPackageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        limits ??= PluginPackageLimits.Default;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        try
        {
            var root = Path.GetFullPath(directory);
            var entry = entryAssemblyPath is null ? null : Path.GetFullPath(entryAssemblyPath);
            var files = new List<(string Relative, string Full)>();
            var count = 0;
            if (Collect(root, "", 0) is { } problem)
                return new PluginFilesHashResult(null, problem, false);

            files.Sort(static (a, b) => string.CompareOrdinal(a.Relative, b.Relative));

            using var outer = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendBytes(outer, Encoding.ASCII.GetBytes(FilesDomainTag));
            AppendInt32(outer, files.Count);
            long total = 0;
            foreach (var (relative, full) in files)
            {
                AppendBytes(outer, Encoding.UTF8.GetBytes(relative));
                var (length, digest) = HashContent(full, limits, ref total, out var tooBig);
                if (tooBig is not null)
                    return new PluginFilesHashResult(null, tooBig.Replace("{file}", relative, StringComparison.Ordinal), false);
                AppendInt64(outer, length);
                outer.AppendData(digest);
            }
            return new PluginFilesHashResult(Convert.ToHexStringLower(outer.GetHashAndReset()), null, false);

            string? Collect(string dir, string prefix, int depth)
            {
                if (depth >= PluginPathRules.MaxDepth)
                    return $"the plugin folder is nested deeper than {PluginPathRules.MaxDepth} levels";
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    var relative = prefix.Length == 0 ? info.Name : prefix + "/" + info.Name;
                    var isDirectory = info is DirectoryInfo;

                    // The plugin's own scratch space: never entered, so never read, link or not.
                    if (depth == 0 && isDirectory && string.Equals(info.Name, DataFolderName, comparison))
                        continue;

                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                        return $"the plugin folder contains a symbolic link or junction ('{Display(relative)}'), which is not supported";

                    if (++count > limits.MaxEntries)
                        return $"the plugin folder holds more than {limits.MaxEntries} files and folders";

                    if (isDirectory)
                    {
                        if (Collect(info.FullName, relative, depth + 1) is { } nested)
                            return nested;
                        continue;
                    }

                    if (depth == 0 && string.Equals(info.Name, PluginManifestParser.FileName, comparison))
                        continue;   // bound by the manifest fingerprint
                    if (entry is not null && string.Equals(Path.GetFullPath(info.FullName), entry, comparison))
                        continue;   // bound by the entry-assembly hash
                    files.Add((relative, info.FullName));
                }
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new PluginFilesHashResult(null, "the plugin folder could not be read: " + Display(ex.Message), true);
        }
    }

    private static (long Length, byte[] Digest) HashContent(string path, PluginPackageLimits limits, ref long total, out string? problem)
    {
        problem = null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        // Counts the bytes actually read, not the length the file system reported, so a file that
        // grows while it is being hashed still hits the cap.
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            length += read;
            total += read;
            if (length > limits.MaxEntryBytes)
            {
                problem = $"'{{file}}' is larger than the {limits.MaxEntryBytes}-byte per-file limit";
                return (length, []);
            }
            if (total > limits.MaxTotalUncompressedBytes)
            {
                problem = $"the plugin folder is larger than the {limits.MaxTotalUncompressedBytes}-byte limit (at '{{file}}')";
                return (length, []);
            }
            hash.AppendData(buffer, 0, read);
        }
        return (length, hash.GetHashAndReset());
    }

    // Length-prefixed, so no two different file lists share an input.
    private static void AppendBytes(IncrementalHash hash, byte[] bytes)
    {
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static string Display(string text) => TextRules.ForDisplay(text, 200);
}
