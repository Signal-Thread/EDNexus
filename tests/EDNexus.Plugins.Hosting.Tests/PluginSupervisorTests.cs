using System.Collections.Concurrent;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>Lifecycle, persistence and fault quarantine, driven through a real bridge and journal bus.</summary>
public sealed class PluginSupervisorTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string Jump = """{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump","StarSystem":"Sol","Body":"Sol"}""";

    private readonly TempDir _dir = new();
    private readonly JournalEventBus _bus = new();
    private readonly CommanderState _state = new();
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly PluginBridge _bridge;
    private readonly List<IDisposable> _owned = [];
    private PluginSupervisor? _supervisor;
    private PluginHost? _host;

    public PluginSupervisorTests()
    {
        _ = new StateTracker(_bus, _state);
        // Late-bound so the bridge can be built before the supervisor, as an app would wire it.
        _bridge = new PluginBridge(_bus, _state, new PluginBridgeOptions { HandlerError = e => _supervisor?.ReportHandlerError(e) });
        _owned.Add(_bridge);
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _dir.Dispose();
    }

    private PluginSupervisor Supervisor(IPluginEnablementStore? store = null, int threshold = 3, TimeSpan? window = null, string[]? disabledAtStart = null)
    {
        store ??= new InMemoryPluginEnablementStore();
        foreach (var id in disabledAtStart ?? []) store.SetDisabled(id, true);
        var host = new PluginHost(_dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, (manifest, granted) =>
            new Ctx(manifest, _bridge.Attach(manifest, granted), _lines));
        _owned.Add(host);
        _host = host;
        _supervisor = new PluginSupervisor(host, store, new PluginFaultPolicy { Threshold = threshold, Window = window ?? TimeSpan.FromMinutes(5) });
        _owned.Add(_supervisor);
        return _supervisor;
    }

    private void Install(string id, string ns, string initBody, string? tag = null)
        => WriteFolder(_dir.Path, id, Manifest(id, ns + ".dll", ns + ".Plugin"),
            (ns + ".dll", Compile(ns + (tag ?? ""), PluginSource(ns, initBody: initBody))));

    /// <summary>Every event makes this plugin throw.</summary>
    private void InstallThrower(string id, string ns)
        => Install(id, ns, "context.Events.SubscribeAny(e => throw new System.InvalidOperationException(\"boom\"));");

    /// <summary>Logs "got &lt;id&gt;" per event.</summary>
    private void InstallCounter(string id, string ns)
        => Install(id, ns, "context.Events.SubscribeAny(e => context.Log.Info(\"got \" + context.Manifest.Id));");

    private void Publish(string json = Jump)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry));
        _bus.Publish(entry);
    }

    private int Count(string line) => _lines.Count(l => l == line);

    private static void Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException("timed out waiting for " + what);
            Thread.Sleep(10);
        }
    }

    // ---- states ------------------------------------------------------------------------------

    [Fact]
    public void Start_ReportsEnabledDisabledAndFailedPlugins()
    {
        InstallCounter("com.test.on", "On");
        InstallCounter("com.test.off", "Off");
        WriteFolder(_dir.Path, "com.test.bad", Manifest("com.test.bad", "Missing.dll", "Missing.Plugin"));
        var supervisor = Supervisor(disabledAtStart: ["com.test.off"]);

        supervisor.Start();

        Assert.Equal(PluginState.Enabled, supervisor.GetStatus("com.test.on")!.State);
        var off = supervisor.GetStatus("com.test.off")!;
        Assert.Equal(PluginState.Disabled, off.State);
        Assert.DoesNotContain("init com.test.off", _lines);   // a disabled plugin's code never ran
        var bad = supervisor.GetStatus("com.test.bad")!;
        Assert.Equal(PluginState.Failed, bad.State);
        Assert.Contains("not found", bad.LastError);
    }

    // ---- acceptance: a throwing plugin is quarantined, nothing else is affected ----------------

    [Fact]
    public void ThrowingHandler_IsQuarantinedAfterThreshold_WhileThePumpAndOtherPluginsKeepRunning()
    {
        InstallThrower("com.test.bad", "Bad");
        InstallCounter("com.test.good", "Good");
        var supervisor = Supervisor(threshold: 3);
        var changes = new ConcurrentQueue<PluginStatus>();
        supervisor.StatusChanged += changes.Enqueue;
        supervisor.Start();
        var pumped = 0;
        _bus.SubscribeAny(_ => Interlocked.Increment(ref pumped));

        for (var i = 0; i < 20; i++) Publish();   // publishing never throws or blocks on the bad plugin

        Eventually(() => supervisor.GetStatus("com.test.bad")!.State == PluginState.Quarantined, "quarantine");
        Eventually(() => Count("got com.test.good") == 20, "the good plugin to see every event");
        var bad = supervisor.GetStatus("com.test.bad")!;
        Assert.Contains("boom", bad.LastError);
        Assert.Contains("quarantined", bad.LastError);
        Assert.NotNull(bad.LastErrorAt);
        Assert.True(bad.TotalFaults > 3);
        Assert.Equal(PluginState.Enabled, supervisor.GetStatus("com.test.good")!.State);
        Assert.Equal(20, pumped);
        Assert.Contains("shutdown com.test.bad", _lines);   // it was really unloaded
        Assert.Single(_supervisorHost().Loaded);
        Assert.Contains(changes, c => c.Id == "com.test.bad" && c.State == PluginState.Quarantined);

        // Still running, the pump keeps going after the quarantine.
        Publish();
        Eventually(() => Count("got com.test.good") == 21, "the good plugin to see a later event");
    }

    private PluginHost _supervisorHost() => _host!;

    [Fact]
    public void FaultsSpreadOutsideTheRollingWindow_DoNotQuarantine()
    {
        InstallCounter("com.test.slow", "Slow");
        var now = DateTimeOffset.UtcNow;
        var host = new PluginHost(_dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, (m, g) => new Ctx(m, _bridge.Attach(m, g), _lines));
        _owned.Add(host);
        using var supervisor = new PluginSupervisor(host, new InMemoryPluginEnablementStore(),
            new PluginFaultPolicy { Threshold = 2, Window = TimeSpan.FromSeconds(10) }, () => now);
        supervisor.Start();

        for (var i = 0; i < 10; i++)
        {
            supervisor.ReportFault("com.test.slow", "meh");
            now += TimeSpan.FromSeconds(6);   // never more than 2 inside any 10s window
        }

        Assert.Equal(PluginState.Enabled, supervisor.GetStatus("com.test.slow")!.State);
        Assert.Equal(10, supervisor.GetStatus("com.test.slow")!.TotalFaults);
    }

    [Fact]
    public void ReportFault_ForUnknownOrNotRunningPlugin_IsIgnoredAndNeverThrows()
    {
        InstallCounter("com.test.a", "A");
        var supervisor = Supervisor(threshold: 1);
        supervisor.Start();
        supervisor.Disable("com.test.a");

        supervisor.ReportFault("com.test.a", "late");
        supervisor.ReportFault("nope", "x");
        supervisor.ReportHandlerError(new PluginHandlerError("nope", "E", "T", "m"));

        Assert.Equal(PluginState.Disabled, supervisor.GetStatus("com.test.a")!.State);
        Assert.Equal(0, supervisor.GetStatus("com.test.a")!.TotalFaults);
    }

    // ---- acceptance: disable / enable / persistence ----------------------------------------------

    [Fact]
    public void Disable_StopsEvents_Enable_ResumesThem_AndTheChoiceSurvivesRestart()
    {
        InstallCounter("com.test.a", "A");
        var settings = new AppSettings();
        var saves = 0;
        var store = new AppSettingsPluginEnablementStore(settings, _ => saves++);
        var supervisor = Supervisor(store);
        supervisor.Start();
        Publish();
        Eventually(() => Count("got com.test.a") == 1, "first event");

        Assert.True(supervisor.Disable("com.test.a"));

        Assert.Equal(PluginState.Disabled, supervisor.GetStatus("com.test.a")!.State);
        Assert.Contains("shutdown com.test.a", _lines);
        Assert.Empty(_supervisorHost().Loaded);
        Publish();
        Thread.Sleep(200);
        Assert.Equal(1, Count("got com.test.a"));
        Assert.Contains("com.test.a", settings.Plugins.DisabledIds);
        Assert.Equal(1, saves);

        // "Restart": a fresh host + supervisor over the same persisted settings does not run it.
        _lines.Clear();
        _supervisor!.Dispose();
        _supervisorHost().Dispose();
        var restarted = Supervisor(new AppSettingsPluginEnablementStore(settings, _ => saves++));
        restarted.Start();
        Assert.Equal(PluginState.Disabled, restarted.GetStatus("com.test.a")!.State);
        Assert.DoesNotContain("init com.test.a", _lines);

        var enabled = restarted.Enable("com.test.a");

        Assert.Equal(PluginLoadStatus.Loaded, enabled!.Status);
        Assert.Equal(PluginState.Enabled, restarted.GetStatus("com.test.a")!.State);
        Assert.Empty(settings.Plugins.DisabledIds);
        Publish();
        Eventually(() => Count("got com.test.a") == 1, "events after enabling");
    }

    [Fact]
    public void DisabledIds_RoundTripThroughTheSettingsFile()
    {
        var store = new SettingsStore(Path.Combine(_dir.Path, "settings.json"));
        var settings = store.Load();
        new AppSettingsPluginEnablementStore(settings, s => store.Save(s)).SetDisabled("com.test.a", true);

        var reloaded = store.Load();

        Assert.Contains("com.test.a", reloaded.Plugins.DisabledIds);
    }

    [Fact]
    public void UnknownIds_AreNoOps()
    {
        var supervisor = Supervisor();
        supervisor.Start();

        Assert.False(supervisor.Disable("nope"));
        Assert.Null(supervisor.Enable("nope"));
        Assert.Null(supervisor.Reload("nope"));
    }

    // ---- reload ------------------------------------------------------------------------------

    [Fact]
    public void Reload_PicksUpARebuiltAssemblyOverwrittenInPlace()
    {
        Install("com.test.a", "A", "context.Log.Info(\"version one\");");
        var supervisor = Supervisor();
        supervisor.Start();
        Assert.Contains("version one", _lines);

        // A rebuild lands on top of the running plugin's file (it must not be locked).
        File.WriteAllBytes(Path.Combine(_dir.Path, "com.test.a", "A.dll"),
            Compile("A", PluginSource("A", initBody: "context.Log.Info(\"version two\");")));
        var result = supervisor.Reload("com.test.a");

        Assert.Equal(PluginLoadStatus.Loaded, result!.Status);
        Assert.Contains("version two", _lines);
        Assert.Contains("shutdown com.test.a", _lines);
        Assert.Equal(PluginState.Enabled, supervisor.GetStatus("com.test.a")!.State);
        Assert.Single(_supervisorHost().Loaded);
    }

    [Fact]
    public void Reload_OfAQuarantinedPlugin_RetriesItWithAFreshFaultCount()
    {
        InstallThrower("com.test.bad", "Bad");
        var supervisor = Supervisor(threshold: 2);
        supervisor.Start();
        for (var i = 0; i < 6; i++) Publish();
        Eventually(() => supervisor.GetStatus("com.test.bad")!.State == PluginState.Quarantined, "quarantine");

        var result = supervisor.Reload("com.test.bad");

        Assert.Equal(PluginLoadStatus.Loaded, result!.Status);
        var status = supervisor.GetStatus("com.test.bad")!;
        Assert.Equal(PluginState.Enabled, status.State);
        Assert.Equal(0, status.TotalFaults);
    }

    [Fact]
    public void Reload_OfADisabledPlugin_StaysDisabled()
    {
        InstallCounter("com.test.a", "A");
        var supervisor = Supervisor(disabledAtStart: ["com.test.a"]);
        supervisor.Start();

        Assert.Null(supervisor.Reload("com.test.a"));
        Assert.Equal(PluginState.Disabled, supervisor.GetStatus("com.test.a")!.State);
    }

    [Fact]
    public void Reload_WhenTheNewVersionFailsToInitialise_ReportsFailedWithTheError()
    {
        Install("com.test.a", "A", "");
        var supervisor = Supervisor();
        supervisor.Start();
        File.WriteAllBytes(Path.Combine(_dir.Path, "com.test.a", "A.dll"),
            Compile("A", PluginSource("A", initBody: "throw new System.InvalidOperationException(\"nope\");")));

        var result = supervisor.Reload("com.test.a");

        Assert.Equal(PluginLoadStatus.Failed, result!.Status);
        var status = supervisor.GetStatus("com.test.a")!;
        Assert.Equal(PluginState.Failed, status.State);
        Assert.Contains("nope", status.LastError);
        Assert.Empty(_supervisorHost().Loaded);
    }

    // ---- host-level additions --------------------------------------------------------------------

    [Fact]
    public void LoadPlugin_RejectsPathLikeIdsAndMissingFolders()
    {
        InstallCounter("com.test.a", "A");
        using var host = new RecordingContexts().Host(_dir.Path);

        Assert.Equal(PluginLoadStatus.Rejected, host.LoadPlugin("..").Status);
        Assert.Equal(PluginLoadStatus.Rejected, host.LoadPlugin("../com.test.a").Status);
        Assert.Equal(PluginLoadStatus.Rejected, host.LoadPlugin("missing").Status);
        Assert.Equal(PluginLoadStatus.Loaded, host.LoadPlugin("com.test.a").Status);
        Assert.Throws<InvalidOperationException>(() => host.LoadPlugin("com.test.a"));
    }

    [Fact]
    public void LoadFilter_ThatThrows_RefusesThePlugin()
    {
        InstallCounter("com.test.a", "A");
        using var host = new RecordingContexts().Host(_dir.Path);
        host.LoadFilter = _ => throw new InvalidOperationException("filter bug");

        var report = host.LoadAll();

        Assert.Equal(PluginLoadStatus.Disabled, Assert.Single(report.Plugins).Status);
        Assert.Empty(host.Loaded);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>The context an app builds from a bridge session, with a log the tests can read.</summary>
    private sealed class Ctx(PluginManifest manifest, PluginBridgeSession session, ConcurrentQueue<string> lines)
        : IPluginContext, IDisposable, IPluginSessionControl
    {
        public IReadOnlyCommanderState State => session.State;
        public IPluginEvents Events => session.Events;
        public IPluginLog Log { get; } = new Recorder(lines);
        public IPluginStorage Storage => session.Storage;
        public IUiRegistry Ui => session.Ui;
        public PluginManifest Manifest => manifest;
        public void Dispose() => session.Dispose();
        public bool WaitForExit(TimeSpan timeout) => session.WaitForExit(timeout);
    }

    private sealed class Recorder(ConcurrentQueue<string> lines) : IPluginLog
    {
        public void Debug(string message) => lines.Enqueue(message);
        public void Info(string message) => lines.Enqueue(message);
        public void Warn(string message) => lines.Enqueue(message);
        public void Error(string message, Exception? exception = null) => lines.Enqueue(message);
    }
}
