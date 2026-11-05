using System.Collections.Concurrent;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// The wiring <see cref="PluginRuntime"/> does (one root, late-bound fault sink, per-load fault
/// attribution, consent on every load) driven through a real bridge, bus and compiled plugins.
/// Every test uses its own plugin ids; nothing sleeps: waits are on events, bounded by <see cref="Wait"/>.
/// </summary>
public sealed class PluginRuntimeTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string Jump = """{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump","StarSystem":"Sol","Body":"Sol"}""";

    private readonly TempDir _dir = new();
    private readonly JournalEventBus _bus = new();
    private readonly CommanderState _state = new();
    private readonly ConcurrentQueue<(string Id, PluginBridgeSession Session)> _sessions = new();
    private readonly List<IDisposable> _owned = [];

    public PluginRuntimeTests() => _ = new StateTracker(_bus, _state);

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _dir.Dispose();
    }

    private PluginRuntime Runtime(
        Func<PluginManifest, IReadOnlyCollection<string>?>? consent = null,
        IPluginEnablementStore? store = null,
        IPluginGrantStore? grants = null,
        int threshold = 3,
        TimeSpan? unloadTimeout = null,
        bool captureSessions = true)
    {
        var runtime = PluginRuntime.Create(_bus, _state, new PluginRuntimeOptions
        {
            PluginsRoot = _dir.Path,
            AppVersion = SemanticVersion.Parse("1.0.0"),
            Consent = consent ?? Consent.AllowAll,
            EnablementStore = store ?? new InMemoryPluginEnablementStore(),
            GrantStore = grants,
            FaultPolicy = new PluginFaultPolicy { Threshold = threshold, Window = TimeSpan.FromMinutes(5) },
            UnloadTimeout = unloadTimeout ?? TimeSpan.FromSeconds(5),
            ContextFactory = captureSessions
                ? (manifest, _, session) =>
                {
                    _sessions.Enqueue((manifest.Id, session));
                    return new PluginContext(manifest, session, new NullLog());
                }
                : null,
        })!;
        _owned.Add(runtime);
        return runtime;
    }

    private void Install(string id, string ns, string initBody, string capabilities = "\"events\"", string version = "1.0.0")
        => WriteFolder(_dir.Path, id,
            Manifest(id, ns + ".dll", ns + ".Plugin", capabilities: capabilities).Replace("\"version\": \"1.0.0\"", $"\"version\": \"{version}\""),
            (ns + ".dll", Compile(ns, PluginSource(ns, initBody: initBody))));

    private void Publish(string json = Jump)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry));
        _bus.Publish(entry);
    }

    private PluginBridgeSession[] SessionsOf(string id) => _sessions.Where(s => s.Id == id).Select(s => s.Session).ToArray();

    private static Task<PluginStatus> NextStatus(PluginSupervisor supervisor, PluginState state)
    {
        var tcs = new TaskCompletionSource<PluginStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.StatusChanged += status =>
        {
            if (status.State == state) tcs.TrySetResult(status);
        };
        return tcs.Task;
    }

    // ---- the factory owns one root and the late-bound sink ---------------------------------------

    [Fact]
    public void Create_UsesOneRootForTheHostAndForStorage()
    {
        const string id = "com.test.rt.storage";
        Install(id, "RtStorage", "context.Storage.SetString(\"greeting\", \"hello\");", capabilities: "\"events\",\"storage\"");
        const string denied = "com.test.rt.nostorage";
        Install(denied, "RtNoStorage", "context.Storage.SetString(\"greeting\", \"hello\");", capabilities: "\"events\"");
        var runtime = Runtime();

        var report = runtime.Start();

        Assert.Equal(Path.GetFullPath(_dir.Path), runtime.PluginsRoot);
        Assert.Equal(runtime.PluginsRoot, runtime.Host.PluginsRoot);
        Assert.Equal(PluginLoadStatus.Loaded, report.Plugins.Single(p => p.FolderName == id).Status);
        // Storage is live (the bridge was given the same root the host scans): the plugin wrote under <root>/<id>/data.
        var data = Path.Combine(runtime.PluginsRoot, id, "data");
        Assert.True(Directory.Exists(data));
        Assert.NotEmpty(Directory.GetFiles(data, "*", SearchOption.AllDirectories));
        // A plugin that was not granted storage is still refused.
        var refused = report.Plugins.Single(p => p.FolderName == denied);
        Assert.Equal(PluginLoadStatus.Failed, refused.Status);
        Assert.Contains("storage", refused.ReasonSummary);
    }

    [Fact]
    public async Task Create_WiresHandlerErrorsToTheSupervisor_SoARepeatOffenderIsQuarantined()
    {
        const string id = "com.test.rt.thrower";
        Install(id, "RtThrower", "context.Events.SubscribeAny(e => throw new System.InvalidOperationException(\"boom\"));");
        var runtime = Runtime(threshold: 1);
        var quarantined = NextStatus(runtime.Supervisor, PluginState.Quarantined);
        runtime.Start();

        Publish();
        Publish();   // more than one failure in the window

        await quarantined.WaitAsync(Wait);
        var status = runtime.Supervisor.GetStatus(id)!;
        Assert.Equal(PluginState.Quarantined, status.State);
        Assert.Contains("boom", status.LastError);
    }

    [Fact]
    public void HandlerErrorObserver_SeesEveryError_AndCannotStopTheSupervisorReceivingIt()
    {
        const string id = "com.test.rt.observed";
        Install(id, "RtObserved", "context.Events.SubscribeAny(e => throw new System.InvalidOperationException(\"seen\"));");
        var seen = new BlockingCollection<PluginHandlerError>();
        var runtime = PluginRuntime.Create(_bus, _state, new PluginRuntimeOptions
        {
            PluginsRoot = _dir.Path,
            AppVersion = SemanticVersion.Parse("1.0.0"),
            Consent = Consent.AllowAll,
            EnablementStore = new InMemoryPluginEnablementStore(),
            HandlerErrorObserver = e => { seen.Add(e); throw new InvalidOperationException("observer failure is swallowed"); },
        })!;
        _owned.Add(runtime);
        runtime.Start();

        Publish();

        Assert.True(seen.TryTake(out var error, Wait));
        Assert.Equal(id, error.PluginId);
        Assert.True(error.Generation > 0);
        // The observer threw, yet the supervisor still counted the fault. Unloading joins the plugin's
        // dispatch thread, so by the time it returns that thread has finished reporting.
        Assert.NotNull(runtime.Host.Unload(id));
        Assert.Equal(1, runtime.Supervisor.GetStatus(id)!.TotalFaults);
    }

    // ---- faults are charged to the load that produced them ----------------------------------------

    [Fact]
    public async Task ReportFromAnOlderSession_AfterAReload_IsNotChargedToTheNewCopy()
    {
        const string id = "com.test.rt.generations";
        Install(id, "RtGenerations", "context.Events.SubscribeAny(e => { });");
        var runtime = Runtime(threshold: 1);
        runtime.Start();
        Assert.NotNull(runtime.Supervisor.Reload(id));
        var sessions = SessionsOf(id);
        Assert.Equal(2, sessions.Length);
        var (oldGen, newGen) = (sessions[0].Generation, sessions[1].Generation);
        Assert.True(newGen > oldGen);

        // More than the threshold from the old session: none of it counts, so no quarantine is even scheduled.
        for (var i = 0; i < 5; i++)
            runtime.Supervisor.ReportHandlerError(new PluginHandlerError(id, "FSDJump", "System.Exception", "stale", oldGen));

        var status = runtime.Supervisor.GetStatus(id)!;
        Assert.Equal(PluginState.Enabled, status.State);
        Assert.Equal(0, status.TotalFaults);

        // The same reports stamped with the current session do count, and quarantine.
        var quarantined = NextStatus(runtime.Supervisor, PluginState.Quarantined);
        runtime.Supervisor.ReportHandlerError(new PluginHandlerError(id, "FSDJump", "System.Exception", "fresh", newGen));
        Assert.Equal(1, runtime.Supervisor.GetStatus(id)!.TotalFaults);
        runtime.Supervisor.ReportHandlerError(new PluginHandlerError(id, "FSDJump", "System.Exception", "fresh", newGen));
        await quarantined.WaitAsync(Wait);
    }

    [Fact]
    public void ReportBeforeTheNewCopyHasAttached_IsNotChargedToIt()
    {
        // While a reload is loading (consent, assembly read, constructor) the old session's number
        // is no longer valid and the new one is not known yet: a stamped report can only be stale.
        const string id = "com.test.rt.awaiting";
        Install(id, "RtAwaiting", "context.Events.SubscribeAny(e => { });");
        PluginRuntime? runtime = null;
        var duringConsent = -1L;
        runtime = Runtime(consent: manifest =>
        {
            if (_sessions.Count == 1)   // the reload's consent check: the first copy is attached, the second is not
            {
                var oldGen = SessionsOf(id)[0].Generation;
                runtime!.Supervisor.ReportHandlerError(new PluginHandlerError(id, "E", "T", "late", oldGen));
                duringConsent = runtime.Supervisor.GetStatus(id)!.TotalFaults;
            }
            return manifest.Capabilities;
        });
        runtime.Start();

        runtime.Supervisor.Reload(id);

        Assert.Equal(0, duringConsent);
        Assert.Equal(2, SessionsOf(id).Length);
    }

    [Fact]
    public void StuckOldCopy_ThatFaultsAfterTheReloadStarted_DoesNotCountAgainstTheNewCopy()
    {
        const string id = "com.test.rt.stuck";
        var key = Guid.NewGuid().ToString("N");
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        AppDomain.CurrentDomain.SetData("rt.entered." + key, entered);
        AppDomain.CurrentDomain.SetData("rt.gate." + key, gate);
        // Old code: blocks inside its handler until released, then throws.
        Install(id, "RtStuck", $$"""
            context.Events.SubscribeAny(e =>
            {
                ((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData("rt.entered.{{key}}")!).Set();
                ((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData("rt.gate.{{key}}")!).Wait();
                throw new System.InvalidOperationException("late failure from the old copy");
            });
            """);
        // Zero: the unload does not wait for the stuck handler, so the reload goes ahead with it still running.
        var runtime = Runtime(unloadTimeout: TimeSpan.Zero);
        runtime.Start();
        Publish();
        Assert.True(entered.Wait(Wait));

        // The rebuilt plugin: same id, different code.
        Install(id, "RtStuck", "context.Events.SubscribeAny(e => { });");
        var reloaded = runtime.Supervisor.Reload(id);
        Assert.Equal(PluginLoadStatus.Loaded, reloaded!.Status);
        var sessions = SessionsOf(id);
        Assert.Equal(2, sessions.Length);

        gate.Set();   // the old handler now throws, after the new copy is running
        Assert.True(sessions[0].WaitForExit(Wait));

        var status = runtime.Supervisor.GetStatus(id)!;
        Assert.Equal(PluginState.Enabled, status.State);
        Assert.Equal(0, status.TotalFaults);
        Assert.Equal(1, sessions[0].HandlerErrorCount);   // it did fault, it was just not charged to the new copy
    }

    // ---- uninstall ---------------------------------------------------------------------------------

    [Fact]
    public void Uninstalled_UnloadsThePlugin_DropsItsStatus_AndClearsItsChoices()
    {
        const string id = "com.test.rt.gone";
        const string kept = "com.test.rt.kept";
        Install(id, "RtGone", "context.Events.SubscribeAny(e => { });");
        Install(kept, "RtKept", "context.Events.SubscribeAny(e => { });");
        var store = new InMemoryPluginEnablementStore();
        var grants = new InMemoryPluginGrantStore();
        grants.Set(id, new PluginGrant("hash", []));
        grants.Set(kept, new PluginGrant("hash", []));
        var runtime = Runtime(store: store, grants: grants);
        runtime.Start();
        Assert.Equal(2, runtime.Supervisor.Statuses.Count);

        Directory.Delete(Path.Combine(_dir.Path, id), recursive: true);
        Assert.True(runtime.Uninstalled(id));

        Assert.Null(runtime.Supervisor.GetStatus(id));
        Assert.DoesNotContain(runtime.Supervisor.Statuses, s => s.Id == id);
        Assert.DoesNotContain(runtime.Host.Loaded, p => p.Id == id);
        Assert.Null(grants.Get(id));
        Assert.NotNull(grants.Get(kept));
        Assert.Equal(PluginState.Enabled, runtime.Supervisor.GetStatus(kept)!.State);
        Assert.All(SessionsOf(id), s => Assert.True(s.WaitForExit(Wait)));

        // Anything that arrives late for it is ignored, not resurrected as a new entry.
        runtime.Supervisor.ReportHandlerError(new PluginHandlerError(id, "E", "T", "late", 1));
        runtime.Supervisor.ReportFault(id, "late");
        Assert.Null(runtime.Supervisor.GetStatus(id));
        Assert.False(runtime.Uninstalled(id));   // idempotent
    }

    [Fact]
    public void Uninstalled_ClearsAPersistedDisable_SoAReinstallStartsFromTheDefault()
    {
        const string id = "com.test.rt.disabled";
        Install(id, "RtDisabled", "");
        var store = new InMemoryPluginEnablementStore();
        var runtime = Runtime(store: store);
        runtime.Start();
        Assert.True(runtime.Supervisor.Disable(id));
        Assert.True(store.IsDisabled(id));

        Assert.True(runtime.Uninstalled(id));

        Assert.False(store.IsDisabled(id));
        Assert.Null(runtime.Supervisor.GetStatus(id));
    }

    [Fact]
    public void Forget_OfAnUnknownId_ReturnsFalse_AndAfterDisposeThrows()
    {
        var runtime = Runtime();
        runtime.Start();

        Assert.False(runtime.Supervisor.Forget("com.test.rt.unknown"));

        runtime.Dispose();
        Assert.Throws<ObjectDisposedException>(() => runtime.Supervisor.Forget("com.test.rt.unknown"));
    }

    // ---- consent: deny by default, keyed on manifest contents, applied on every load ----------------

    [Fact]
    public void DenyAll_RunsNoPluginCode()
    {
        const string id = "com.test.rt.deny";
        var key = "rt.ran." + Guid.NewGuid().ToString("N");
        AppDomain.CurrentDomain.SetData(key, null);
        WriteFolder(_dir.Path, id, Manifest(id, "RtDeny.dll", "RtDeny.Plugin"),
            ("RtDeny.dll", Compile("RtDeny", PluginSource("RtDeny", ctorBody: $"System.AppDomain.CurrentDomain.SetData(\"{key}\", \"ctor ran\");"))));
        var runtime = Runtime(consent: PluginConsent.DenyAll);

        var report = runtime.Start();

        Assert.Equal(PluginLoadStatus.Denied, Assert.Single(report.Plugins).Status);
        Assert.Null(AppDomain.CurrentDomain.GetData(key));
        Assert.Empty(runtime.Host.Loaded);
        Assert.Empty(_sessions);
    }

    [Fact]
    public void FromGrants_DeniesUntilGranted_ThenRunsWithOnlyTheGrantedCapabilities()
    {
        const string id = "com.test.rt.grant";
        Install(id, "RtGrant", "", capabilities: "\"events\",\"storage\"");
        var grants = new InMemoryPluginGrantStore();
        var runtime = Runtime(consent: PluginConsent.FromGrants(grants), grants: grants);

        Assert.Equal(PluginLoadStatus.Denied, Assert.Single(runtime.Start().Plugins).Status);

        var manifest = PluginManifestParser.ParseFile(Path.Combine(_dir.Path, id, PluginManifestParser.FileName)).Manifest!;
        grants.Grant(manifest, [PluginCapabilities.Events]);   // events only: storage is declared but not granted
        var result = runtime.Supervisor.Reload(id);   // re-reads the manifest and asks consent again

        Assert.Equal(PluginLoadStatus.Loaded, result!.Status);
        var session = SessionsOf(id).Single();
        Assert.Throws<UnauthorizedAccessException>(() => session.Storage.GetString("k"));
    }

    [Fact]
    public void ConsentIsReAskedOnReload_AndAnEditedManifestNeedsANewGrant()
    {
        const string id = "com.test.rt.reconsent";
        Install(id, "RtReconsent", "context.Events.SubscribeAny(e => { });");
        var grants = new InMemoryPluginGrantStore();
        var manifestPath = Path.Combine(_dir.Path, id, PluginManifestParser.FileName);
        grants.Grant(PluginManifestParser.ParseFile(manifestPath).Manifest!, [PluginCapabilities.Events]);
        var runtime = Runtime(consent: PluginConsent.FromGrants(grants));
        Assert.Equal(PluginLoadStatus.Loaded, Assert.Single(runtime.Start().Plugins).Status);

        // Reload of the same files: the grant still matches.
        Assert.Equal(PluginLoadStatus.Loaded, runtime.Supervisor.Reload(id)!.Status);

        // A different manifest on disk (here a version bump, with the same id and the same capabilities):
        // the reload re-reads it and the old approval does not carry over.
        Install(id, "RtReconsent", "context.Events.SubscribeAny(e => { });", version: "1.0.1");
        var reloaded = runtime.Supervisor.Reload(id)!;
        Assert.Equal(PluginLoadStatus.Denied, reloaded.Status);
        Assert.Equal(PluginState.Failed, runtime.Supervisor.GetStatus(id)!.State);
        Assert.Empty(runtime.Host.Loaded);

        // Approving the new contents lets it load again.
        grants.Grant(PluginManifestParser.ParseFile(manifestPath).Manifest!, [PluginCapabilities.Events]);
        Assert.Equal(PluginLoadStatus.Loaded, runtime.Supervisor.Reload(id)!.Status);
    }

    // ---- lifecycle -----------------------------------------------------------------------------------

    [Fact]
    public void Dispose_UnloadsPlugins_ReleasesSessions_AndIsIdempotent()
    {
        const string id = "com.test.rt.dispose";
        Install(id, "RtDispose", "context.Events.SubscribeAny(e => { });");
        var runtime = Runtime();
        runtime.Start();
        var session = SessionsOf(id).Single();

        runtime.Dispose();
        runtime.Dispose();

        Assert.Empty(runtime.Host.Loaded);
        Assert.True(session.WaitForExit(Wait));
        Assert.Throws<ObjectDisposedException>(() => runtime.Supervisor.Start());
    }

    [Fact]
    public void DefaultContext_IsBuiltWhenNoFactoryIsGiven_AndLogsWithoutThrowing()
    {
        const string id = "com.test.rt.default";
        var key = "rt.context." + Guid.NewGuid().ToString("N");
        Install(id, "RtDefault", $"context.Log.Info(\"hello\\u001b[31m\"); context.Log.Error(\"bad\", new System.Exception(\"x\")); System.AppDomain.CurrentDomain.SetData(\"{key}\", context.GetType().FullName);");
        var runtime = Runtime(captureSessions: false);

        Assert.Equal(PluginLoadStatus.Loaded, Assert.Single(runtime.Start().Plugins).Status);
        Assert.Equal(typeof(PluginContext).FullName, AppDomain.CurrentDomain.GetData(key));
    }

    private sealed class NullLog : IPluginLog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}

public sealed class PluginConsentTests
{
    private static PluginManifest Sample(string version = "1.0.0", params string[] capabilities) => new(
        "com.test.consent", "Consent test", version, PluginSdk.CurrentVersionString, "Author", "Does things")
    {
        EntryAssembly = "A.dll",
        EntryType = "A.Plugin",
        Capabilities = capabilities,
    };

    [Fact]
    public void Fingerprint_IsStable_IgnoresCapabilityOrder_AndChangesWithAnyField()
    {
        var baseline = PluginManifestFingerprint.Compute(Sample("1.0.0", "events", "state"));
        Assert.Equal(baseline, PluginManifestFingerprint.Compute(Sample("1.0.0", "events", "state")));
        Assert.Equal(baseline, PluginManifestFingerprint.Compute(Sample("1.0.0", "state", "events")));
        Assert.Equal(64, baseline.Length);

        Assert.NotEqual(baseline, PluginManifestFingerprint.Compute(Sample("1.0.1", "events", "state")));
        Assert.NotEqual(baseline, PluginManifestFingerprint.Compute(Sample("1.0.0", "events")));
        Assert.NotEqual(baseline, PluginManifestFingerprint.Compute(Sample("1.0.0", "events", "state", "network")));
        var m = Sample("1.0.0", "events", "state");
        Assert.NotEqual(baseline, PluginManifestFingerprint.Compute(m with { EntryType = "A.Other" }));
        Assert.NotEqual(baseline, PluginManifestFingerprint.Compute(m with { EntryAssembly = "B.dll" }));
        Assert.NotEqual(baseline, PluginManifestFingerprint.Compute(m with { Author = null }));
        Assert.NotEqual(baseline, PluginManifestFingerprint.Compute(m with { MinAppVersion = "2.0.0" }));
        Assert.NotEqual(baseline, PluginManifestFingerprint.Compute(m with { Id = "com.test.other" }));
    }

    [Fact]
    public void Fingerprint_DoesNotConfuseFieldBoundaries()
    {
        var a = Sample() with { Name = "ab", Description = "c" };
        var b = Sample() with { Name = "a", Description = "bc" };
        var withNull = Sample() with { Description = null };
        var withEmpty = Sample() with { Description = "" };

        Assert.NotEqual(PluginManifestFingerprint.Compute(a), PluginManifestFingerprint.Compute(b));
        Assert.NotEqual(PluginManifestFingerprint.Compute(withNull), PluginManifestFingerprint.Compute(withEmpty));
    }

    [Fact]
    public void FromGrants_DeniesWithNoGrant_AWrongHash_OrAThrowingStore()
    {
        var manifest = Sample("1.0.0", "events");
        var store = new InMemoryPluginGrantStore();
        var consent = PluginConsent.FromGrants(store);

        Assert.Null(consent(manifest));

        store.Set(manifest.Id, new PluginGrant("not-the-hash", ["events"]));
        Assert.Null(consent(manifest));

        store.Set(manifest.Id, new PluginGrant("", ["events"]));
        Assert.Null(consent(manifest));

        Assert.Null(PluginConsent.FromGrants(new ThrowingStore())(manifest));
        Assert.Null(PluginConsent.DenyAll(manifest));
    }

    [Fact]
    public void Grant_AllowsExactlyTheseContents_WithTheDeclaredCapabilitiesRequested()
    {
        var manifest = Sample("1.0.0", "events", "state");
        var store = new InMemoryPluginGrantStore();
        store.Grant(manifest, ["events", "network"]);   // network is not declared: dropped
        var consent = PluginConsent.FromGrants(store);

        Assert.Equal(["events"], consent(manifest)!.ToArray());
        Assert.Null(consent(manifest with { Version = "1.0.1" }));
        Assert.Null(consent(manifest with { Capabilities = ["events", "state", "network"] }));
    }

    [Fact]
    public void AppSettingsGrantStore_PersistsThroughSave_RoundTripsThroughTheSettingsFile_AndRemoves()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var settingsStore = new SettingsStore(path);
        var settings = settingsStore.Load();
        var saves = 0;
        var store = new AppSettingsPluginGrantStore(settings, s => { saves++; Assert.True(settingsStore.TrySave(s)); });
        var manifest = Sample("1.0.0", "events", "storage");

        Assert.Null(store.Get(manifest.Id));
        store.Grant(manifest, ["events"]);
        Assert.Equal(1, saves);

        var reloaded = new AppSettingsPluginGrantStore(new SettingsStore(path).Load(), _ => { });
        var grant = reloaded.Get(manifest.Id)!;
        Assert.Equal(PluginManifestFingerprint.Compute(manifest), grant.ManifestHash);
        Assert.Equal(["events"], grant.Capabilities);
        Assert.Equal(["events"], PluginConsent.FromGrants(reloaded)(manifest)!.ToArray());

        Assert.True(store.Remove(manifest.Id));
        Assert.False(store.Remove(manifest.Id));
        Assert.Equal(2, saves);
        Assert.Null(new AppSettingsPluginGrantStore(new SettingsStore(path).Load(), _ => { }).Get(manifest.Id));
    }

    [Fact]
    public void Settings_WithNullGrantSections_AreTreatedAsNoGrants()
    {
        var settings = new AppSettings();
        settings.Plugins.Grants = null!;
        settings.Plugins.Grants = new Dictionary<string, PluginGrantSettings> { ["a"] = null! };
        Assert.Empty(settings.Plugins.Grants);
        settings.Plugins = null!;
        var store = new AppSettingsPluginGrantStore(settings, _ => { });

        Assert.Null(store.Get("a"));
        store.Set("a", new PluginGrant("h", ["events"]));
        Assert.Equal("h", store.Get("a")!.ManifestHash);
    }

    private sealed class ThrowingStore : IPluginGrantStore
    {
        public PluginGrant? Get(string pluginId) => throw new IOException("unreadable");
        public void Set(string pluginId, PluginGrant grant) => throw new IOException();
        public bool Remove(string pluginId) => throw new IOException();
    }
}
