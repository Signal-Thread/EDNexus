using System.Collections.Concurrent;
using EDNexus.Plugins.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// Compiles tiny plugin assemblies with Roslyn at test time and lays them out as installed plugin
/// folders, so the host is exercised against real assemblies without committing any binaries.
/// </summary>
internal static class TestPlugins
{
    public const string AbstractionsFileName = "EDNexus.Plugins.Abstractions.dll";

    private static readonly Lazy<MetadataReference[]> FrameworkReferences = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p) is var name
                && (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("System.dll", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    private static readonly MetadataReference AbstractionsReference
        = MetadataReference.CreateFromFile(typeof(IEDNexusPlugin).Assembly.Location);

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

    /// <summary>Compiles <paramref name="source"/> into a DLL image (cached per name + source).</summary>
    /// <param name="referenceAbstractions">Whether the SDK contract is referenced.</param>
    /// <param name="references">Other in-memory assemblies to reference.</param>
    public static byte[] Compile(string assemblyName, string source, bool referenceAbstractions = true, params byte[][] references)
        => Cache.GetOrAdd(assemblyName + "\n" + referenceAbstractions + "\n" + source + "\n" + string.Join(",", references.Select(r => r.Length)), _ =>
        {
            var refs = FrameworkReferences.Value
                .Concat(referenceAbstractions ? [AbstractionsReference] : [])
                .Concat(references.Select(r => (MetadataReference)MetadataReference.CreateFromImage(r)));
            var compilation = CSharpCompilation.Create(
                assemblyName,
                [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            if (!emitted.Success)
                throw new InvalidOperationException("test plugin failed to compile:\n" + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            return stream.ToArray();
        });

    /// <summary>
    /// A plugin whose <c>Initialize</c> logs "init &lt;id&gt;", whose <c>Shutdown</c> logs
    /// "shutdown &lt;id&gt;", with <paramref name="ctorBody"/>, <paramref name="initBody"/> and
    /// <paramref name="shutdownBody"/> spliced in.
    /// </summary>
    public static string PluginSource(string ns, string ctorBody = "", string initBody = "", string shutdownBody = "", string extraMembers = "") => $$"""
        using EDNexus.Plugins.Abstractions;
        namespace {{ns}};
        public sealed class Plugin : IEDNexusPlugin
        {
            private IPluginContext? _context;
            public Plugin() { {{ctorBody}} }
            public void Initialize(IPluginContext context)
            {
                _context = context;
                context.Log.Info("init " + context.Manifest.Id);
                {{initBody}}
            }
            public void Shutdown()
            {
                _context?.Log.Info("shutdown " + _context.Manifest.Id);
                {{shutdownBody}}
            }
            {{extraMembers}}
        }
        """;

    /// <summary>
    /// A nested <c>EvilException</c> (splice into <c>extraMembers</c>) whose <c>Message</c>,
    /// <c>StackTrace</c> and <c>ToString</c> all throw — hostile to any host code that describes it.
    /// </summary>
    public const string EvilException = """
        public sealed class EvilException : System.Exception
        {
            public override string Message => throw new System.InvalidOperationException("Message getter");
            public override string? StackTrace => throw new System.InvalidOperationException("StackTrace getter");
            public override string ToString() => throw new System.InvalidOperationException("ToString");
        }
        """;

    /// <summary>The standard well-behaved plugin, as <c>&lt;ns&gt;.dll</c>.</summary>
    public static byte[] Standard(string ns) => Compile(ns, PluginSource(ns));

    public static string Manifest(
        string id,
        string entryAssembly,
        string entryType,
        string sdkVersion = PluginSdk.CurrentVersionString,
        string? minAppVersion = null,
        string capabilities = "\"events\"") => $$"""
        {
          "id": "{{id}}",
          "name": "Test plugin {{id}}",
          "version": "1.0.0",
          "sdkVersion": "{{sdkVersion}}",
          {{(minAppVersion is null ? "" : $"\"minAppVersion\": \"{minAppVersion}\",")}}
          "entryAssembly": "{{entryAssembly}}",
          "entryType": "{{entryType}}",
          "capabilities": [{{capabilities}}]
        }
        """;

    /// <summary>Writes <c>&lt;root&gt;/&lt;folder&gt;/plugin.json</c> plus <paramref name="files"/>.</summary>
    public static string WriteFolder(string root, string folder, string? manifestJson, params (string Name, byte[] Content)[] files)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        if (manifestJson is not null)
            File.WriteAllText(Path.Combine(dir, PluginManifestParser.FileName), manifestJson);
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
        return dir;
    }

    /// <summary>
    /// Installs the standard plugin in namespace <paramref name="ns"/> as id <paramref name="id"/>
    /// in folder <paramref name="folder"/> (default: the id).
    /// </summary>
    public static string WriteStandard(string root, string id, string ns, string? folder = null, string sdkVersion = PluginSdk.CurrentVersionString, string? minAppVersion = null, string capabilities = "\"events\"")
        => WriteFolder(root, folder ?? id, Manifest(id, ns + ".dll", ns + ".Plugin", sdkVersion, minAppVersion, capabilities), (ns + ".dll", Standard(ns)));
}

/// <summary>
/// Consent callbacks for tests. <see cref="AllowAll"/> exists only here: the host has no allow-all
/// default, so production wiring must make a real decision.
/// </summary>
internal static class Consent
{
    /// <summary>Grants every capability a plugin declares.</summary>
    public static readonly Func<PluginManifest, IReadOnlyCollection<string>?> AllowAll = manifest => manifest.Capabilities;

    /// <summary>Refuses every plugin.</summary>
    public static readonly Func<PluginManifest, IReadOnlyCollection<string>?> DenyAll = _ => null;
}

/// <summary>A context factory that records every log line (per plugin id), every dispose and each grant.</summary>
internal sealed class RecordingContexts
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ConcurrentQueue<string> Disposed { get; } = new();

    /// <summary>The capabilities each plugin's context was built with, by plugin id.</summary>
    public ConcurrentDictionary<string, string[]> Granted { get; } = new();

    public Func<PluginManifest, IReadOnlySet<string>, IPluginContext> Factory => (manifest, granted) =>
    {
        Granted[manifest.Id] = [.. granted.Order(StringComparer.Ordinal)];
        return new Context(manifest, this);
    };

    public PluginHost Host(string root, string appVersion = "1.0.0", Func<PluginManifest, IReadOnlyCollection<string>?>? consent = null)
        => new(root, SemanticVersion.Parse(appVersion), consent ?? Consent.AllowAll, Factory);

    private sealed class Context(PluginManifest manifest, RecordingContexts owner) : IPluginContext, IDisposable
    {
        public IReadOnlyCommanderState State { get; } = new NoState();
        public IPluginEvents Events { get; } = new NoEvents();
        public IPluginLog Log { get; } = new Recorder(owner.Lines);
        public IPluginStorage Storage { get; } = new NoStorage();
        public IUiRegistry Ui { get; } = new NoUi();
        public PluginManifest Manifest { get; } = manifest;
        public void Dispose() => owner.Disposed.Enqueue(Manifest.Id);
    }

    private sealed class Recorder(ConcurrentQueue<string> lines) : IPluginLog
    {
        public void Debug(string message) => lines.Enqueue(message);
        public void Info(string message) => lines.Enqueue(message);
        public void Warn(string message) => lines.Enqueue(message);
        public void Error(string message, Exception? exception = null) => lines.Enqueue(message);
    }

    private sealed class NoState : IReadOnlyCommanderState
    {
        public string? Name => null;
        public long Balance => 0;
        public string? Ship => null;
        public string? StarSystem => null;
        public bool Docked => false;
        public DateTimeOffset LastUpdated => DateTimeOffset.MinValue;
    }

    private sealed class NoEvents : IPluginEvents
    {
        public void Subscribe(string eventName, Action<IJournalEvent> handler) { }
        public void SubscribeAny(Action<IJournalEvent> handler) { }
    }

    private sealed class NoStorage : IPluginStorage
    {
        public string? GetString(string key) => null;
        public void SetString(string key, string value) { }
        public void Remove(string key) { }
    }

    private sealed class NoUi : IUiRegistry
    {
        public void Register(string id, object descriptor) { }
    }
}
