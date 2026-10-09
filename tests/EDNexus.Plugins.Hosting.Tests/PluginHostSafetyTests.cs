using System.Reflection;
using System.Runtime.Loader;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// The context an app builds from a bridge session: a disposable <see cref="IPluginContext"/> that is
/// also an <see cref="IPluginSessionControl"/>, so <see cref="PluginHost"/> can tell a plugin whose
/// handler is still running from one that has really stopped.
/// </summary>
internal sealed class SessionContext(PluginManifest manifest, PluginBridgeSession session)
    : IPluginContext, IDisposable, IPluginSessionControl
{
    public IReadOnlyCommanderState State => session.State;
    public IPluginEvents Events => session.Events;
    public IPluginLog Log { get; } = new Quiet();
    public IPluginStorage Storage => session.Storage;
    public IUiRegistry Ui => session.Ui;
    public PluginManifest Manifest => manifest;
    public void Dispose() => session.Dispose();
    public bool WaitForExit(TimeSpan timeout) => session.WaitForExit(timeout);

    private sealed class Quiet : IPluginLog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}

/// <summary>Consent gating, unload-time handler waits, shutdown races and the load-time safety checks.</summary>
public class PluginHostSafetyTests
{
    private static PluginLoadResult Single(PluginDiscoveryReport report, string folder)
        => Assert.Single(report.Plugins, p => p.FolderName == folder);

    // ---- consent (PLG-2) -------------------------------------------------------------------

    [Fact]
    public void Constructor_RequiresAConsentCallback()
    {
        using var dir = new TempDir();

        Assert.Throws<ArgumentNullException>(() =>
            new PluginHost(dir.Path, SemanticVersion.Parse("1.0.0"), null!, (_, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void LoadAll_ConsentDenied_RunsNoPluginCodeAndBuildsNoContext()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path, consent: Consent.DenyAll);

        var result = Single(host.LoadAll(), "com.test.alpha");

        Assert.Equal(PluginLoadStatus.Denied, result.Status);
        Assert.Contains("not allowed", result.ReasonSummary);
        Assert.Null(result.Plugin);
        Assert.Empty(host.Loaded);
        Assert.Empty(contexts.Lines);       // Initialize never ran
        Assert.Empty(contexts.Granted);     // the factory was never asked for a context
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name == "EDNexus plugin com.test.alpha"); // not even loaded
    }

    [Fact]
    public void LoadAll_ConsentDecidesPerPlugin_AndOnlyForPluginsThatPassTheStaticChecks()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        WriteStandard(dir.Path, "com.test.beta", "Beta");
        WriteStandard(dir.Path, "com.test.future", "Future", sdkVersion: "9.0");
        WriteFolder(dir.Path, "com.test.broken", "{ not json");
        var asked = new List<string>();
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path, consent: manifest =>
        {
            asked.Add(manifest.Id);
            return manifest.Id == "com.test.alpha" ? manifest.Capabilities : null;
        });

        var report = host.LoadAll();

        Assert.Equal(["com.test.alpha", "com.test.beta"], asked);
        Assert.Equal(PluginLoadStatus.Loaded, Single(report, "com.test.alpha").Status);
        Assert.Equal(PluginLoadStatus.Denied, Single(report, "com.test.beta").Status);
        Assert.Equal(PluginLoadStatus.Incompatible, Single(report, "com.test.future").Status);
        Assert.Equal(PluginLoadStatus.Rejected, Single(report, "com.test.broken").Status);
        Assert.Equal(["init com.test.alpha"], contexts.Lines);
    }

    [Fact]
    public void LoadAll_TheContextFactoryGetsOnlyTheGrantedDeclaredCapabilities()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha", capabilities: "\"events\", \"state\", \"storage\"");
        WriteStandard(dir.Path, "com.test.beta", "Beta", capabilities: "\"events\"");
        WriteStandard(dir.Path, "com.test.gamma", "Gamma", capabilities: "\"events\", \"network\"");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path, consent: manifest => manifest.Id switch
        {
            // The user switched storage off, and the callback also names things the manifest never declared.
            "com.test.alpha" => ["events", "state", "network", "ui.overlay", "bogus"],
            "com.test.beta" => [],                       // allowed to run, granted nothing
            _ => manifest.Capabilities,
        });

        host.LoadAll();

        Assert.Equal(["events", "state"], contexts.Granted["com.test.alpha"]);
        Assert.Empty(contexts.Granted["com.test.beta"]);
        Assert.Equal(["events", "network"], contexts.Granted["com.test.gamma"]);
        Assert.Equal(3, host.Loaded.Count);
    }

    [Fact]
    public void LoadAll_ConsentThrows_DeniesThePluginAndOthersStillLoad()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        WriteStandard(dir.Path, "com.test.zulu", "Zulu");
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path, consent: manifest =>
            manifest.Id == "com.test.alpha" ? throw new InvalidOperationException("consent store unreadable") : manifest.Capabilities);

        var report = host.LoadAll();

        var alpha = Single(report, "com.test.alpha");
        Assert.Equal(PluginLoadStatus.Failed, alpha.Status);
        Assert.Contains("the consent check failed", alpha.ReasonSummary);
        Assert.Contains("consent store unreadable", alpha.ReasonSummary);
        Assert.Equal(["init com.test.zulu"], contexts.Lines);
    }

    // ---- shutdown races (PLG-4) -------------------------------------------------------------

    [Fact]
    public void LoadAll_DisposedMidPass_DoesNotConstructOrInitialiseTheRemainingPlugins()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        WriteStandard(dir.Path, "com.test.zulu", "Zulu");
        var contexts = new RecordingContexts();
        PluginHost? host = null;
        host = new PluginHost(dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, (manifest, granted) =>
        {
            var context = contexts.Factory(manifest, granted);
            if (manifest.Id == "com.test.alpha")
                host!.Dispose();   // the app shuts down while the first plugin is loading
            return context;
        });

        var report = host.LoadAll();

        // alpha finished loading, was then unloaded because the host is gone; zulu never ran at all.
        Assert.Equal(["init com.test.alpha", "shutdown com.test.alpha"], contexts.Lines);
        Assert.DoesNotContain(contexts.Granted.Keys, id => id == "com.test.zulu");
        Assert.Single(report.Plugins);
        Assert.Contains(report.Errors, e => e.Contains("disposed during the load pass"));
        Assert.Empty(host.Loaded);
    }

    // ---- stuck handlers at unload (PLG-3) ---------------------------------------------------

    private const string BlockingMembers = "public static readonly System.Threading.ManualResetEventSlim Entered = new(false), Gate = new(false);";

    private static string BlockingPlugin(string ns, bool block) => PluginSource(ns,
        initBody: block
            ? "context.Events.SubscribeAny(e => { Entered.Set(); Gate.Wait(); });"
            : "context.Events.SubscribeAny(e => { Entered.Set(); });",
        extraMembers: BlockingMembers);

    private static (ManualResetEventSlim Entered, ManualResetEventSlim Gate) Latches(LoadedPlugin plugin)
    {
        var type = plugin.Instance!.GetType();
        return ((ManualResetEventSlim)type.GetField("Entered")!.GetValue(null)!, (ManualResetEventSlim)type.GetField("Gate")!.GetValue(null)!);
    }

    [Fact]
    public void Unload_HandlerBlockedInPluginCode_IsReportedAsStuckNotCleanlyUnloaded()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.blocker", Manifest("com.test.blocker", "Blocker.dll", "Blocker.Plugin"),
            ("Blocker.dll", Compile("Blocker", BlockingPlugin("Blocker", block: true))));
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        using var bridge = new PluginBridge(bus, state);
        using var host = new PluginHost(dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll,
            (manifest, granted) => new SessionContext(manifest, bridge.Attach(manifest, granted)))
        { UnloadTimeout = TimeSpan.FromMilliseconds(250) };
        var plugin = Single(host.LoadAll(), "com.test.blocker").Plugin!;
        var (entered, gate) = Latches(plugin);
        Assert.True(JournalEntry.TryParse("""{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump"}""", false, out var entry));
        bus.Publish(entry);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the handler never started");

        try
        {
            var result = host.Unload("com.test.blocker")!;

            Assert.True(result.Stuck);
            var error = Assert.Single(result.Errors);
            Assert.StartsWith("stuck handler:", error);
            Assert.Empty(host.Loaded);          // still removed from the host: it must not be handed out again
            Assert.False(plugin.IsLoaded);
        }
        finally
        {
            gate.Set();   // let the plugin's thread finish so the test process holds nothing
        }
    }

    [Fact]
    public void Unload_HandlerThatFinishes_IsReportedClean()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.quick", Manifest("com.test.quick", "Quick.dll", "Quick.Plugin"),
            ("Quick.dll", Compile("Quick", BlockingPlugin("Quick", block: false))));
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        using var bridge = new PluginBridge(bus, state);
        using var host = new PluginHost(dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll,
            (manifest, granted) => new SessionContext(manifest, bridge.Attach(manifest, granted)));
        var plugin = Single(host.LoadAll(), "com.test.quick").Plugin!;
        var (entered, _) = Latches(plugin);
        Assert.True(JournalEntry.TryParse("""{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump"}""", false, out var entry));
        bus.Publish(entry);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var result = host.Unload("com.test.quick")!;

        Assert.False(result.Stuck);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Unload_ContextWhoseWaitThrows_IsReportedAndStillUnloads()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        using var host = new PluginHost(dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll,
            (manifest, _) => new ThrowingWaitContext(manifest));
        host.LoadAll();

        var result = host.Unload("com.test.alpha")!;

        Assert.False(result.Stuck);
        Assert.Contains("waiting for the plugin's event handlers failed", Assert.Single(result.Errors));
        Assert.Empty(host.Loaded);
    }

    private sealed class ThrowingWaitContext(PluginManifest manifest) : IPluginContext, IPluginSessionControl
    {
        private readonly IPluginContext _inner = new RecordingContexts().Factory(manifest, new HashSet<string>());
        public IReadOnlyCommanderState State => _inner.State;
        public IPluginEvents Events => _inner.Events;
        public IPluginLog Log => _inner.Log;
        public IPluginStorage Storage => _inner.Storage;
        public IUiRegistry Ui => _inner.Ui;
        public PluginManifest Manifest => manifest;
        public bool WaitForExit(TimeSpan timeout) => throw new InvalidOperationException("boom");
    }

    // ---- SDK version cross-check (PLG-10) ---------------------------------------------------

    private static byte[] StubSdk(string assemblyVersion) => Compile("EDNexus.Plugins.Abstractions", $$"""
        using System.Reflection;
        [assembly: AssemblyVersion("{{assemblyVersion}}")]
        namespace EDNexus.Plugins.Abstractions
        {
            public interface IPluginContext { }
            public interface IEDNexusPlugin { void Initialize(IPluginContext context); void Shutdown(); }
        }
        """, referenceAbstractions: false);

    [Theory]
    [InlineData("2.5.0.0", "2.5")]     // a newer minor than the host's 2.0
    [InlineData("3.0.0.0", "3.0")]     // a different major
    public void LoadAll_PluginBuiltAgainstANewerSdkThanItsManifestAdmits_IsIncompatible(string builtAgainst, string reported)
    {
        using var dir = new TempDir();
        var sdk = StubSdk(builtAgainst);
        var plugin = Compile("Liar", $$"""
            using EDNexus.Plugins.Abstractions;
            namespace Liar;
            // built against {{builtAgainst}}
            public sealed class Plugin : IEDNexusPlugin
            {
                public void Initialize(IPluginContext context) { }
                public void Shutdown() { }
            }
            """, referenceAbstractions: false, sdk);
        // The manifest claims the SDK this host provides; the assembly's own reference says otherwise.
        WriteFolder(dir.Path, "com.test.liar", Manifest("com.test.liar", "Liar.dll", "Liar.Plugin", sdkVersion: PluginSdk.CurrentVersionString), ("Liar.dll", plugin));
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.liar");

        Assert.Equal(PluginLoadStatus.Incompatible, result.Status);
        Assert.Contains($"built against SDK {reported}", result.ReasonSummary);
        Assert.Contains($"provides SDK {PluginSdk.CurrentVersionString}", result.ReasonSummary);
        Assert.Empty(contexts.Granted);
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name == "EDNexus plugin com.test.liar");
    }

    [Fact]
    public void LoadAll_PluginBuiltAgainstTheHostsSdk_PassesTheCrossCheck()
    {
        using var dir = new TempDir();
        WriteStandard(dir.Path, "com.test.alpha", "Alpha");
        // The compiled plugin really does record the host's contract assembly version.
        Assert.Equal(typeof(IEDNexusPlugin).Assembly.GetName().Version, PluginAssemblyInspector.GetReferencedSdkVersion(Path.Combine(dir.Path, "com.test.alpha", "Alpha.dll")));
        using var host = new RecordingContexts().Host(dir.Path);

        Assert.Equal(PluginLoadStatus.Loaded, Single(host.LoadAll(), "com.test.alpha").Status);
    }

    [Fact]
    public void ReferencedSdkVersion_IsNullForAnAssemblyThatDoesNotReferenceTheSdkOrIsNotAssembly()
    {
        using var dir = new TempDir();
        var noSdk = dir.Write("NoSdk.dll", Compile("NoSdk", "namespace NoSdk; public sealed class C { }", referenceAbstractions: false));
        var junk = dir.Write("Junk.dll", [0x4D, 0x5A, 1, 2, 3]);

        Assert.Null(PluginAssemblyInspector.GetReferencedSdkVersion(noSdk));
        Assert.Null(PluginAssemblyInspector.GetReferencedSdkVersion(junk));
        Assert.Null(PluginAssemblyInspector.GetReferencedSdkVersion(Path.Combine(dir.Path, "missing.dll")));
    }

    // ---- host assemblies are not reachable from a plugin (PLG-9) ----------------------------

    private static readonly byte[] CoreImage = File.ReadAllBytes(typeof(JournalEntry).Assembly.Location);

    [Fact]
    public void LoadAll_PluginThatReferencesHostCoreWithoutShippingIt_FailsLoudly()
    {
        using var dir = new TempDir();
        WriteFolder(dir.Path, "com.test.snoop", Manifest("com.test.snoop", "Snoop.dll", "Snoop.Plugin"),
            ("Snoop.dll", Compile("Snoop", PluginSource("Snoop",
                initBody: "context.Log.Info(\"core \" + EDNexus.Core.Journal.JournalJson.Options.PropertyNameCaseInsensitive);"),
                true, CoreImage)));
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.snoop");

        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Contains("EDNexus.Core", result.ReasonSummary);
        Assert.Contains("part of EDNexus itself", result.ReasonSummary);
        Assert.DoesNotContain(contexts.Lines, l => l.StartsWith("core", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadAll_PluginMayShipItsOwnLibraryWithAnEdnexusPrefixedName()
    {
        using var dir = new TempDir();
        var helper = Compile("EDNexus.Helpers.Acme", "namespace Acme.Helpers; public static class Greeting { public static string Value => \"from the plugin's own copy\"; }");
        WriteFolder(dir.Path, "com.test.owner", Manifest("com.test.owner", "Owner.dll", "Owner.Plugin"),
            ("Owner.dll", Compile("Owner", PluginSource("Owner", initBody: "context.Log.Info(Acme.Helpers.Greeting.Value);"), true, helper)),
            ("EDNexus.Helpers.Acme.dll", helper));
        var contexts = new RecordingContexts();
        using var host = contexts.Host(dir.Path);

        var result = Single(host.LoadAll(), "com.test.owner");

        Assert.Equal(PluginLoadStatus.Loaded, result.Status);
        Assert.Contains("from the plugin's own copy", contexts.Lines);
    }

    [Theory]
    [InlineData("EDNexus.Core", true)]
    [InlineData("EDNexus.Plugins.Hosting", true)]
    [InlineData("edneXus.Whatever", true)]
    [InlineData("EliteDangerous.Inara", true)]
    [InlineData("System.Text.Json", false)]
    [InlineData("Newtonsoft.Json", false)]
    [InlineData("EDNexusish", false)]
    public void IsHostInternal_NamesTheHostsOwnAssembliesOnly(string name, bool expected)
        => Assert.Equal(expected, PluginLoadContext.IsHostInternal(new AssemblyName(name)));
}
