using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// Regression tests for #260: a plugin's <c>&lt;entry&gt;.deps.json</c> is parsed by the runtime's
/// <em>native</em> host policy library when <see cref="System.Runtime.Loader.AssemblyDependencyResolver"/>
/// is created, and a document that is valid JSON but not shaped like SDK output (no
/// <c>runtimeTarget</c>, a library without <c>sha512</c>, a number where an object belongs, ...) aborts
/// the whole process with a native <c>std::logic_error</c> or a segmentation fault. No managed
/// <c>try/catch</c> can contain that, so the host vets the file first (<see cref="PluginDepsJson"/>) and
/// rejects the plugin.
/// <para>
/// Anything that would take the process down if the fix regressed is exercised in a child process,
/// never in the test host (see <see cref="ChildHost"/>); the child's exit code and output are asserted.
/// The in-process tests either call the pure validator or load documents the native parser merely
/// reports as an error. Every test has its own plugin id and nothing sleeps.
/// </para>
/// </summary>
public sealed class PluginDepsJsonTests : IDisposable, IClassFixture<PluginDepsJsonTests.ChildHost>
{
    private readonly TempDir _dir = new();
    private readonly ChildHost _child;

    public PluginDepsJsonTests(ChildHost child) => _child = child;

    public void Dispose() => _dir.Dispose();

    /// <summary>What the .NET SDK emits for a plugin project (trimmed), plus one package dependency of every asset kind.</summary>
    private static string SdkDocument(string name) => $$"""
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0", "signature": "" },
          "compilationOptions": {},
          "targets": {
            ".NETCoreApp,Version=v10.0": {
              "{{name}}/1.0.0": { "dependencies": { "Dep": "2.0.0" }, "runtime": { "{{name}}.dll": {} } },
              "Dep/2.0.0": {
                "runtime": { "lib/net10.0/Dep.dll": { "assemblyVersion": "2.0.0.0", "fileVersion": "2.0.0.0" } },
                "native": { "runtimes/linux-x64/native/libdep.so": { "rid": "linux-x64", "assetType": "native" } },
                "resources": { "lib/fr/Dep.resources.dll": { "locale": "fr" } },
                "runtimeTargets": { "runtimes/linux-x64/lib/net10.0/Dep.dll": { "rid": "linux-x64", "assetType": "runtime" } }
              }
            }
          },
          "libraries": {
            "{{name}}/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
            "Dep/2.0.0": { "type": "package", "serviceable": true, "sha512": "sha512-abc", "path": "dep/2.0.0", "hashPath": "dep.2.0.0.nupkg.sha512" }
          }
        }
        """;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string? Check(byte[] bytes) => PluginDepsJson.CheckContent(bytes);

    // ---- the validator ---------------------------------------------------------------------------

    [Fact]
    public void AnSdkShapedDocument_IsAccepted_WithOrWithoutAByteOrderMark()
    {
        Assert.Null(Check(Utf8(SdkDocument("Plug"))));
        Assert.Null(Check([0xEF, 0xBB, 0xBF, .. Utf8(SdkDocument("Plug"))]));
    }

    [Fact]
    public void ADocument_WhoseRuntimeTargetNamesNoTarget_IsAccepted_BecauseTheNativeParserCopesWithIt()
    {
        // Probed natively: this resolves nothing but does not crash, so rejecting it would be needless.
        var doc = SdkDocument("Plug").Replace("\"name\": \".NETCoreApp,Version=v10.0\", \"signature\"", "\"name\": \".NETCoreApp,Version=v99.0/linux-arm64\", \"signature\"");
        Assert.Null(Check(Utf8(doc)));
    }

    /// <summary>
    /// Documents that were observed to abort the process (SIGABRT from a native <c>std::logic_error</c>,
    /// or SIGSEGV) when handed to <c>AssemblyDependencyResolver</c> on .NET 10 / Linux. Each is valid JSON.
    /// </summary>
    public static TheoryData<string, string> CrashingDocuments => new()
    {
        { "empty-object", "{}" },
        { "wrong-schema", """{"hello":"world","targets":[1,2,3],"libraries":"nope"}""" },
        { "no-runtimeTarget", """{"targets":{},"libraries":{}}""" },
        { "runtimeTarget-null", """{"runtimeTarget":null,"targets":{}}""" },
        { "runtimeTarget-number", """{"runtimeTarget":1,"targets":{}}""" },
        { "runtimeTarget-no-name", """{"runtimeTarget":{},"targets":{}}""" },
        { "no-targets", """{"runtimeTarget":{"name":"x"}}""" },
        { "targets-number", """{"runtimeTarget":{"name":"x"},"targets":1}""" },
        { "targets-array", """{"runtimeTarget":{"name":"x"},"targets":[]}""" },
        { "tfm-number", """{"runtimeTarget":{"name":"x"},"targets":{"x":1}}""" },
        { "library-number", """{"runtimeTarget":{"name":"x"},"targets":{"x":{"p/1":1}}}""" },
        { "runtime-string", """{"runtimeTarget":{"name":"x"},"targets":{"x":{"p/1":{"runtime":"bad"}}}}""" },
        { "runtime-asset-number", """{"runtimeTarget":{"name":"x"},"targets":{"x":{"p/1":{"runtime":{"p.dll":1}}}}}""" },
        { "runtimeTargets-asset-null", """{"runtimeTarget":{"name":"x"},"targets":{"x":{"p/1":{"runtimeTargets":{"a.dll":null}}}}}""" },
        { "runtimeTargets-no-assetType", """{"runtimeTarget":{"name":"x"},"targets":{"x":{"p/1":{"runtimeTargets":{"a.dll":{"rid":"linux-x64"}}}}}}""" },
        { "runtimeTargets-no-rid", """{"runtimeTarget":{"name":"x"},"targets":{"x":{"p/1":{"runtimeTargets":{"a.dll":{"assetType":"runtime"}}}}}}""" },
        { "libraries-string", """{"runtimeTarget":{"name":"x"},"targets":{},"libraries":"s"}""" },
        { "libraries-number", """{"runtimeTarget":{"name":"x"},"targets":{},"libraries":1}""" },
        { "library-null", """{"runtimeTarget":{"name":"x"},"targets":{},"libraries":{"p/1":null}}""" },
        { "library-no-type", """{"runtimeTarget":{"name":"x"},"targets":{},"libraries":{"p/1":{"sha512":""}}}""" },
        { "library-no-sha512", """{"runtimeTarget":{"name":"x"},"targets":{},"libraries":{"p/1":{"type":"project"}}}""" },
        { "library-sha512-number", """{"runtimeTarget":{"name":"x"},"targets":{},"libraries":{"p/1":{"type":"project","sha512":1}}}""" },
    };

    [Theory]
    [MemberData(nameof(CrashingDocuments))]
    public void DocumentsThatCrashTheNativeParser_AreRefused(string name, string json)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.NotNull(Check(Utf8(json)));
    }

    public static TheoryData<string, byte[]> NotUsableAtAll => new()
    {
        { "empty", [] },
        { "whitespace", Utf8("  \n ") },
        { "bom-only", [0xEF, 0xBB, 0xBF] },
        { "truncated", Utf8(SdkDocument("Plug")[..200]) },
        { "array-root", Utf8("[1,2,3]") },
        { "string-root", Utf8("\"x\"") },
        { "binary-garbage", [.. Enumerable.Range(0, 512).Select(i => (byte)(i * 31 + 7))] },
        { "nul-bytes", new byte[256] },
        { "utf16", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(SdkDocument("Plug"))] },
        { "invalid-utf8-in-string", [.. Utf8("{\"runtimeTarget\":{\"name\":\""), 0xFF, 0xFE, 0xC0, 0x80, .. Utf8("\"}}")] },
        { "lone-surrogate-escape", Utf8(SdkDocument("Plug").Replace("\"Dep\": \"2.0.0\"", "\"\\ud800\": \"2.0.0\"")) },
        { "comment", Utf8(SdkDocument("Plug").Replace("{", "/* c */ {", StringComparison.Ordinal)) },
        { "trailing-comma", Utf8(SdkDocument("Plug").Replace("\"signature\": \"\"", "\"signature\": \"\",")) },
        { "duplicate-top-level-key", Utf8(SdkDocument("Plug").TrimEnd().TrimEnd('}') + ", \"libraries\": {} }") },
        { "duplicate-nested-key", Utf8(SdkDocument("Plug").Replace("\"serviceable\": false,", "\"serviceable\": false, \"serviceable\": true,")) },
        { "deep-array-nesting", Utf8(new string('[', 100_000) + new string(']', 100_000)) },
        { "deep-object-nesting", Utf8(string.Concat(Enumerable.Repeat("{\"a\":", 50_000)) + "1" + new string('}', 50_000)) },
        { "nesting-just-over-the-limit", Utf8(string.Concat(Enumerable.Repeat("{\"a\":", PluginDepsJson.MaxDepth + 1)) + "1" + new string('}', PluginDepsJson.MaxDepth + 1)) },
    };

    [Theory]
    [MemberData(nameof(NotUsableAtAll))]
    public void MalformedOrHostileText_IsRefused_WithoutThrowing(string name, byte[] bytes)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.NotNull(Check(bytes));
    }

    [Fact]
    public void TheReason_StaysShort_ForAHugeHostileName()
    {
        var doc = "{\"runtimeTarget\":{\"name\":\"x\"},\"targets\":{\"" + new string('A', 100_000) + "\":1}}";
        var reason = Check(Utf8(doc));
        Assert.NotNull(reason);
        Assert.True(reason!.Length < 600, $"reason is {reason.Length} chars");
    }

    [Theory]
    [InlineData("/p/Plug.dll", "/p/Plug.deps.json")]
    [InlineData("/p/Plug.Core.dll", "/p/Plug.Core.deps.json")]
    [InlineData("/p/Plug", "/p/Plug.deps.json")]
    [InlineData("/p/Plug.exe", "/p/Plug.deps.json")]
    public void TheFileVetted_IsTheOneTheResolverWouldRead(string entry, string expected)
        => Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar), PluginDepsJson.PathFor(entry.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void CheckFor_NoFile_IsFine_ADirectoryOrAnOversizedFile_IsNot()
    {
        var entry = Path.Combine(_dir.Path, "Plug.dll");
        Assert.Null(PluginDepsJson.CheckFor(entry));   // none: the resolver falls back to the folder

        var deps = PluginDepsJson.PathFor(entry);
        Directory.CreateDirectory(deps);
        Assert.Contains("folder", PluginDepsJson.CheckFor(entry));
        Directory.Delete(deps);

        using (var stream = File.Create(deps))
            stream.SetLength(PluginDepsJson.MaxBytes + 1L);
        Assert.Contains("larger than", PluginDepsJson.CheckFor(entry));

        File.WriteAllBytes(deps, Utf8(SdkDocument("Plug")));
        Assert.Null(PluginDepsJson.CheckFor(entry));
    }

    // ---- through the host: only documents the native parser survives, in-process ----------------

    private (PluginLoadResult Result, RecordingContexts Contexts) LoadWithDeps(string id, string ns, byte[]? deps, string ctorBody = "")
    {
        var files = new List<(string, byte[])> { (ns + ".dll", Compile(ns, PluginSource(ns, ctorBody: ctorBody))) };
        if (deps is not null)
            files.Add((ns + ".deps.json", deps));
        WriteFolder(_dir.Path, id, Manifest(id, ns + ".dll", ns + ".Plugin"), [.. files]);
        var contexts = new RecordingContexts();
        using var host = contexts.Host(_dir.Path);
        return (host.LoadPlugin(id), contexts);
    }

    [Fact]
    public void AValidDepsJson_StillLoadsThePlugin_AndASdkDocumentWithABomToo()
    {
        var (plain, _) = LoadWithDeps("com.test.deps.valid", "DepsValid", Utf8(SdkDocument("DepsValid")));
        var (bom, _) = LoadWithDeps("com.test.deps.bom", "DepsBom", [0xEF, 0xBB, 0xBF, .. Utf8(SdkDocument("DepsBom"))]);
        var (none, _) = LoadWithDeps("com.test.deps.none", "DepsNone", null);

        Assert.True(plain.Status == PluginLoadStatus.Loaded, plain.ReasonSummary);
        Assert.True(bom.Status == PluginLoadStatus.Loaded, bom.ReasonSummary);
        Assert.True(none.Status == PluginLoadStatus.Loaded, none.ReasonSummary);
    }

    [Theory]
    [InlineData("com.test.deps.empty", "DepsEmpty", "")]
    [InlineData("com.test.deps.truncated", "DepsTruncated", "{\"runtimeTarget\":{\"name\":")]
    [InlineData("com.test.deps.array", "DepsArray", "[1,2,3]")]
    public void ADepsJson_TheNativeParserOnlyReportsAsAnError_IsRejected_AndThePluginNeverRuns(string id, string ns, string json)
    {
        var key = "deps.ran." + id;
        var (result, contexts) = LoadWithDeps(id, ns, Utf8(json), ctorBody: $"System.AppDomain.CurrentDomain.SetData(\"{key}\", \"ctor ran\");");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains(".deps.json", result.ReasonSummary);
        Assert.Null(AppDomain.CurrentDomain.GetData(key));
        Assert.Empty(contexts.Lines);
        Assert.Empty(contexts.Disposed);   // no context was ever built
    }

    // ---- through the host: documents that kill the process, in a child process -------------------

    /// <summary>
    /// The control: with a valid <c>.deps.json</c> the child gets past the resolver all the way to the
    /// context factory (which throws on purpose). If this ever fails, the child harness is not
    /// exercising the resolver and the crash tests below prove nothing.
    /// </summary>
    [Fact]
    public void ChildProcess_Control_AValidDepsJson_GetsPastTheResolver()
    {
        var run = _child.Run(_dir.Path, "com.test.deps.ctl", "DepsCtl", SdkDocument("DepsCtl"));

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.Contains("context factory reached", run.Output);
        Assert.StartsWith("Failed|", run.FirstLine);
    }

    [Theory]
    [InlineData("com.test.deps.crash.abort", "DepsCrashAbort", """{"hello":"world","targets":[1,2,3],"libraries":"nope"}""")]
    [InlineData("com.test.deps.crash.sha", "DepsCrashSha", """{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0"},"targets":{".NETCoreApp,Version=v10.0":{}},"libraries":{"DepsCrashSha/1.0.0":{"type":"project"}}}""")]
    [InlineData("com.test.deps.crash.segv", "DepsCrashSegv", """{"runtimeTarget":1,"targets":{}}""")]
    [InlineData("com.test.deps.crash.rtargets", "DepsCrashRt", """{"runtimeTarget":{"name":"x"},"targets":{"x":{"p/1":{"runtimeTargets":{"a.dll":null}}}}}""")]
    public void ChildProcess_ADepsJsonThatWouldAbortTheProcess_IsRejected_AndTheHostSurvives(string id, string ns, string json)
    {
        var run = _child.Run(_dir.Path, id, ns, json);

        // A native abort/segfault shows up as a non-zero exit code and no status line.
        Assert.True(run.ExitCode == 0, "the host process died while loading a plugin with a malformed .deps.json:\n" + run.Describe());
        Assert.StartsWith("Rejected|", run.FirstLine);
        Assert.Contains("not a usable dependency manifest", run.FirstLine);
    }

    /// <summary>
    /// Compiles, once, a tiny console program that loads every plugin under a folder through a real
    /// <see cref="PluginHost"/> (granting everything) and prints one <c>Status|reasons</c> line per
    /// plugin, then runs it as a separate <c>dotnet</c> process so a native crash is observable
    /// instead of fatal to the test host.
    /// </summary>
    public sealed class ChildHost : IDisposable
    {
        private const string Source = """
            using System;
            using EDNexus.Plugins.Abstractions;
            using EDNexus.Plugins.Hosting;

            public static class ChildProgram
            {
                public static int Main(string[] args)
                {
                    var host = new PluginHost(
                        args[0],
                        SemanticVersion.Parse("1.0.0"),
                        request => request.Manifest.Capabilities,
                        (manifest, granted) => throw new InvalidOperationException("context factory reached"));
                    foreach (var plugin in host.LoadAll().Plugins)
                        Console.WriteLine(plugin.Status + "|" + string.Join(" ; ", plugin.Reasons));
                    return 0;
                }
            }
            """;

        private readonly string _baseName = "ednexus-depsjson-child-" + Guid.NewGuid().ToString("N");
        private readonly Lazy<string> _dll;

        public ChildHost() => _dll = new Lazy<string>(Build, LazyThreadSafetyMode.ExecutionAndPublication);

        // Beside the test assembly, so the child finds EDNexus.Plugins.Hosting and friends by probing its own folder.
        private string ChildFile(string extension) => Path.Combine(AppContext.BaseDirectory, _baseName + extension);

        private string Build()
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => Path.GetFileName(p) is var name
                    && (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("System.dll", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)))
                .Concat([typeof(PluginHost).Assembly.Location, typeof(EDNexus.Plugins.Abstractions.IEDNexusPlugin).Assembly.Location])
                .Concat(Directory.GetFiles(AppContext.BaseDirectory, "EDNexus.Core.dll"))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

            var compilation = CSharpCompilation.Create(
                _baseName,
                [CSharpSyntaxTree.ParseText(Source)],
                references,
                new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
            var dll = ChildFile(".dll");
            var emitted = compilation.Emit(dll);
            if (!emitted.Success)
                throw new InvalidOperationException("child host failed to compile:\n" + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

            // Same runtime selection as the test host; with no deps.json of its own the child probes its folder.
            File.Copy(Path.Combine(AppContext.BaseDirectory, "EDNexus.Plugins.Hosting.Tests.runtimeconfig.json"), ChildFile(".runtimeconfig.json"), overwrite: true);
            return dll;
        }

        /// <summary>Installs <paramref name="depsJson"/> beside a standard plugin in a fresh root under <paramref name="scratch"/> and loads it in a child process.</summary>
        public ChildRun Run(string scratch, string id, string ns, string depsJson)
        {
            var root = Path.Combine(scratch, "child-" + id);
            WriteFolder(root, id, Manifest(id, ns + ".dll", ns + ".Plugin"),
                (ns + ".dll", Standard(ns)),
                (ns + ".deps.json", Encoding.UTF8.GetBytes(depsJson)));

            var start = new ProcessStartInfo(DotnetHost())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(_dll.Value);
            start.ArgumentList.Add(root);
            start.Environment["DOTNET_NOLOGO"] = "1";

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(120)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.WaitForExit();
                return new ChildRun(-1, stdout.Result, "timed out after 120 s\n" + stderr.Result);
            }
            process.WaitForExit();   // flush the async readers
            return new ChildRun(process.ExitCode, stdout.Result, stderr.Result);
        }

        private static string DotnetHost()
        {
            var current = Environment.ProcessPath;
            if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                return current;
            var fromSdk = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrEmpty(fromSdk) && File.Exists(fromSdk))
                return fromSdk;
            // <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet
            var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        }

        public void Dispose()
        {
            foreach (var extension in new[] { ".dll", ".runtimeconfig.json" })
            {
                try { File.Delete(ChildFile(extension)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>The outcome of one child run.</summary>
    public sealed record ChildRun(int ExitCode, string Output, string Error)
    {
        public string FirstLine => Output.Split('\n', 2)[0].TrimEnd('\r');

        public string Describe() => $"exit code {ExitCode}\nstdout:\n{Output}\nstderr:\n{Error}";
    }
}
