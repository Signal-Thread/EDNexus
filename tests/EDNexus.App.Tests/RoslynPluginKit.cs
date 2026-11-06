using EDNexus.Plugins.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EDNexus.App.Tests;

/// <summary>
/// Compiles tiny real plugin assemblies with Roslyn and lays them out as installed plugin folders,
/// so App-level tests can run code inside a genuine plugin load context.
/// </summary>
internal static class RoslynPluginKit
{
    /// <summary>Compiles <paramref name="source"/> into a DLL image that references the SDK contract.</summary>
    public static byte[] Compile(string assemblyName, string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p) is var name && (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(IEDNexusPlugin).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        if (!emitted.Success)
            throw new InvalidOperationException("test plugin failed to compile:\n" + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }

    /// <summary>Writes <c>&lt;root&gt;/&lt;id&gt;/plugin.json</c> and <c>&lt;ns&gt;.dll</c> (entry type <c>&lt;ns&gt;.Plugin</c>); returns the plugin folder.</summary>
    public static string Install(string root, string id, string ns, string source, params string[] capabilities)
    {
        if (capabilities.Length == 0) capabilities = [PluginCapabilities.Events];
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "Test {{id}}",
              "version": "1.0.0",
              "sdkVersion": "{{PluginSdk.CurrentVersionString}}",
              "entryAssembly": "{{ns}}.dll",
              "entryType": "{{ns}}.Plugin",
              "capabilities": [{{string.Join(",", capabilities.Select(c => $"\"{c}\""))}}]
            }
            """);
        File.WriteAllBytes(Path.Combine(dir, ns + ".dll"), Compile(ns, source));
        return dir;
    }

    /// <summary>A plugin whose <c>Shutdown</c> does nothing and whose <paramref name="members"/> are spliced into the class.</summary>
    public static string PluginSource(string ns, string members) => $$"""
        using EDNexus.Plugins.Abstractions;
        namespace {{ns}};
        public sealed class Plugin : IEDNexusPlugin
        {
            public void Initialize(IPluginContext context) { }
            public void Shutdown() { }
            {{members}}
        }
        """;
}
