using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using EDNexus.Plugins.Abstractions;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

public class PluginHostTests
{
    private static PluginLoadResult Single(PluginDiscoveryReport report, string folder)
        => Assert.Single(report.Plugins, p => p.FolderName == folder);

    // ---- discovery & the happy path ---------------------------------------------------------

    [Fact]
    public void LoadAll_NoPluginsRoot_LoadsNothingAndReportsNothing()
    {
        using var dir = new TempDir();
        var contexts = new RecordingContexts();
        using var host = contexts.Host(Path.Combine(dir.Path, "does-not-exist"));

        var report = host.LoadAll();

        Assert.Empty(report.Plugins);
        Assert.Empty(report.Errors);
        Assert.Empty(host.Loaded);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "does-not-exist")));
    }

    [Fact]
    public void LoadAll_ValidPlugin_LoadsIntoItsOwnCollectibleContextAndInitialises()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var report = host.LoadAll();

        var result = Single(report, "com.test.alpha");
        Assert.Equal(PluginLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Reasons);
        Assert.Contains("init com.test.alpha", contexts.Lines);
        var plugin = Assert.Single(host.Loaded);
        Assert.Same(result.Plugin, plugin);
        Assert.Equal(Path.Combine(dir.Path, "com.test.alpha", "Alpha.dll"), plugin.EntryAssemblyPath);

        var loadContext = AssemblyLoadContext.GetLoadContext(plugin.Instance!.GetType().Assembly)!;
        Assert.NotSame(AssemblyLoadContext.Default, loadContext);
        Assert.True(loadContext.IsCollectible);
        Assert.Equal(plugin.LoadContextName, loadContext.Name);
    }

    [Fact]
    public void LoadAll_ContractTypesUnifyWithTheHost_EvenWhenThePluginBundlesItsOwnSdkCopy()
    {
        using var dir = new TempDir();
        var bundledSdk = File.ReadAllBytes(typeof(IEDNexusPlugin).Assembly.Location);
        WriteFolder(dir.Path, "com.test.bundled", Manifest("com.test.bundled", "Bundled.dll", "Bundled.Plugin"),
            ("Bundled.dll", Standard("Bundled")), (AbstractionsFileName, bundledSdk));
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.bundled");

        Assert.Equal(PluginLoadStatus.Loaded, result.Status);
        var instance = result.Plugin!.Instance!;
        Assert.IsAssignableFrom<IEDNexusPlugin>(instance);
        var pluginContext = AssemblyLoadContext.GetLoadContext(instance.GetType().Assembly)!;
        // The plugin's IEDNexusPlugin is the host's: the bundled copy was never loaded.
        var implemented = Assert.Single(instance.GetType().GetInterfaces());
        Assert.Same(typeof(IEDNexusPlugin), implemented);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(implemented.Assembly));
        Assert.DoesNotContain(pluginContext.Assemblies, a => a.GetName().Name == "EDNexus.Plugins.Abstractions");
    }

    [Fact]
    public void LoadAll_SkipsDotFoldersLeftByTheInstaller()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha", folder: ".hidden");
        WriteStandard(dir.Path, "com.test.alpha", "Alpha", folder: ".git");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var report = host.LoadAll();

        Assert.Empty(report.Plugins);
        Assert.Empty(contexts.Lines);
    }

    [Fact]
    public void LoadAll_RunsInstallRecoveryBeforeDiscovery()
    {
        using var dir = new TempDir();
        // A replace interrupted between its two renames: only the backup of the previous version is
        // on disk, plus the half-written staging folder of the new one.
        WriteStandard(dir.Path, "com.test.alpha", "Alpha", folder: PluginInstaller.BackupName("com.test.alpha"));
        WriteStandard(dir.Path, "com.test.beta", "Beta", folder: ".staging-" + Guid.NewGuid().ToString("N"));
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var report = host.LoadAll();

        Assert.Equal(["com.test.alpha"], report.Recovery.Restored);
        Assert.Contains(report.Recovery.Removed, name => name.StartsWith(".staging-", StringComparison.Ordinal));
        Assert.Empty(report.Errors);
        // The restored plugin was discovered and loaded in the same pass; the staging folder was not.
        var result = Assert.Single(report.Plugins);
        Assert.Equal("com.test.alpha", result.FolderName);
        Assert.Equal(PluginLoadStatus.Loaded, result.Status);
        Assert.Equal(["init com.test.alpha"], contexts.Lines);
    }

    [Fact]
    public void LoadAll_BadPluginsDoNotStopGoodOnes()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        WriteFolder(dir.Path, "com.test.broken", "{ not json");
        WriteFolder(dir.Path, "com.test.throws", Manifest("com.test.throws", "Throws.dll", "Throws.Plugin"),
            ("Throws.dll", Compile("Throws", PluginSource("Throws", ctorBody: "throw new System.InvalidOperationException(\"boom\");"))));
        WriteStandard(dir.Path, "com.test.zulu", "Zulu");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var report = host.LoadAll();

        Assert.Equal(
            ["com.test.alpha", "com.test.broken", "com.test.throws", "com.test.zulu"],
            report.Plugins.Select(p => p.FolderName));
        Assert.Equal(["com.test.alpha", "com.test.zulu"], host.Loaded.Select(p => p.Id));
        Assert.Equal(["com.test.alpha", "com.test.zulu"], report.Loaded.Select(p => p.Id));
    }

    // ---- rejected: static problems, no plugin code runs ------------------------------------

    [Fact]
    public void LoadAll_FolderWithoutManifest_IsRejected()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.empty", manifestJson: null);
        using var host = new RecordingContexts().Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.empty");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains("no plugin.json", result.ReasonSummary);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void LoadAll_InvalidManifest_IsRejectedWithTheParserReasons()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.bad", """{ "id": "com.test.bad", "name": "Bad" }""");
        using var host = new RecordingContexts().Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.bad");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains(result.Reasons, r => r.StartsWith("invalid manifest:", StringComparison.Ordinal) && r.Contains("'version'"));
    }

    [Fact]
    public void LoadAll_FolderNameNotMatchingId_IsRejectedWithoutRunningCode()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha", folder: "com.test.renamed");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.renamed");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains("does not match plugin id 'com.test.alpha'", result.ReasonSummary);
        Assert.Empty(contexts.Lines);
        Assert.Empty(host.Loaded);
    }

    [Fact]
    public void LoadAll_DuplicateIds_RejectsEveryClaimant()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        WriteStandard(dir.Path, "com.test.alpha", "Alpha", folder: "com.test.alpha-copy");
        WriteStandard(dir.Path, "com.test.beta", "Beta");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var report = host.LoadAll();

        foreach (var folder in new[] { "com.test.alpha", "com.test.alpha-copy" })
        {
            var result = Single(report, folder);
            Assert.Equal(PluginLoadStatus.Rejected, result.Status);
            Assert.Contains(result.Reasons, r => r.Contains("declared by more than one folder")
                && r.Contains("'com.test.alpha'") && r.Contains("'com.test.alpha-copy'"));
        }
        Assert.Equal(["com.test.beta"], host.Loaded.Select(p => p.Id));
        Assert.Equal(["init com.test.beta"], contexts.Lines);
    }

    [Fact]
    public void LoadAll_MissingEntryAssembly_IsRejected()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.missing", Manifest("com.test.missing", "lib/Missing.dll", "Missing.Plugin"));
        using var host = new RecordingContexts().Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.missing");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains("'lib/Missing.dll' was not found", result.ReasonSummary);
    }

    [Theory]
    [InlineData("../com.test.victim/Victim.dll")]
    [InlineData("lib/../../com.test.victim/Victim.dll")]
    [InlineData("..\\com.test.victim\\Victim.dll")]
    [InlineData("/etc/Victim.dll")]
    [InlineData("C:/Victim.dll")]
    public void LoadAll_EntryAssemblyOutsideThePluginFolder_IsRejected(string entryAssembly)
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.victim", "Victim");
        WriteFolder(dir.Path, "com.test.escape", Manifest("com.test.escape", entryAssembly.Replace("\\", "\\\\"), "Victim.Plugin"));
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.escape");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains(result.Reasons, r => r.Contains("entryAssembly"));
        Assert.Equal(["init com.test.victim"], contexts.Lines); // only the victim's own load
    }

    [Theory]
    [InlineData("../Victim.dll")]
    [InlineData("lib/../../Victim.dll")]
    [InlineData("..\\Victim.dll")]
    [InlineData("/abs/Victim.dll")]
    [InlineData("C:/Victim.dll")]
    [InlineData("")]
    public void ResolveEntryAssembly_RefusesPathsThatLeaveTheFolder_EvenForHandBuiltManifests(string entryAssembly)
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "Victim.dll"), [1]);
        var pluginDir = Path.Combine(dir.Path, "com.test.escape");
        Directory.CreateDirectory(Path.Combine(pluginDir, "lib"));

        var reason = PluginHost.ResolveEntryAssembly(pluginDir, entryAssembly, out var fullPath);

        Assert.NotNull(reason);
        Assert.Null(fullPath);
    }

    [Fact]
    public void ResolveEntryAssembly_NestedPathInsideTheFolder_Resolves()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "lib", "net10.0", "Entry.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [1]);

        Assert.Null(PluginHost.ResolveEntryAssembly(dir.Path, "lib/net10.0/Entry.dll", out var fullPath));
        Assert.Equal(Path.GetFullPath(file), fullPath);
    }

    [Fact]
    public void LoadAll_EntryAssemblyThatIsNotDotNet_IsRejected()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.native", Manifest("com.test.native", "Native.dll", "Native.Plugin"),
            ("Native.dll", [0x4D, 0x5A, 1, 2, 3, 4, 5, 6]));
        using var host = new RecordingContexts().Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.native");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains("not a valid .NET assembly", result.ReasonSummary);
    }

    [Fact]
    public void LoadAll_EntryTypeMissing_IsRejected()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.typo", Manifest("com.test.typo", "Typo.dll", "Typo.Plugn"), ("Typo.dll", Standard("Typo")));
        using var host = new RecordingContexts().Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.typo");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains("entry type 'Typo.Plugn' was not found", result.ReasonSummary);
    }

    [Fact]
    public void LoadAll_EntryTypeNotImplementingTheContract_IsRejectedWithoutConstructingIt()
    {
        using var dir = new TempDir();
        var source = """
            namespace Wrong;
            public sealed class Plugin
            {
                public Plugin() => throw new System.InvalidOperationException("must never be constructed");
            }
            """;
        WriteFolder(dir.Path, "com.test.wrong", Manifest("com.test.wrong", "Wrong.dll", "Wrong.Plugin"), ("Wrong.dll", Compile("Wrong", source)));
        using var host = new RecordingContexts().Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.wrong");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains("does not implement EDNexus.Plugins.Abstractions.IEDNexusPlugin", result.ReasonSummary);
    }

    [Fact]
    public void LoadAll_EntryTypeImplementingALookalikeContract_IsRejectedAndSaysSo()
    {
        using var dir = new TempDir();
        // The plugin declares its own IEDNexusPlugin with the SDK's full name instead of referencing the SDK.
        var source = """
            namespace EDNexus.Plugins.Abstractions { public interface IEDNexusPlugin { void Shutdown(); } }
            namespace Fake { public sealed class Plugin : EDNexus.Plugins.Abstractions.IEDNexusPlugin { public void Shutdown() { } } }
            """;
        WriteFolder(dir.Path, "com.test.fake", Manifest("com.test.fake", "Fake.dll", "Fake.Plugin"),
            ("Fake.dll", Compile("Fake", source, referenceAbstractions: false)));
        using var host = new RecordingContexts().Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.fake");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains("implements a different EDNexus.Plugins.Abstractions.IEDNexusPlugin", result.ReasonSummary);
    }

    [Fact]
    public void LoadAll_EntryTypeWithoutParameterlessConstructor_IsRejected()
    {
        using var dir = new TempDir();
        var source = PluginSource("NoCtor", extraMembers: "public Plugin(int x) { }").Replace("public Plugin() {  }", "");
        WriteFolder(dir.Path, "com.test.noctor", Manifest("com.test.noctor", "NoCtor.dll", "NoCtor.Plugin"), ("NoCtor.dll", Compile("NoCtor", source)));
        using var host = new RecordingContexts().Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.noctor");

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains("no public parameterless constructor", result.ReasonSummary);
    }

    // ---- incompatible ----------------------------------------------------------------------

    [Theory]
    [InlineData("3.0", null, "targets SDK 3.0")]
    [InlineData("2.1", null, "targets SDK 2.1")]
    [InlineData("1.0", null, "targets SDK 1.0")]   // built before 2.0 removed IJournalEvent.Deserialize<T>
    [InlineData("0.9", null, "targets SDK 0.9")]
    [InlineData("2.0", "99.0.0", "requires EDNexus 99.0.0")]
    public void LoadAll_IncompatiblePlugin_IsSkippedWithAReasonAndOthersStillLoad(string sdkVersion, string? minAppVersion, string expected)
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.future", "Future", sdkVersion: sdkVersion, minAppVersion: minAppVersion);
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path, appVersion: "1.4.0");

        var report = host.LoadAll();

        var result = Single(report, "com.test.future");
        Assert.Equal(PluginLoadStatus.Incompatible, result.Status);
        Assert.Contains(expected, result.ReasonSummary);
        Assert.Equal(["init com.test.alpha"], contexts.Lines);
    }

    [Fact]
    public void LoadAll_GatesAgainstTheHostSdkVersion()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha", sdkVersion: "2.3");
        var contexts = new RecordingContexts();
        using var host = new PluginHost(dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, contexts.Factory) { HostSdkVersion = new Version(2, 3) };

        Assert.Equal(PluginLoadStatus.Loaded, Single(host.LoadAll(), "com.test.alpha").Status);
    }

    // ---- failed: plugin code threw ---------------------------------------------------------

    [Fact]
    public void LoadAll_ConstructorThrows_IsFailedWithTheMessage()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.throws", Manifest("com.test.throws", "Throws.dll", "Throws.Plugin"),
            ("Throws.dll", Compile("Throws", PluginSource("Throws", ctorBody: "throw new System.InvalidOperationException(\"boom\");"))));
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.throws");

        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Equal("constructing the entry type failed: System.InvalidOperationException: boom", Assert.Single(result.Reasons));
        Assert.Null(result.Plugin);
        Assert.Empty(contexts.Disposed); // no context was ever built
    }

    [Fact]
    public void LoadAll_InitializeThrows_IsFailedAndItsContextIsDisposed()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.initfail", Manifest("com.test.initfail", "InitFail.dll", "InitFail.Plugin"),
            ("InitFail.dll", Compile("InitFail", PluginSource("InitFail", initBody: "throw new System.IO.IOException(\"disk on fire\");"))));
        WriteStandard(dir.Path, "com.test.zulu", "Zulu");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var report = host.LoadAll();

        var result = Single(report, "com.test.initfail");
        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Equal("Initialize failed: System.IO.IOException: disk on fire", Assert.Single(result.Reasons));
        Assert.Equal(["com.test.initfail"], contexts.Disposed);
        // Best-effort Shutdown ran before the context was disposed.
        Assert.Contains("shutdown com.test.initfail", contexts.Lines);
        Assert.Equal(["com.test.zulu"], host.Loaded.Select(p => p.Id));
    }

    [Fact]
    public void LoadAll_PluginThatFailsToInitialise_DoesNotLeakItsLoadContext()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.leakcheck", Manifest("com.test.leakcheck", "LeakCheck.dll", "LeakCheck.Plugin"),
            ("LeakCheck.dll", Compile("LeakCheck", PluginSource("LeakCheck", initBody: "throw new System.InvalidOperationException(\"nope\");"))));
        using var host = new RecordingContexts().Host(dir.Path);

        Assert.Equal(PluginLoadStatus.Failed, Single(host.LoadAll(), "com.test.leakcheck").Status);

        for (var i = 0; i < 20 && AssemblyLoadContext.All.Any(c => c.Name == "EDNexus plugin com.test.leakcheck"); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name == "EDNexus plugin com.test.leakcheck");
    }

    [Fact]
    public void LoadAll_ContextFactoryThrows_IsFailed()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        using var host = new PluginHost(dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, (_, _) => throw new InvalidOperationException("no bridge"));

        var result = Single(host.LoadAll(), "com.test.alpha");

        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Contains("building the plugin context failed: System.InvalidOperationException: no bridge", result.ReasonSummary);
    }

    [Fact]
    public void LoadAll_PluginExceptionMessages_AreSanitisedForDisplay()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.ansi", Manifest("com.test.ansi", "Ansi.dll", "Ansi.Plugin"),
            ("Ansi.dll", Compile("Ansi", PluginSource("Ansi", ctorBody: "throw new System.Exception(\"\\u001b[31mred\\u202Etxt.exe\\r\\nFAKE LOG LINE\");"))));
        using var host = new RecordingContexts().Host(dir.Path);

        var reason = Assert.Single(Single(host.LoadAll(), "com.test.ansi").Reasons);

        Assert.DoesNotContain(reason, c => char.IsControl(c) || c == '\u202E');
        Assert.EndsWith("?[31mred?txt.exe??FAKE LOG LINE", reason);
    }

    [Theory]
    [InlineData(null, 10, "")]
    [InlineData("plain", 10, "plain")]
    [InlineData("0123456789abc", 10, "0123456789…")]
    [InlineData("a\u0000b\u200Bc\u001bd", 10, "a?b?c?d")]
    [InlineData("🚀 ok", 10, "🚀 ok")]
    public void ForDisplay_ReplacesInvisibleCharactersAndTruncates(string? text, int max, string expected)
        => Assert.Equal(expected, TextRules.ForDisplay(text, max));

    [Fact]
    public void ForDisplay_ReplacesUnpairedSurrogates()
    {
        // Built at run time: a lone surrogate does not survive as an attribute argument.
        var text = "a" + (char)0xD800 + "b" + (char)0xDC00;
        Assert.Equal("a?b?", TextRules.ForDisplay(text));
    }

    // ---- isolation ------------------------------------------------------------------------

    [Fact]
    public void LoadAll_TwoPluginsBundlingDifferentVersionsOfTheSameDependency_EachGetsItsOwn()
    {
        using var dir = new TempDir();
        byte[] Dep(string version) => Compile("Test.Dep", $$"""
            [assembly: System.Reflection.AssemblyVersion("{{version}}")]
            namespace Test.Dep;
            public static class DepInfo { public static string Value => "dep {{version}}"; }
            """, referenceAbstractions: false);
        var depV1 = Dep("1.0.0.0");
        var depV2 = Dep("2.0.0.0");
        string UsesDep(string ns) => PluginSource(ns, initBody: "context.Log.Info(context.Manifest.Id + \" uses \" + Test.Dep.DepInfo.Value);");
        WriteFolder(dir.Path, "com.test.one", Manifest("com.test.one", "One.dll", "One.Plugin"),
            ("One.dll", Compile("One", UsesDep("One"), true, depV1)), ("Test.Dep.dll", depV1));
        WriteFolder(dir.Path, "com.test.two", Manifest("com.test.two", "Two.dll", "Two.Plugin"),
            ("Two.dll", Compile("Two", UsesDep("Two"), true, depV2)), ("Test.Dep.dll", depV2));
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var report = host.LoadAll();

        Assert.All(report.Plugins, p => Assert.Equal(PluginLoadStatus.Loaded, p.Status));
        Assert.Contains("com.test.one uses dep 1.0.0.0", contexts.Lines);
        Assert.Contains("com.test.two uses dep 2.0.0.0", contexts.Lines);
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == "Test.Dep");
    }

    // ---- unload ---------------------------------------------------------------------------

    [Fact]
    public void Unload_CallsShutdownDisposesTheContextAndTheLoadContextIsCollected()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var (weak, plugin) = LoadThenUnload(host, "com.test.alpha");

        Assert.Contains("shutdown com.test.alpha", contexts.Lines);
        Assert.Equal(["com.test.alpha"], contexts.Disposed);
        Assert.Null(plugin.Instance);
        Assert.False(plugin.IsLoaded);
        Assert.Empty(host.Loaded);
        Assert.Null(host.Unload("com.test.alpha")); // already gone
        AssertCollected(weak);
    }

    [Fact]
    public void Unload_ShutdownThrows_IsReportedAndThePluginStillUnloads()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.grumpy", Manifest("com.test.grumpy", "Grumpy.dll", "Grumpy.Plugin"),
            ("Grumpy.dll", Compile("Grumpy", PluginSource("Grumpy", shutdownBody: "throw new System.InvalidOperationException(\"no\");"))));
        using var host = new RecordingContexts().Host(dir.Path);
        host.LoadAll();

        var result = UnloadOnly(host, "com.test.grumpy");

        Assert.Equal("Shutdown threw: System.InvalidOperationException: no", Assert.Single(result.Errors));
        Assert.Empty(host.Loaded);
        AssertCollected(result.LoadContext);
    }

    [Fact]
    public void UnloadAll_ThenLoadAll_LoadsAgain()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);
        host.LoadAll();

        Assert.Throws<InvalidOperationException>(() => host.LoadAll());
        Assert.Single(host.UnloadAll());
        host.LoadAll();

        Assert.Equal(["init com.test.alpha", "shutdown com.test.alpha", "init com.test.alpha"], contexts.Lines);
        Assert.Single(host.Loaded);
    }

    [Fact]
    public void Dispose_UnloadsEverything()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        WriteStandard(dir.Path, "com.test.beta", "Beta");
        var contexts = new RecordingContexts();
        var host = contexts.Host(dir.Path);
        host.LoadAll();

        host.Dispose();

        Assert.Empty(host.Loaded);
        Assert.Contains("shutdown com.test.alpha", contexts.Lines);
        Assert.Contains("shutdown com.test.beta", contexts.Lines);
        Assert.Throws<ObjectDisposedException>(() => host.LoadAll());
    }

    // ---- hostile exceptions (Message / ToString throw) -------------------------------------

    [Fact]
    public void LoadAll_ExceptionWhoseMessageThrows_FromConstructorOrInitialize_IsContained()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.evilctor", Manifest("com.test.evilctor", "EvilCtor.dll", "EvilCtor.Plugin"),
            ("EvilCtor.dll", Compile("EvilCtor", PluginSource("EvilCtor", ctorBody: "throw new EvilException();", extraMembers: EvilException))));
        WriteFolder(dir.Path, "com.test.evilinit", Manifest("com.test.evilinit", "EvilInit.dll", "EvilInit.Plugin"),
            ("EvilInit.dll", Compile("EvilInit", PluginSource("EvilInit", initBody: "throw new EvilException();", extraMembers: EvilException))));
        WriteStandard(dir.Path, "com.test.zulu", "Zulu");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var report = host.LoadAll(); // must not throw

        Assert.Same(report, host.LastReport);
        Assert.Equal(
            "constructing the entry type failed: EvilCtor.Plugin+EvilException: <message unavailable: EvilCtor.Plugin+EvilException>",
            Assert.Single(Single(report, "com.test.evilctor").Reasons));
        var init = Single(report, "com.test.evilinit");
        Assert.Equal(PluginLoadStatus.Failed, init.Status);
        Assert.Equal(
            "Initialize failed: EvilInit.Plugin+EvilException: <message unavailable: EvilInit.Plugin+EvilException>",
            Assert.Single(init.Reasons));
        Assert.Contains("shutdown com.test.evilinit", contexts.Lines);
        Assert.Equal(["com.test.evilinit"], contexts.Disposed);
        Assert.Equal(["com.test.zulu"], host.Loaded.Select(p => p.Id));
        AssertLoadContextsGone("com.test.evilctor", "com.test.evilinit");
    }

    [Fact]
    public void LoadAll_EvilExceptionFromShutdownAfterFailedInitialize_IsContained()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.doubleevil", Manifest("com.test.doubleevil", "DoubleEvil.dll", "DoubleEvil.Plugin"),
            ("DoubleEvil.dll", Compile("DoubleEvil", PluginSource("DoubleEvil",
                initBody: "throw new EvilException();", shutdownBody: "throw new EvilException();", extraMembers: EvilException))));
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.doubleevil");

        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Equal(2, result.Reasons.Count);
        Assert.StartsWith("Shutdown threw: DoubleEvil.Plugin+EvilException: <message unavailable", result.Reasons[1]);
        Assert.Equal(["com.test.doubleevil"], contexts.Disposed);
        AssertLoadContextsGone("com.test.doubleevil");
    }

    [Fact]
    public void UnloadAll_ExceptionWhoseMessageThrows_FromShutdown_StillUnloadsEveryPlugin()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        WriteFolder(dir.Path, "com.test.evilshut", Manifest("com.test.evilshut", "EvilShut.dll", "EvilShut.Plugin"),
            ("EvilShut.dll", Compile("EvilShut", PluginSource("EvilShut", shutdownBody: "throw new EvilException();", extraMembers: EvilException))));
        WriteStandard(dir.Path, "com.test.zulu", "Zulu");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);
        Assert.Equal(3, LoadCount(host));

        var results = host.UnloadAll(); // must not throw

        Assert.Equal(["com.test.alpha", "com.test.evilshut", "com.test.zulu"], results.Select(r => r.Id));
        Assert.Empty(results[0].Errors);
        Assert.Equal(
            "Shutdown threw: EvilShut.Plugin+EvilException: <message unavailable: EvilShut.Plugin+EvilException>",
            Assert.Single(results[1].Errors));
        Assert.Empty(results[2].Errors);
        Assert.Contains("shutdown com.test.alpha", contexts.Lines);
        Assert.Contains("shutdown com.test.zulu", contexts.Lines);
        Assert.Equal(["com.test.alpha", "com.test.evilshut", "com.test.zulu"], contexts.Disposed.Order());
        Assert.Empty(host.Loaded);
        Assert.Empty(host.LastReport!.Loaded);
        AssertLoadContextsGone("com.test.alpha", "com.test.evilshut", "com.test.zulu");
    }

    [Fact]
    public void Dispose_ExceptionWhoseMessageThrows_FromShutdown_DoesNotThrow()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.evildispose", Manifest("com.test.evildispose", "EvilDispose.dll", "EvilDispose.Plugin"),
            ("EvilDispose.dll", Compile("EvilDispose", PluginSource("EvilDispose", shutdownBody: "throw new EvilException();", extraMembers: EvilException))));
        WriteStandard(dir.Path, "com.test.zulu", "Zulu");
        var contexts = new RecordingContexts();
        var host = contexts.Host(dir.Path);
        Assert.Equal(2, LoadCount(host));

        host.Dispose();

        Assert.Equal(["com.test.evildispose", "com.test.zulu"], contexts.Disposed.Order());
        AssertLoadContextsGone("com.test.evildispose", "com.test.zulu");
    }

    private const string ThrowOnUnloading =
        "System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(GetType().Assembly)!.Unloading += _ => throw new EvilException();";

    [Fact]
    public void UnloadAll_UnloadingHandlerThatThrows_IsContainedAndEveryPluginStillUnloads()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        WriteFolder(dir.Path, "com.test.evilunload", Manifest("com.test.evilunload", "EvilUnload.dll", "EvilUnload.Plugin"),
            ("EvilUnload.dll", Compile("EvilUnload", PluginSource("EvilUnload", initBody: ThrowOnUnloading, extraMembers: EvilException))));
        WriteStandard(dir.Path, "com.test.zulu", "Zulu");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);
        Assert.Equal(3, LoadCount(host));

        var results = host.UnloadAll(); // must not throw

        Assert.Equal(["com.test.alpha", "com.test.evilunload", "com.test.zulu"], results.Select(r => r.Id));
        Assert.Equal(
            "unloading the load context failed: EvilUnload.Plugin+EvilException: <message unavailable: EvilUnload.Plugin+EvilException>",
            Assert.Single(results[1].Errors));
        Assert.Empty(results[2].Errors);
        Assert.Contains("shutdown com.test.evilunload", contexts.Lines);
        Assert.Contains("shutdown com.test.zulu", contexts.Lines);
        Assert.Equal(["com.test.alpha", "com.test.evilunload", "com.test.zulu"], contexts.Disposed.Order());
        Assert.Empty(host.Loaded);
        AssertLoadContextsGone("com.test.alpha", "com.test.evilunload", "com.test.zulu");
    }

    [Fact]
    public void LoadAll_UnloadingHandlerThatThrowsDuringAFailedLoad_KeepsTheRealReason()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.evilfail", Manifest("com.test.evilfail", "EvilFail.dll", "EvilFail.Plugin"),
            ("EvilFail.dll", Compile("EvilFail", PluginSource("EvilFail",
                initBody: ThrowOnUnloading + " throw new System.IO.IOException(\"init failed\");", extraMembers: EvilException))));
        WriteStandard(dir.Path, "com.test.zulu", "Zulu");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var report = host.LoadAll(); // must not throw

        var result = Single(report, "com.test.evilfail");
        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Equal(
            [
                "Initialize failed: System.IO.IOException: init failed",
                "unloading the load context failed: EvilFail.Plugin+EvilException: <message unavailable: EvilFail.Plugin+EvilException>",
            ],
            result.Reasons);
        Assert.Equal(["com.test.evilfail"], contexts.Disposed);
        Assert.Equal(["com.test.zulu"], host.Loaded.Select(p => p.Id));
        AssertLoadContextsGone("com.test.evilfail");
    }

    [Fact]
    public void Describe_NeverThrows()
    {
        Assert.Equal("x: EDNexus.Plugins.Hosting.Tests.PluginHostTests+ThrowingMessage: <message unavailable: EDNexus.Plugins.Hosting.Tests.PluginHostTests+ThrowingMessage>",
            PluginHost.Describe("x", new ThrowingMessage()));
        Assert.Equal("x: System.InvalidOperationException: inner",
            PluginHost.Describe("x", new System.Reflection.TargetInvocationException(new InvalidOperationException("inner"))));
    }

    private sealed class ThrowingMessage : Exception
    {
        public override string Message => throw new InvalidOperationException();
        public override string ToString() => throw new InvalidOperationException();
    }

    // ---- recovery runs once; re-entrancy ---------------------------------------------------

    [Fact]
    public void LoadAll_RunsRecoveryOnlyOncePerHost()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);
        var first = host.LoadAll();
        var staging = Directory.CreateDirectory(Path.Combine(dir.Path, ".staging-" + Guid.NewGuid().ToString("N"))).FullName;

        host.UnloadAll();
        var second = host.LoadAll();

        Assert.Same(first.Recovery, host.Recovery);
        Assert.Empty(second.Recovery.Removed);
        Assert.Empty(second.Recovery.Restored);
        Assert.True(Directory.Exists(staging), "recovery must not rerun (it may race an install)");
        Assert.Equal(PluginLoadStatus.Loaded, Single(second, "com.test.alpha").Status);
    }

    [Fact]
    public void LoadAll_CalledReentrantlyDuringAPass_FailsFastWithoutBreakingThePass()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        WriteStandard(dir.Path, "com.test.zulu", "Zulu");
        var contexts = new RecordingContexts();
        PluginHost? host = null;
        host = new PluginHost(dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, (manifest, granted) =>
        {
            if (manifest.Id == "com.test.alpha")
                host!.LoadAll();
            return contexts.Factory(manifest, granted);
        });
        using (host)
        {
            var report = host.LoadAll();

            var alpha = Single(report, "com.test.alpha");
            Assert.Equal(PluginLoadStatus.Failed, alpha.Status);
            Assert.Contains("building the plugin context failed: System.InvalidOperationException: A plugin load pass is already in progress.", alpha.ReasonSummary);
            Assert.Equal(["com.test.zulu"], host.Loaded.Select(p => p.Id));
        }
    }

    [Fact]
    public void Report_Loaded_ExcludesPluginsUnloadedSinceThePass()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        WriteStandard(dir.Path, "com.test.beta", "Beta");
        using var host = new RecordingContexts().Host(dir.Path);
        var report = host.LoadAll();

        host.Unload("com.test.alpha");

        Assert.Equal(["com.test.beta"], report.Loaded.Select(p => p.Id));
        Assert.Equal(PluginLoadStatus.Loaded, Single(report, "com.test.alpha").Status); // load-time status kept
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int LoadCount(PluginHost host) => host.LoadAll().Plugins.Count(p => p.Status == PluginLoadStatus.Loaded);

    private static void AssertLoadContextsGone(params string[] ids)
    {
        var names = ids.Select(id => "EDNexus plugin " + id).ToHashSet();
        for (var i = 0; i < 20 && AssemblyLoadContext.All.Any(c => names.Contains(c.Name!)); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name is not null && names.Contains(c.Name));
    }

    // Kept out of line so no plugin object is left in a local of the calling test's frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Weak, LoadedPlugin Plugin) LoadThenUnload(PluginHost host, string id)
    {
        var report = host.LoadAll();
        var plugin = Single(report, id).Plugin!;
        Assert.NotNull(plugin.Instance);
        return (UnloadOnly(host, id).LoadContext, plugin);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PluginUnloadResult UnloadOnly(PluginHost host, string id) => host.Unload(id)!;

    private static void AssertCollected(WeakReference weak)
    {
        for (var i = 0; weak.IsAlive && i < 20; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "the plugin's load context was not collected after unload");
    }
}
