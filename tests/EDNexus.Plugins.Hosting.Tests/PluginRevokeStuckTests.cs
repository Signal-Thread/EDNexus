using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// <see cref="PluginRuntime.Revoke"/> on a plugin whose old copy cannot be stopped: the host drops a
/// stuck copy from <see cref="PluginHost.Loaded"/> although its code may still be executing, so
/// <see cref="PluginRevocation.StillRunning"/> must still say so. Real compiled plugins whose
/// <c>Shutdown</c> or event handler blocks on a latch the test releases; the bounds are injected and
/// tiny, and nothing sleeps (the ceiling only turns a hang into a failure).
/// </summary>
public sealed class PluginRevokeStuckTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(150);

    private const string Jump = """{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump","StarSystem":"Sol","Body":"Sol"}""";

    private const string Latches = """
        public static readonly System.Threading.ManualResetEventSlim Entered = new(false), Gate = new(false), Returned = new(false);
        """;

    private readonly TempDir _dir = new();
    private readonly JournalEventBus _bus = new();
    private readonly CommanderState _state = new();
    private readonly InMemoryPluginGrantStore _grants = new();
    private readonly InMemoryPluginEnablementStore _enablement = new();
    private readonly List<ManualResetEventSlim> _gates = [];
    private readonly List<IDisposable> _owned = [];
    private readonly List<(string Id, PluginBridgeSession Session)> _sessions = [];

    public PluginRevokeStuckTests() => _ = new StateTracker(_bus, _state);

    public void Dispose()
    {
        // Whatever a test left blocked is released so the process holds no stuck threads.
        foreach (var gate in _gates) gate.Set();
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _dir.Dispose();
    }

    private PluginRuntime Runtime()
    {
        var runtime = PluginRuntime.Create(_bus, _state, new PluginRuntimeOptions
        {
            PluginsRoot = _dir.Path,
            AppVersion = SemanticVersion.Parse("1.0.0"),
            Consent = PluginConsent.FromGrants(_grants),
            EnablementStore = _enablement,
            GrantStore = _grants,
            ShutdownTimeout = Bound,
            UnloadTimeout = Bound,
            ContextFactory = (manifest, _, session) =>
            {
                lock (_sessions) _sessions.Add((manifest.Id, session));
                return new PluginContext(manifest, session, new TracePluginLog(manifest.Id));
            },
        })!;
        _owned.Add(runtime);
        return runtime;
    }

    private void Install(string id, string ns, string source)
    {
        var dir = WriteFolder(_dir.Path, id, Manifest(id, ns + ".dll", ns + ".Plugin"), (ns + ".dll", Compile(ns, source)));
        _grants.Grant(dir, [PluginCapabilities.Events]);
    }

    private (ManualResetEventSlim Entered, ManualResetEventSlim Gate, ManualResetEventSlim Returned) Probe(PluginRuntime runtime, string id)
    {
        var type = runtime.Host.Loaded.Single(p => p.Id == id).Instance!.GetType();
        ManualResetEventSlim Get(string name) => (ManualResetEventSlim)type.GetField(name)!.GetValue(null)!;
        var gate = Get("Gate");
        _gates.Add(gate);
        return (Get("Entered"), gate, Get("Returned"));
    }

    private static T Bounded<T>(Func<T> action, string what)
    {
        var task = Task.Run(action);
        if (!task.Wait(Ceiling))
            throw new Xunit.Sdk.XunitException(what + " did not return: a stuck plugin hung it");
        return task.Result;
    }

    private void Publish()
    {
        Assert.True(JournalEntry.TryParse(Jump, historical: false, out var entry));
        _bus.Publish(entry);
    }

    private void AssertRevoked(PluginRuntime runtime, string id, PluginRevocation result)
    {
        Assert.True(result.GrantRemoved);
        Assert.Null(_grants.Get(id));
        var status = runtime.Supervisor.GetStatus(id)!;
        Assert.Equal(PluginState.Failed, status.State);
        Assert.Contains(nameof(PluginLoadStatus.Denied), status.LastError);
        Assert.DoesNotContain(runtime.Host.Loaded, p => p.Id == id);   // removed from the host even though stuck
        Assert.False(_enablement.IsDisabled(id));
    }

    [Fact]
    public void Revoke_OfAPluginStuckInShutdown_ReportsItStillRunning()
    {
        const string Id = "com.test.revokestuck.shutdown";
        Install(Id, "RevokeStuckShutdown", PluginSource("RevokeStuckShutdown",
            shutdownBody: "Entered.Set(); Gate.Wait(); Returned.Set();", extraMembers: Latches));
        var runtime = Runtime();
        runtime.Start();
        var latches = Probe(runtime, Id);

        var result = Bounded(() => runtime.Revoke(Id), "Revoke");

        Assert.True(result.StillRunning);
        AssertRevoked(runtime, Id, result);
        Assert.True(runtime.Supervisor.LastUnloadStuck(Id));
        Assert.True(latches.Entered.IsSet);
        Assert.False(latches.Returned.IsSet);   // its code really is still executing

        // Releasing it afterwards is clean, and a repeat revoke is a no-op for the stopped plugin.
        latches.Gate.Set();
        Assert.True(latches.Returned.Wait(Ceiling), "the released Shutdown never finished");
        var again = Bounded(() => runtime.Revoke(Id), "second Revoke");
        Assert.False(again.StillRunning);
        Assert.False(again.GrantRemoved);
    }

    [Fact]
    public void Revoke_OfAPluginStuckInAHandler_ReportsItStillRunning()
    {
        const string Id = "com.test.revokestuck.handler";
        Install(Id, "RevokeStuckHandler", PluginSource("RevokeStuckHandler",
            initBody: "context.Events.SubscribeAny(e => { Entered.Set(); Gate.Wait(); Returned.Set(); });", extraMembers: Latches));
        var runtime = Runtime();
        runtime.Start();
        var latches = Probe(runtime, Id);
        Publish();
        Assert.True(latches.Entered.Wait(Ceiling), "the handler never ran");

        var result = Bounded(() => runtime.Revoke(Id), "Revoke");

        Assert.True(result.StillRunning);
        AssertRevoked(runtime, Id, result);
        Assert.True(runtime.Supervisor.LastUnloadStuck(Id));
        Assert.False(latches.Returned.IsSet);

        latches.Gate.Set();
        Assert.True(latches.Returned.Wait(Ceiling), "the released handler never finished");
        PluginBridgeSession session;
        lock (_sessions) session = _sessions.Last(s => s.Id == Id).Session;
        Assert.True(session.WaitForExit(Ceiling));
    }

    [Fact]
    public void Revoke_OfAWellBehavedPlugin_ReportsItStopped()
    {
        const string Id = "com.test.revokestuck.control";
        Install(Id, "RevokeStuckControl", PluginSource("RevokeStuckControl"));
        var runtime = Runtime();
        runtime.Start();
        Assert.False(runtime.Supervisor.LastUnloadStuck(Id));

        var result = Bounded(() => runtime.Revoke(Id), "Revoke");

        Assert.False(result.StillRunning);
        AssertRevoked(runtime, Id, result);
        Assert.False(runtime.Supervisor.LastUnloadStuck(Id));
        Assert.False(runtime.Supervisor.LastUnloadStuck("com.test.revokestuck.nobody"));   // unknown id
    }
}
