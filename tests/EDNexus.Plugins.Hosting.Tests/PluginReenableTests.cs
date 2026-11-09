using System.Collections.Concurrent;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// Disable then Enable goes through the same load path as Reload (<c>LoadFresh</c>: a new generation,
/// the old session's number forgotten, the fault window cleared), but nothing else drove it that way.
/// A copy of the plugin that is still stuck in a handler when the commander disables it must not be
/// able to charge its late failure to the copy the commander enables next.
/// </summary>
public sealed class PluginReenableTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private const string Jump = """{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump","StarSystem":"Sol","Body":"Sol"}""";

    private readonly TempDir _dir = new();
    private readonly JournalEventBus _bus = new();
    private readonly CommanderState _state = new();
    private readonly ConcurrentQueue<(string Id, PluginBridgeSession Session)> _sessions = new();
    private readonly List<ManualResetEventSlim> _gates = [];
    private readonly List<IDisposable> _owned = [];

    public PluginReenableTests() => _ = new StateTracker(_bus, _state);

    public void Dispose()
    {
        foreach (var gate in _gates) gate.Set();   // never leave a handler thread stuck behind the test
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _dir.Dispose();
    }

    private PluginRuntime Runtime(int threshold = 1, TimeSpan? unloadTimeout = null)
    {
        var runtime = PluginRuntime.Create(_bus, _state, new PluginRuntimeOptions
        {
            PluginsRoot = _dir.Path,
            AppVersion = SemanticVersion.Parse("1.0.0"),
            Consent = Consent.AllowAll,
            EnablementStore = new InMemoryPluginEnablementStore(),
            FaultPolicy = new PluginFaultPolicy { Threshold = threshold, Window = TimeSpan.FromMinutes(5) },
            UnloadTimeout = unloadTimeout ?? TimeSpan.FromSeconds(5),
            ContextFactory = (manifest, _, session) =>
            {
                _sessions.Enqueue((manifest.Id, session));
                return new PluginContext(manifest, session, new NullLog());
            },
        })!;
        _owned.Add(runtime);
        return runtime;
    }

    private PluginBridgeSession[] SessionsOf(string id) => _sessions.Where(s => s.Id == id).Select(s => s.Session).ToArray();

    private void Publish()
    {
        Assert.True(JournalEntry.TryParse(Jump, historical: false, out var entry));
        _bus.Publish(entry);
    }

    /// <summary>
    /// Installs a plugin whose handler blocks on a latch until the test releases it, then throws. The
    /// latches are passed through the app domain, keyed so that nothing is shared between tests.
    /// </summary>
    private (ManualResetEventSlim Entered, ManualResetEventSlim Gate) InstallStuckThenThrowing(string id, string ns)
    {
        var key = Guid.NewGuid().ToString("N");
        var entered = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        _gates.Add(gate);
        _owned.Add(entered);
        AppDomain.CurrentDomain.SetData("re.entered." + key, entered);
        AppDomain.CurrentDomain.SetData("re.gate." + key, gate);
        WriteFolder(_dir.Path, id, Manifest(id, ns + ".dll", ns + ".Plugin"),
            (ns + ".dll", Compile(ns, PluginSource(ns, initBody: $$"""
                context.Events.SubscribeAny(e =>
                {
                    ((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData("re.entered.{{key}}")!).Set();
                    ((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData("re.gate.{{key}}")!).Wait();
                    throw new System.InvalidOperationException("late failure from the old copy");
                });
                """))));
        return (entered, gate);
    }

    [Fact]
    public void AStuckCopy_DisabledThenReEnabled_CannotBeChargedToTheNewCopy_ButTheNewCopyIsStillCounted()
    {
        const string id = "com.test.reenable.stuck";
        var (entered, gate) = InstallStuckThenThrowing(id, "ReenableStuck");
        // Zero: the unload does not wait for the stuck handler, so Disable returns with it still running.
        var runtime = Runtime(threshold: 1, unloadTimeout: TimeSpan.Zero);
        runtime.Start();
        Publish();
        Assert.True(entered.Wait(Wait));

        Assert.True(runtime.Supervisor.Disable(id));
        Assert.Equal(PluginState.Disabled, runtime.Supervisor.GetStatus(id)!.State);
        var enabled = runtime.Supervisor.Enable(id);
        Assert.Equal(PluginLoadStatus.Loaded, enabled!.Status);
        var sessions = SessionsOf(id);
        Assert.Equal(2, sessions.Length);
        Assert.NotEqual(sessions[0].Generation, sessions[1].Generation);

        // The old copy now fails, after the new one is running: the failure is real, but not the new copy's.
        gate.Set();
        Assert.True(sessions[0].WaitForExit(Wait));
        Assert.Equal(1, sessions[0].HandlerErrorCount);
        var status = runtime.Supervisor.GetStatus(id)!;
        Assert.Equal(PluginState.Enabled, status.State);
        Assert.Equal(0, status.TotalFaults);
        Assert.Null(status.LastError);

        // Positive control: the same plugin's new copy is counted. Its handler (the latch is open now) throws on every event.
        Publish();
        Assert.True(SpinWait.SpinUntil(() => runtime.Supervisor.GetStatus(id)!.TotalFaults == 1, Wait),
            "a fault from the re-enabled copy was never counted");
        Assert.Contains("late failure from the old copy", runtime.Supervisor.GetStatus(id)!.LastError);
    }

    private sealed class NullLog : IPluginLog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
