using System.Collections.Concurrent;
using EDNexus.App.Services.Plugins;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;

namespace EDNexus.App.Tests;

/// <summary>
/// An engine rebuild whose old plugin teardown outlasts the bounded detach: the rebuild attaches a new
/// runtime while the old one is still unloading. <see cref="PluginService.Attach"/> must not wait for
/// that teardown (it runs on the UI thread), and the new runtime must still be torn down by a later
/// <see cref="PluginService.Detach()"/> / <see cref="PluginService.ShutdownForExit"/> even though the
/// older teardown has not finished.
/// </summary>
/// <remarks>
/// The old teardown is held open by a plugin parked in <c>Initialize</c>: the teardown waits for that
/// load pass (<see cref="PluginService.StartWaitTimeout"/>, set far above the test ceiling), so it ends
/// only when the test releases the plugin. A parked <c>Shutdown</c> would not do: the host gives up on it
/// after its fixed 5 s timeout, which would make "the older teardown is still running" a race against
/// the clock. Latches are handed to the plugins through <see cref="AppDomain"/> data; nothing sleeps, and
/// the 30 s ceiling is reached only when the code under test hangs.
/// </remarks>
public sealed class PluginServiceReattachTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    /// <summary>The bound handed to a bounded detach that is meant to give up.</summary>
    private static readonly TimeSpan ShortBound = TimeSpan.FromMilliseconds(150);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-app-reattach-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly List<PluginService> _services = [];
    private readonly List<Latches> _latches = [];

    public PluginServiceReattachTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Whatever a test left parked is released first, so no stuck thread outlives the test.
        foreach (var latches in _latches) latches.Gate.Set();
        foreach (var service in _services) service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Three latches published under a fresh key: a plugin sets <c>Entered</c>, may wait for <c>Gate</c>, and sets <c>Returned</c> when its <c>Shutdown</c> has run.</summary>
    private sealed class Latches
    {
        public Latches()
        {
            Key = Guid.NewGuid().ToString("N");
            AppDomain.CurrentDomain.SetData("reattach.entered." + Key, Entered);
            AppDomain.CurrentDomain.SetData("reattach.gate." + Key, Gate);
            AppDomain.CurrentDomain.SetData("reattach.returned." + Key, Returned);
        }

        public string Key { get; }
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Gate { get; } = new();
        public ManualResetEventSlim Returned { get; } = new();

        public string Set(string which) => $"((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData(\"reattach.{which}.{Key}\")!).Set();";
        public string Park() => Set("entered") + $" ((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData(\"reattach.gate.{Key}\")!).Wait();";
    }

    /// <summary>A service whose join timeouts are far larger than the test ceiling: a code path that waits on them hangs the test, which then fails at the ceiling.</summary>
    private PluginService Service()
    {
        var service = new PluginService(
            new AppSettings(),
            _ => { },
            postToUi: action => action(),
            isSimulated: () => false,
            onError: _errors.Enqueue,
            appVersion: SemanticVersion.Parse("1.0.0"),
            pluginsRoot: _root,
            consent: request => request.Manifest.Capabilities)
        { TeardownJoinTimeout = TimeSpan.FromMinutes(10), StartWaitTimeout = TimeSpan.FromMinutes(10) };
        _services.Add(service);
        return service;
    }

    private static (JournalEventBus Bus, CommanderState State) Engine() => (new JournalEventBus(), new CommanderState());

    private static T Bounded<T>(Func<T> action, string what)
    {
        var task = Task.Run(action);
        if (!task.Wait(Ceiling))
            throw new Xunit.Sdk.XunitException(what + " did not return: it is waiting on a teardown it must not wait for");
        return task.Result;
    }

    /// <summary>Installs a plugin whose <c>Shutdown</c> records that it ran, and whose <c>Initialize</c> parks if <paramref name="parkInInitialize"/>.</summary>
    private Latches Install(string id, string ns, bool parkInInitialize = false)
    {
        var latches = new Latches();
        _latches.Add(latches);
        RoslynPluginKit.Install(_root, id, ns, $$"""
            using EDNexus.Plugins.Abstractions;
            namespace {{ns}};
            public sealed class Plugin : IEDNexusPlugin
            {
                public void Initialize(IPluginContext context) { {{(parkInInitialize ? latches.Park() : "")}} }
                public void Shutdown() { {{latches.Set("returned")}} }
            }
            """);
        return latches;
    }

    /// <summary>
    /// Runtime 1 loads <paramref name="stuckId"/>, which parks in <c>Initialize</c>, so tearing it down
    /// has to wait for the load pass. A bounded detach gives up on that (returns false) with the
    /// teardown still running. The plugin is then disabled so the next runtime does not load it again.
    /// </summary>
    private (PluginService Service, Latches Stuck) AStuckTeardownInFlight(string stuckId, string ns)
    {
        var stuck = Install(stuckId, ns, parkInInitialize: true);
        var service = Service();
        var (bus, state) = Engine();
        Assert.True(service.Attach(bus, state));
        _ = service.StartAsync();
        Assert.True(stuck.Entered.Wait(Ceiling));   // the load pass is parked inside the plugin's Initialize

        Assert.False(Bounded(() => service.Detach(ShortBound), "Detach(timeout)"));

        Assert.True(SpinWait.SpinUntil(() => service.Runtime is null, Ceiling));   // detached, but its teardown is still waiting for the load pass
        Assert.False(stuck.Gate.IsSet);
        service.Enablement.SetDisabled(stuckId, true);
        return (service, stuck);
    }

    [Fact]
    public async Task Attach_WhileAnOlderTeardownIsStillRunning_DoesNotWaitForIt_AndTheNewRuntimeIsStillTornDownOnExit()
    {
        const string StuckId = "com.test.reattach.stuck";
        const string NewId = "com.test.reattach.fresh";
        var (service, stuck) = AStuckTeardownInFlight(StuckId, "ReattachStuck");
        var fresh = Install(NewId, "ReattachFresh");   // installed only now, so runtime 1 never saw it

        // The UI thread's part of a rebuild: it must return although the old teardown has not.
        var (bus2, state2) = Engine();
        Assert.True(Bounded(() => service.Attach(bus2, state2), "Attach"));
        Assert.False(stuck.Gate.IsSet);   // proof it did not wait: the old teardown is still held open
        Assert.NotNull(service.Runtime);
        await service.StartAsync();
        Assert.Contains(service.Runtime!.Host.Loaded, p => p.Id == NewId);
        Assert.DoesNotContain(service.Runtime!.Host.Loaded, p => p.Id == StuckId);
        Assert.False(fresh.Returned.IsSet);

        // Exit: the new runtime is torn down even though the older one is still running; only the
        // wait for that older one gives up.
        var finishedInTime = Bounded(() => service.ShutdownForExit(ShortBound), "ShutdownForExit");

        Assert.False(finishedInTime);              // the older teardown is still running
        // But the new plugin's Shutdown runs: it is not left running at exit. ShutdownForExit bounds the
        // whole teardown, so on a slow machine it can return first; wait for it rather than assume it.
        Assert.True(fresh.Returned.Wait(Ceiling));
        Assert.Null(service.Runtime);
        var (bus3, state3) = Engine();
        Assert.False(service.Attach(bus3, state3));   // closed for good

        // Releasing the old one afterwards is clean, and then everything is gone.
        stuck.Gate.Set();
        Assert.True(Bounded(() => service.ShutdownForExit(Ceiling), "a second ShutdownForExit"));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task Detach_TearsDownTheCurrentRuntime_EvenWhileAnOlderTeardownRuns_ThenWaitsForTheOlderOne()
    {
        const string StuckId = "com.test.reattach.detach.stuck";
        const string NewId = "com.test.reattach.detach.fresh";
        var (service, stuck) = AStuckTeardownInFlight(StuckId, "ReattachDetachStuck");
        var fresh = Install(NewId, "ReattachDetachFresh");
        var (bus2, state2) = Engine();
        Assert.True(Bounded(() => service.Attach(bus2, state2), "Attach"));
        await service.StartAsync();
        Assert.Contains(service.Runtime!.Host.Loaded, p => p.Id == NewId);

        var detach = Task.Run(service.Detach);   // unbounded: joins the older teardown (join timeout is 10 minutes)

        Assert.True(fresh.Returned.Wait(Ceiling));                                                              // the current runtime was torn down at once
        await Assert.ThrowsAsync<TimeoutException>(() => detach.WaitAsync(TimeSpan.FromMilliseconds(200)));      // and the call still waits for the older one
        Assert.Null(service.Runtime);

        stuck.Gate.Set();
        await detach.WaitAsync(Ceiling);
        Assert.True(Bounded(() => service.Detach(Ceiling), "a further Detach"));   // nothing left in flight
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task Detach_WithNoRuntime_StillJoinsTheOlderTeardown()
    {
        var (service, stuck) = AStuckTeardownInFlight("com.test.reattach.join.stuck", "ReattachJoinStuck");

        var second = Task.Run(() => service.Detach(Ceiling));
        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromMilliseconds(200)));   // parked behind the older teardown

        stuck.Gate.Set();
        Assert.True(await second.WaitAsync(Ceiling));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task Attach_WithALeftoverRuntime_TearsItDown_WithoutWaitingForAnyOtherTeardown()
    {
        var (service, stuck) = AStuckTeardownInFlight("com.test.reattach.leftover.stuck", "ReattachLeftoverStuck");
        var leftover = Install("com.test.reattach.leftover.fresh", "ReattachLeftoverFresh");
        var (bus2, state2) = Engine();
        Assert.True(Bounded(() => service.Attach(bus2, state2), "Attach"));
        await service.StartAsync();
        Assert.Single(service.Runtime!.Host.Loaded);

        // A third engine arrives without a Detach in between: the leftover runtime goes, and the older
        // teardown is none of Attach's business.
        var (bus3, state3) = Engine();
        Assert.True(Bounded(() => service.Attach(bus3, state3), "second Attach"));

        Assert.True(leftover.Returned.IsSet);
        Assert.False(stuck.Gate.IsSet);
        Assert.NotNull(service.Runtime);
        Assert.Empty(service.Runtime!.Host.Loaded);   // the new runtime has not been started
        Assert.Empty(_errors);
    }
}
