using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// Interleavings of the supervisor's lifecycle calls with an uninstall or a dispose. Each race is
/// forced by parking one thread inside a store write or a plugin's <c>Initialize</c> on a latch, so
/// nothing sleeps: every wait is on an event, with <see cref="Ceiling"/> only to fail a hang.
/// </summary>
public sealed class PluginLifecycleRaceTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly TempDir _dir = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _dir.Dispose();
    }

    private PluginRuntime Runtime(
        IPluginEnablementStore? store = null,
        IPluginGrantStore? grants = null,
        Func<PluginConsentRequest, IReadOnlyCollection<string>?>? consent = null)
    {
        var runtime = PluginRuntime.Create(new JournalEventBus(), new CommanderState(), new PluginRuntimeOptions
        {
            PluginsRoot = _dir.Path,
            AppVersion = SemanticVersion.Parse("1.0.0"),
            Consent = consent ?? Consent.AllowAll,
            EnablementStore = store ?? new InMemoryPluginEnablementStore(),
            GrantStore = grants,
        })!;
        _owned.Add(runtime);
        return runtime;
    }

    private void Install(string id, string ns, string ctorBody = "", string initBody = "")
        => WriteFolder(_dir.Path, id, Manifest(id, ns + ".dll", ns + ".Plugin"),
            (ns + ".dll", Compile(ns, PluginSource(ns, ctorBody: ctorBody, initBody: initBody))));

    private static T Await<T>(Task<T> task)
    {
        Assert.True(task.Wait(Ceiling), "timed out (a lifecycle call hung)");
        return task.Result;
    }

    /// <summary>An enablement store whose next write can be parked until the test lets it go.</summary>
    private sealed class ParkingStore : IPluginEnablementStore
    {
        private readonly InMemoryPluginEnablementStore _inner = new();
        private volatile Parked? _armed;

        public sealed class Parked
        {
            public readonly ManualResetEventSlim Entered = new();
            public readonly ManualResetEventSlim Release = new();
            public required string Id { get; init; }
            public required bool Value { get; init; }
        }

        /// <summary>Parks the first <c>SetDisabled(id, value)</c> from now on, before it takes effect.</summary>
        public Parked ParkNext(string id, bool value) => _armed = new Parked { Id = id, Value = value };

        public bool IsDisabled(string pluginId) => _inner.IsDisabled(pluginId);

        public void SetDisabled(string pluginId, bool disabled)
        {
            var armed = _armed;
            if (armed is not null && armed.Id == pluginId && armed.Value == disabled
                && Interlocked.CompareExchange(ref _armed, null, armed) == armed)
            {
                armed.Entered.Set();
                Assert.True(armed.Release.Wait(Ceiling));
            }
            _inner.SetDisabled(pluginId, disabled);
        }
    }

    // ---- 4: disposing the supervisor must not let an in-flight load pass load a disabled plugin ----

    [Fact]
    public void DisposedSupervisor_RefusesEveryPluginInTheHostsLoadFilter()
    {
        using var host = new PluginHost(_dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, new RecordingContexts().Factory);
        var supervisor = new PluginSupervisor(host, new InMemoryPluginEnablementStore());
        Assert.True(host.LoadFilter!("com.test.lf.any"));

        supervisor.Dispose();

        // Null would mean "allow everything" to the host.
        Assert.NotNull(host.LoadFilter);
        Assert.False(host.LoadFilter!("com.test.lf.any"));
    }

    [Fact]
    public void LoadPassRunningWhenTheSupervisorIsDisposed_DoesNotLoadAPluginTheCommanderDisabled()
    {
        const string first = "com.test.lf.a";
        const string second = "com.test.lf.b";
        var key = Guid.NewGuid().ToString("N");
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        AppDomain.CurrentDomain.SetData("lf.entered." + key, entered);
        AppDomain.CurrentDomain.SetData("lf.gate." + key, gate);
        AppDomain.CurrentDomain.SetData("lf.ran." + key, null);
        // Candidates load in folder-name order: "a" parks inside Initialize, "b" is disabled and must never run.
        Install(first, "LfFirst", initBody: $$"""
            ((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData("lf.entered.{{key}}")!).Set();
            ((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData("lf.gate.{{key}}")!).Wait();
            """);
        Install(second, "LfSecond", ctorBody: $"System.AppDomain.CurrentDomain.SetData(\"lf.ran.{key}\", \"constructed\");");
        var store = new InMemoryPluginEnablementStore();
        store.SetDisabled(second, true);
        var runtime = Runtime(store: store);

        var start = Task.Run(() => runtime.Supervisor.Start());
        try
        {
            Assert.True(entered.Wait(Ceiling));      // the pass is running, parked in the first plugin
            runtime.Supervisor.Dispose();            // what PluginRuntime.Dispose does first
        }
        finally
        {
            gate.Set();
        }
        var report = Await(start);

        Assert.Null(AppDomain.CurrentDomain.GetData("lf.ran." + key));   // its constructor never ran
        Assert.Equal(PluginLoadStatus.Disabled, report.Plugins.Single(p => p.FolderName == second).Status);
    }

    // ---- 5: Enable of an id that is forgotten in the meantime ---------------------------------------

    [Fact]
    public void Enable_WhenTheIdIsForgottenAfterTheKnownCheck_ReturnsNullInsteadOfThrowing()
    {
        const string id = "com.test.race.enable";
        Install(id, "RaceEnable");
        var store = new ParkingStore();
        store.SetDisabled(id, true);
        var runtime = Runtime(store: store);
        runtime.Start();
        Assert.Equal(PluginState.Disabled, runtime.Supervisor.GetStatus(id)!.State);
        Directory.Delete(Path.Combine(_dir.Path, id), recursive: true);   // uninstalled
        var parked = store.ParkNext(id, value: false);

        var enable = Task.Run(() => runtime.Supervisor.Enable(id));       // passes Known, parks in the store write
        Assert.True(parked.Entered.Wait(Ceiling));
        Assert.True(runtime.Uninstalled(id));                              // Forget completes underneath it
        parked.Release.Set();

        Assert.Null(Await(enable));   // a missing entry is "unknown", not a NullReferenceException
        Assert.Null(runtime.Supervisor.GetStatus(id));
    }

    // ---- 6: Disable racing an uninstall ---------------------------------------------------------------

    [Fact]
    public void Disable_WhenTheIdIsUninstalledAfterTheKnownCheck_DoesNotLeaveAStaleDisabledFlag()
    {
        const string id = "com.test.race.disable";
        Install(id, "RaceDisable");
        var store = new ParkingStore();
        var runtime = Runtime(store: store);
        runtime.Start();
        Directory.Delete(Path.Combine(_dir.Path, id), recursive: true);
        var parked = store.ParkNext(id, value: true);

        var disable = Task.Run(() => runtime.Supervisor.Disable(id));     // passes Known, parks before persisting
        Assert.True(parked.Entered.Wait(Ceiling));
        Assert.True(runtime.Uninstalled(id));                              // forget + clear the flag + revoke
        parked.Release.Set();

        Assert.False(Await(disable));
        // A reinstall of the same id must start from the default, not come back Disabled.
        Assert.False(store.IsDisabled(id));
    }

    // ---- 7: a reinstalled id can be loaded again ------------------------------------------------------

    [Fact]
    public void AReinstalledIdAfterForget_IsAdoptedByEnableAndReload_WithConsentStillApplying()
    {
        const string id = "com.test.race.reinstall";
        Install(id, "RaceReinstall");
        var grants = new InMemoryPluginGrantStore();
        grants.Grant(Path.Combine(_dir.Path, id), [PluginCapabilities.Events]);
        var runtime = Runtime(consent: PluginConsent.FromGrants(grants), grants: grants);
        Assert.Equal(PluginLoadStatus.Loaded, Assert.Single(runtime.Start().Plugins).Status);

        Directory.Delete(Path.Combine(_dir.Path, id), recursive: true);
        Assert.True(runtime.Uninstalled(id));
        // Nothing on disk: still an unknown id, for both calls.
        Assert.Null(runtime.Supervisor.Enable(id));
        Assert.Null(runtime.Supervisor.Reload(id));
        Assert.Null(runtime.Supervisor.GetStatus(id));

        Install(id, "RaceReinstall");   // the commander reinstalls it
        var denied = runtime.Supervisor.Enable(id);                         // consent was revoked by the uninstall
        Assert.Equal(PluginLoadStatus.Denied, denied!.Status);
        Assert.Equal(PluginState.Failed, runtime.Supervisor.GetStatus(id)!.State);
        Assert.Empty(runtime.Host.Loaded);

        grants.Grant(Path.Combine(_dir.Path, id), [PluginCapabilities.Events]);
        Assert.Equal(PluginLoadStatus.Loaded, runtime.Supervisor.Reload(id)!.Status);
        Assert.Equal(PluginState.Enabled, runtime.Supervisor.GetStatus(id)!.State);
        Assert.Single(runtime.Host.Loaded);
    }

    [Fact]
    public void AnInstalledFolderIsNotAdopted_BeforeStartHasRun_OrUnderAnUnsafeId()
    {
        const string id = "com.test.race.early";
        Install(id, "RaceEarly");
        var runtime = Runtime();

        // Before the first load pass a single-plugin load would make Start() throw.
        Assert.Null(runtime.Supervisor.Enable(id));
        Assert.Null(runtime.Supervisor.Reload(id));
        Assert.Empty(runtime.Host.Loaded);
        Assert.Single(runtime.Start().Plugins);

        Assert.Null(runtime.Supervisor.Reload(".."));
        Assert.Null(runtime.Supervisor.Reload("../" + id));
        Assert.Equal([id], runtime.Supervisor.Statuses.Select(s => s.Id));
    }

    // ---- 9: the four-argument PluginHandlerError still exists -----------------------------------------

    [Fact]
    public void PluginHandlerError_KeepsItsFourArgumentConstructor()
    {
        var ctor = typeof(PluginHandlerError).GetConstructor([typeof(string), typeof(string), typeof(string), typeof(string)]);

        Assert.NotNull(ctor);   // code compiled before Generation was added links against this exact signature
        var error = (PluginHandlerError)ctor.Invoke(["p", "e", "T", "m"]);
        Assert.Equal(("p", "e", "T", "m", 0L), (error.PluginId, error.EventName, error.ExceptionType, error.Message, error.Generation));
        Assert.Equal(new PluginHandlerError("p", "e", "T", "m", 0), error);
    }
}
