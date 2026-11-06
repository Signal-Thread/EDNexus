using EDNexus.Plugins.Abstractions;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// A plugin whose <see cref="IEDNexusPlugin.Shutdown"/> never returns must not hang the unload,
/// <see cref="PluginSupervisor.Disable"/>, <see cref="PluginSupervisor.Reload"/> or
/// <see cref="PluginHost.Dispose"/>: it is reported as stuck instead. Each plugin's <c>Shutdown</c>
/// blocks on a latch the test releases, so nothing here sleeps or depends on how long anything takes
/// beyond "it returned at all", which is guarded by <see cref="Bounded{T}"/>.
/// </summary>
public sealed class PluginShutdownTimeoutTests : IDisposable
{
    /// <summary>The generous ceiling for "returned": only reached when the code under test hangs.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    /// <summary>The injected bound. Small, because a stuck Shutdown always waits the whole of it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(150);

    private readonly TempDir _dir = new();
    private readonly List<ManualResetEventSlim> _gates = [];

    public void Dispose()
    {
        // Whatever a test left blocked is released so the process holds no stuck threads.
        foreach (var gate in _gates) gate.Set();
        _dir.Dispose();
    }

    private const string Latches = """
        public static readonly System.Threading.ManualResetEventSlim Entered = new(false), Gate = new(false), Returned = new(false);
        public static int ShutdownThread;
        """;

    /// <summary>Shutdown records its thread, signals Entered, blocks on Gate, signals Returned, then optionally throws.</summary>
    private static string BlockedInShutdown(string ns, bool throwOnRelease = false) => PluginSource(ns,
        shutdownBody: "ShutdownThread = System.Environment.CurrentManagedThreadId; Entered.Set(); Gate.Wait(); Returned.Set();"
            + (throwOnRelease ? " throw new System.InvalidOperationException(\"late\");" : ""),
        extraMembers: Latches);

    private void Install(string id, string ns, string source)
        => WriteFolder(_dir.Path, id, Manifest(id, ns + ".dll", ns + ".Plugin"), (ns + ".dll", Compile(ns, source)));

    private PluginHost Host(RecordingContexts contexts)
        => new(_dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, contexts.Factory)
        { ShutdownTimeout = Bound, UnloadTimeout = Bound };

    private (ManualResetEventSlim Entered, ManualResetEventSlim Gate, ManualResetEventSlim Returned, Func<int> ShutdownThread) Probe(LoadedPlugin plugin)
    {
        var type = plugin.Instance!.GetType();
        ManualResetEventSlim Get(string name) => (ManualResetEventSlim)type.GetField(name)!.GetValue(null)!;
        var gate = Get("Gate");
        _gates.Add(gate);
        return (Get("Entered"), gate, Get("Returned"), () => (int)type.GetField("ShutdownThread")!.GetValue(null)!);
    }

    /// <summary>Runs <paramref name="action"/> on the pool and fails (rather than hanging the run) if it does not return.</summary>
    private static T Bounded<T>(Func<T> action, string what)
    {
        var task = Task.Run(action);
        if (!task.Wait(Ceiling))
            throw new Xunit.Sdk.XunitException(what + " did not return: a plugin whose Shutdown never returns hung it");
        return task.Result;
    }

    // ---- PluginHost ----------------------------------------------------------------------------

    [Fact]
    public void Unload_ShutdownThatNeverReturns_ReturnsWithinTheBound_AndIsReportedStuck()
    {
        const string Id = "com.test.sdt.stuck";
        Install(Id, "SdtStuck", BlockedInShutdown("SdtStuck"));
        var contexts = new RecordingContexts();
        using var host = Host(contexts);
        var plugin = Assert.Single(host.LoadAll().Plugins).Plugin!;
        var latches = Probe(plugin);

        var result = Bounded(() => host.Unload(Id)!, "Unload");

        Assert.True(latches.Entered.IsSet);     // Shutdown really was called, and is still inside plugin code
        Assert.False(latches.Returned.IsSet);
        Assert.True(result.Stuck);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("stuck shutdown:", error);
        Assert.Contains("did not return within", error);
        Assert.Empty(host.Loaded);              // removed: must not be handed out again
        Assert.False(plugin.IsLoaded);
        Assert.Contains(Id, contexts.Disposed); // the rest of the unload still happened
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.True(result.LoadContext.IsAlive, "plugin code is still running, so its load context must not be reported collected");
    }

    [Fact]
    public void Unload_ShutdownRunsOnAWorkerThread_NotTheCallers()
    {
        const string Id = "com.test.sdt.thread";
        Install(Id, "SdtThread", PluginSource("SdtThread",
            shutdownBody: "ShutdownThread = System.Environment.CurrentManagedThreadId;", extraMembers: "public static int ShutdownThread;"));
        using var host = new RecordingContexts().Host(_dir.Path);
        var plugin = Assert.Single(host.LoadAll().Plugins).Plugin!;
        var type = plugin.Instance!.GetType();

        var result = host.Unload(Id)!;

        Assert.False(result.Stuck);
        Assert.Empty(result.Errors);
        Assert.NotEqual(Environment.CurrentManagedThreadId, (int)type.GetField("ShutdownThread")!.GetValue(null)!);
    }

    [Fact]
    public void Unload_ShutdownThatReturnsLate_DoesNotThrowOrChangeTheReportedResult()
    {
        const string Id = "com.test.sdt.late";
        Install(Id, "SdtLate", BlockedInShutdown("SdtLate"));
        var contexts = new RecordingContexts();
        using var host = Host(contexts);
        var latches = Probe(Assert.Single(host.LoadAll().Plugins).Plugin!);
        var result = Bounded(() => host.Unload(Id)!, "Unload");
        var errorsBefore = result.Errors.ToArray();

        latches.Gate.Set();
        Assert.True(latches.Returned.Wait(Ceiling), "the released Shutdown never finished");

        Assert.Equal(errorsBefore, result.Errors);   // the late worker did not write into the result
        Assert.True(result.Stuck);
        Assert.Empty(host.Loaded);
    }

    [Fact]
    public void Unload_ShutdownThatThrowsAfterTheTimeout_IsSwallowed_NotAnUnobservedFault()
    {
        const string Id = "com.test.sdt.latethrow";
        Install(Id, "SdtLateThrow", BlockedInShutdown("SdtLateThrow", throwOnRelease: true));
        using var host = Host(new RecordingContexts());
        var latches = Probe(Assert.Single(host.LoadAll().Plugins).Plugin!);
        var result = Bounded(() => host.Unload(Id)!, "Unload");
        var errorsBefore = result.Errors.ToArray();

        // An exception leaking from the worker would take the whole test process down.
        latches.Gate.Set();
        Assert.True(latches.Returned.Wait(Ceiling), "the released Shutdown never finished");

        Assert.Equal(errorsBefore, result.Errors);
        Assert.Single(errorsBefore);
    }

    [Fact]
    public void UnloadAll_OneStuckShutdown_DoesNotAffectTheOtherPlugins()
    {
        Install("com.test.sdt.all.a", "SdtAllA", BlockedInShutdown("SdtAllA"));
        WriteStandard(_dir.Path, "com.test.sdt.all.b", "SdtAllB");
        WriteStandard(_dir.Path, "com.test.sdt.all.c", "SdtAllC");
        var contexts = new RecordingContexts();
        using var host = Host(contexts);
        var loaded = host.LoadAll();
        Assert.Equal(3, host.Loaded.Count());
        Probe(loaded.Plugins.Single(p => p.Manifest!.Id == "com.test.sdt.all.a").Plugin!);

        var results = Bounded(() => host.UnloadAll(), "UnloadAll");

        Assert.Equal(["com.test.sdt.all.a", "com.test.sdt.all.b", "com.test.sdt.all.c"], results.Select(r => r.Id));
        Assert.True(results[0].Stuck);
        foreach (var other in results.Skip(1))
        {
            Assert.False(other.Stuck);
            Assert.Empty(other.Errors);
        }
        Assert.Contains("shutdown com.test.sdt.all.b", contexts.Lines);
        Assert.Contains("shutdown com.test.sdt.all.c", contexts.Lines);
        Assert.Equal(3, contexts.Disposed.Count);
        Assert.Empty(host.Loaded);
    }

    [Fact]
    public void Dispose_WithAStuckShutdown_Returns()
    {
        const string Id = "com.test.sdt.dispose";
        Install(Id, "SdtDispose", BlockedInShutdown("SdtDispose"));
        var host = Host(new RecordingContexts());
        Probe(Assert.Single(host.LoadAll().Plugins).Plugin!);

        Bounded<object?>(() => { host.Dispose(); return null; }, "PluginHost.Dispose");

        Assert.Empty(host.Loaded);
    }

    [Fact]
    public void ShutdownTimeout_RejectsNegativeAndUnrepresentableValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginHost(_dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, new RecordingContexts().Factory)
        { ShutdownTimeout = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginHost(_dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, new RecordingContexts().Factory)
        { ShutdownTimeout = TimeSpan.MaxValue });
    }

    // ---- PluginSupervisor ----------------------------------------------------------------------

    private PluginSupervisor Supervisor(out PluginHost host)
    {
        host = Host(new RecordingContexts());
        return new PluginSupervisor(host, new InMemoryPluginEnablementStore());
    }

    [Fact]
    public void Disable_OfAPluginWhoseShutdownNeverReturns_Completes_AndRecordsWhy()
    {
        const string Id = "com.test.sdt.sup.disable";
        Install(Id, "SdtSupDisable", BlockedInShutdown("SdtSupDisable"));
        Install("com.test.sdt.sup.disable.other", "SdtSupDisableOther", PluginSource("SdtSupDisableOther"));
        using var supervisor = Supervisor(out var host);
        using var hostOwner = host;
        supervisor.Start();
        Probe(host.Loaded.Single(p => p.Id == Id));

        Assert.True(Bounded(() => supervisor.Disable(Id), "Disable"));

        var status = supervisor.GetStatus(Id)!;
        Assert.Equal(PluginState.Disabled, status.State);
        Assert.Contains("stuck shutdown", status.LastError);
        Assert.Equal(PluginState.Enabled, supervisor.GetStatus("com.test.sdt.sup.disable.other")!.State);   // the lifecycle lock was released
        Assert.Single(host.Loaded, p => p.Id == "com.test.sdt.sup.disable.other");
    }

    [Fact]
    public void Reload_OfAPluginWhoseShutdownNeverReturns_Completes_AndLoadsTheNewCopy()
    {
        const string Id = "com.test.sdt.sup.reload";
        Install(Id, "SdtSupReload", BlockedInShutdown("SdtSupReload"));
        using var supervisor = Supervisor(out var host);
        using var hostOwner = host;
        supervisor.Start();
        var latches = Probe(host.Loaded.Single());
        Install(Id, "SdtSupReload", PluginSource("SdtSupReload", extraMembers: "// rebuilt"));   // the fixed build

        var reloaded = Bounded(() => supervisor.Reload(Id), "Reload");

        Assert.NotNull(reloaded);
        Assert.Equal(PluginLoadStatus.Loaded, reloaded!.Status);
        Assert.Equal(PluginState.Enabled, supervisor.GetStatus(Id)!.State);
        Assert.Same(reloaded.Plugin, host.Loaded.Single());
        Assert.True(latches.Entered.IsSet);
        Assert.False(latches.Returned.IsSet);   // the old copy is still stuck; the new one runs regardless
    }

    [Fact]
    public void Forget_OfAPluginWhoseShutdownNeverReturns_Completes()
    {
        const string Id = "com.test.sdt.sup.forget";
        Install(Id, "SdtSupForget", BlockedInShutdown("SdtSupForget"));
        using var supervisor = Supervisor(out var host);
        using var hostOwner = host;
        supervisor.Start();
        Probe(host.Loaded.Single());

        Assert.True(Bounded(() => supervisor.Forget(Id), "Forget"));

        Assert.Null(supervisor.GetStatus(Id));
        Assert.Empty(host.Loaded);
    }
}
