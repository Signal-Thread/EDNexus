using System.Diagnostics;
using System.Reflection;
using EDNexus.Core.Dev;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;

namespace EDNexus.App.Services.Plugins;

/// <summary>
/// The app's single point of contact with the plugin system: it owns the commander's persisted
/// plugin choices (enable / disable, consent) for the life of the process and builds one
/// <see cref="PluginRuntime"/> (host, bridge, supervisor) per engine host, because a bridge is bound
/// to one engine's bus and state and the app rebuilds its engine when leaving developer mode or
/// resetting to live.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consent.</b> The default is <b>deny</b>: a plugin runs only if <see cref="Grants"/> holds a grant for its id made for the
/// exact manifest, entry assembly and plugin files now on disk (<see cref="PluginConsentRequest"/>),
/// and then only with the capabilities granted. Grants and disable flags of plugins whose folder is
/// gone are pruned after the load pass (<see cref="PluginChoicePruner"/>). With an empty store, which is every fresh install, no
/// plugin code is loaded at all. The consent callback can be replaced through the constructor.
/// Grants are recorded only by the commander, in the plugin manager in Settings, through
/// <see cref="Approve"/> and withdrawn through <see cref="Revoke"/>. The decision is taken by
/// <see cref="PluginHost"/> on every load, a reload included, from the manifest it reads then, so
/// editing or updating a plugin revokes its approval until it is granted again.
/// </para>
/// <para>
/// <b>Threading.</b> Nothing here runs plugin code on the caller's thread: <see cref="StartAsync"/>
/// loads on the thread pool, and <see cref="Detach"/> / <see cref="ShutdownForExit"/> are meant for a
/// worker (the latter bounds its wait). A failing plugin, or a failure to build the runtime at all,
/// is traced and reported to <c>onError</c> and never reaches the caller, so it cannot stop the app
/// starting. <see cref="StatusChanged"/> is raised on whichever thread did the work.
/// </para>
/// <para>
/// <b>Persistence.</b> Choices are changed in memory on the calling thread (copy-on-write) and the
/// file write is handed to <c>postToUi</c>, where every other edit of the shared settings object
/// is serialised.
/// </para>
/// </remarks>
public sealed class PluginService : IDisposable
{
    private readonly Func<bool> _isSimulated;
    private readonly SemanticVersion _appVersion;
    private readonly string? _pluginsRoot;
    private readonly bool _noPluginsRoot;
    private readonly Func<PluginConsentRequest, IReadOnlyCollection<string>?> _consent;
    private readonly Action<Exception> _onError;
    private readonly AppSettings _settings;

    private readonly object _gate = new();
    private PluginRuntime? _runtime;
    private Task _starting = Task.CompletedTask;   // completes when every StartAsync pass has finished; replaced under _gate before the pass can run
    private readonly List<Task> _teardowns = [];  // one per runtime being unloaded right now (under _gate); each is removed when its teardown finishes
    private bool _closed;                          // ShutdownForExit / Dispose: nothing is attached or loaded any more

    /// <param name="settings">The app settings, where enable / disable and consent are persisted.</param>
    /// <param name="save">Writes the settings file (e.g. <c>SettingsStore.TrySave</c>).</param>
    /// <param name="postToUi">Runs an action on the UI thread, where settings are saved.</param>
    /// <param name="isSimulated">True while developer mode feeds the engine fabricated events.</param>
    /// <param name="onError">Receives host faults (never a plugin's own misbehaviour); default: ignore.</param>
    /// <param name="appVersion">The running app version; default read from the entry assembly.</param>
    /// <param name="pluginsRoot">The plugins root. Default: the per-user folder (<see cref="PluginPaths.DefaultRoot"/>); the <c>EDNEXUS_PLUGINS_DIR</c> override is honoured only when developer tools are available (<see cref="ResolvePluginsRoot"/>). Tests only.</param>
    /// <param name="consent">Replaces the default deny-unless-granted policy. The future consent UI plugs in here or through <see cref="Grants"/>.</param>
    /// <param name="developerTools">Whether developer tools are available; default <see cref="FeatureFlags.DeveloperTools"/>. Tests only.</param>
    /// <param name="getEnvironmentVariable">Environment lookup for the plugins-root override; default the process environment. Tests only.</param>
    public PluginService(
        AppSettings settings,
        Action<AppSettings> save,
        Action<Action> postToUi,
        Func<bool> isSimulated,
        Action<Exception>? onError = null,
        SemanticVersion? appVersion = null,
        string? pluginsRoot = null,
        Func<PluginConsentRequest, IReadOnlyCollection<string>?>? consent = null,
        bool? developerTools = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(postToUi);
        ArgumentNullException.ThrowIfNull(isSimulated);
        _settings = settings;
        void SaveOnUi(AppSettings s) => postToUi(() => save(s));
        Enablement = new AppSettingsPluginEnablementStore(settings, SaveOnUi);
        Grants = new AppSettingsPluginGrantStore(settings, SaveOnUi);
        _isSimulated = isSimulated;
        _onError = onError ?? (static _ => { });
        _appVersion = appVersion ?? ResolveAppVersion();
        // Resolved here, never left to PluginRuntime: with a null root it would consult the
        // EDNEXUS_PLUGINS_DIR override itself, whatever the flag says.
        _pluginsRoot = pluginsRoot ?? ResolvePluginsRoot(developerTools ?? FeatureFlags.DeveloperTools, getEnvironmentVariable ?? Environment.GetEnvironmentVariable);
        _noPluginsRoot = _pluginsRoot is null;
        _consent = consent ?? PluginConsent.FromGrants(Grants);
    }

    /// <summary>
    /// How long <see cref="Detach()"/> waits for a load pass that is still running (a plugin mid-
    /// <c>Initialize</c> is not yet loaded, so unloading cannot stop it) before it releases the
    /// bridge anyway. Default 10 seconds.
    /// </summary>
    public TimeSpan StartWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a <see cref="Detach()"/> waits for teardowns of earlier runtimes that are still
    /// unloading (a second concurrent detach, or one a bounded detach gave up on). Default 30
    /// seconds; those teardowns are themselves bounded, this only stops the caller depending on that.
    /// </summary>
    public TimeSpan TeardownJoinTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Where the commander's enable / disable choice is persisted.</summary>
    public IPluginEnablementStore Enablement { get; }

    /// <summary>
    /// Where the commander's consent is persisted. Empty until the commander approves a plugin in
    /// the plugin manager, so until then no plugin runs. Record grants through <see cref="Approve"/>
    /// and withdraw them through <see cref="Revoke"/>, which also apply the change to a running plugin.
    /// </summary>
    public IPluginGrantStore Grants { get; }

    /// <summary>The full path of the plugins root the app loads from, or null when no per-user folder exists (plugins are off).</summary>
    public string? PluginsRoot => _pluginsRoot is null ? null : Path.GetFullPath(_pluginsRoot);

    /// <summary>The current engine's runtime, or null (not attached, no plugins folder, or it could not be built).</summary>
    public PluginRuntime? Runtime
    {
        get { lock (_gate) return _runtime; }
    }

    /// <summary>Raised after a plugin's status changes, on the thread that changed it. Marshal to the UI thread before touching UI.</summary>
    public event Action<PluginStatus>? StatusChanged;

    /// <summary>
    /// Builds the runtime for a newly built engine. Call it before the engine starts publishing (the
    /// bridge takes its first state snapshot here) and after <see cref="Detach"/> of the previous
    /// engine's. Loads nothing: see <see cref="StartAsync"/>. Never throws; returns whether a runtime
    /// now exists.
    /// </summary>
    public bool Attach(JournalEventBus bus, CommanderState state)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (_closed) return false;   // the app is exiting: no new runtime
        }
        try
        {
            // One runtime per engine; a leftover one is bound to a bus that is going away. Never joins
            // an earlier runtime's teardown (this runs on the UI thread and that one may be stuck).
            TearDownCurrent();
            if (_noPluginsRoot)
            {
                Trace.TraceInformation("Plugins: no per-user plugins folder could be determined; plugins are off.");
                return false;
            }
            var runtime = PluginRuntime.Create(bus, state, new PluginRuntimeOptions
            {
                PluginsRoot = _pluginsRoot,
                AppVersion = _appVersion,
                Consent = _consent,
                EnablementStore = Enablement,
                GrantStore = Grants,
                IsSimulated = _isSimulated,
                HandlerErrorObserver = error => Trace.TraceWarning(
                    $"Plugin {PluginLogText.Sanitise(error.PluginId, 200)}: handler for '{PluginLogText.Sanitise(error.EventName, 100)}' threw "
                    + $"{PluginLogText.Sanitise(error.ExceptionType, 200)}: {PluginLogText.Sanitise(error.Message)}"),
            });
            if (runtime is null)
            {
                Trace.TraceInformation("Plugins: no per-user plugins folder could be determined; plugins are off.");
                return false;
            }
            runtime.Supervisor.StatusChanged += OnStatusChanged;
            bool closed;
            lock (_gate)
            {
                closed = _closed;   // exit began while the runtime was being built
                if (!closed) _runtime = runtime;
            }
            if (closed)
            {
                runtime.Supervisor.StatusChanged -= OnStatusChanged;
                runtime.Dispose();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Plugins: could not set up the plugin host: {ex}");
            Report(ex);
            return false;
        }
    }

    /// <summary>
    /// Loads the installed, approved, enabled plugins on the thread pool and returns at once. The
    /// task never faults and is safe to ignore. The app calls this once the engine has replayed the
    /// journal, so plugin code can neither delay the dashboard's first paint nor see the replay
    /// (it reads the already-built state instead).
    /// </summary>
    public Task StartAsync()
    {
        PluginRuntime runtime;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed || _runtime is not { } current) return Task.CompletedTask;
            runtime = current;
            // Published before the work can run, in the same critical section that read _runtime, so
            // a Detach can never see this runtime without also seeing the pass that is about to use it.
            _starting = Task.WhenAll(_starting, done.Task);
        }

        _ = Task.Run(() =>
        {
            try
            {
                var report = runtime.Start();
                // Folder names, install-recovery messages and load reasons come from the plugins folder
                // (a Linux folder name may hold a newline) or from plugin code: never log them raw.
                foreach (var error in report.Errors)
                    Trace.TraceWarning($"Plugins: {PluginLogText.Sanitise(error)}");
                foreach (var result in report.Plugins)
                    Trace.TraceInformation($"Plugins: {PluginLogText.Sanitise(result.Manifest?.Id ?? result.FolderName, 200)}: {result.Status}"
                        + (result.Status == PluginLoadStatus.Loaded ? "" : $" ({PluginLogText.Sanitise(result.ReasonSummary)})"));
                // After the load pass, which first repairs an interrupted install: an id whose only
                // copy is still in a .replaced-* backup has not been uninstalled.
                PluginChoicePruner.Prune(_settings, runtime.PluginsRoot, Grants, Enablement);
            }
            catch (ObjectDisposedException)
            {
                // The engine was rebuilt or the app is closing while plugins were loading.
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Plugins: loading failed: {ex}");
                Report(ex);
            }
            finally
            {
                done.TrySetResult();
            }
        });
        return done.Task;
    }

    /// <summary>
    /// Withdraws consent for a plugin and stops it if it is running (see <see cref="PluginRuntime.Revoke"/>);
    /// the entry point the management UI uses to revoke, because removing the grant from
    /// <see cref="Grants"/> alone would leave the loaded plugin running with its capabilities.
    /// Without a runtime only the persisted grant is removed. Not a disable: nothing is persisted
    /// beyond the grant removal. Safe off the UI thread (run it on a worker: it unloads plugin code),
    /// idempotent, never throws.
    /// </summary>
    public PluginRevocation Revoke(string id)
    {
        if (string.IsNullOrEmpty(id)) return default;
        PluginRuntime? runtime;
        lock (_gate) runtime = _runtime;
        try
        {
            if (runtime is not null) return runtime.Revoke(id);
            return new PluginRevocation(Grants.Remove(id), false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Plugins: revoking a plugin failed: {PluginLogText.Sanitise(ex.Message)}");
            return default;
        }
    }

    /// <summary>
    /// Records the commander's consent for exactly <paramref name="request"/> (the manifest, entry
    /// assembly and files they were shown, read with <see cref="PluginConsentRequest.TryRead"/>) with
    /// <paramref name="capabilities"/>, then reloads the plugin so the decision takes effect now: a
    /// newly approved plugin starts, and one whose grant was narrowed is unloaded and loaded again
    /// without what was taken away (consent is only evaluated at load). A disabled plugin is only
    /// recorded, not started; without a runtime only the grant is recorded. Returns the load result,
    /// or null when nothing was loaded. Runs plugin code and writes <c>settings.json</c>: call it on
    /// a worker. Never throws for a plugin problem.
    /// </summary>
    /// <exception cref="ArgumentException">The request carries no content hash (see <see cref="PluginGrantStoreExtensions.Grant(IPluginGrantStore, PluginConsentRequest, IEnumerable{string})"/>).</exception>
    public PluginLoadResult? Approve(PluginConsentRequest request, IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(request);
        Grants.Grant(request, capabilities);
        var id = request.Manifest.Id;
        return Lifecycle(id, "approving", supervisor => supervisor.Reload(id));
    }

    /// <summary>
    /// Persists the commander's enable / disable choice and applies it now: disabling unloads the
    /// plugin, enabling loads it (it still needs a grant to run). Without a runtime only the choice
    /// is saved. Runs plugin code: call it on a worker. Never throws.
    /// </summary>
    public void SetEnabled(string id, bool enabled)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            // Saved first, so the choice sticks even for a plugin the supervisor does not know (no
            // runtime, or installed after the load pass).
            Enablement.SetDisabled(id, !enabled);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Plugins: saving the enable choice failed: {PluginLogText.Sanitise(ex.Message)}");
            return;
        }
        Lifecycle(id, enabled ? "enabling" : "disabling", supervisor =>
        {
            if (enabled) return supervisor.Enable(id);
            supervisor.Disable(id);
            return null;
        });
    }

    /// <summary>
    /// Unloads the plugin and loads what is on disk now: picks up a rebuild, retries a quarantined
    /// plugin, and re-checks consent. A disabled plugin stays unloaded. Returns the load result, or
    /// null. Runs plugin code: call it on a worker. Never throws.
    /// </summary>
    public PluginLoadResult? Reload(string id)
        => string.IsNullOrEmpty(id) ? null : Lifecycle(id, "reloading", supervisor => supervisor.Reload(id));

    private PluginLoadResult? Lifecycle(string id, string what, Func<PluginSupervisor, PluginLoadResult?> call)
    {
        PluginRuntime? runtime;
        lock (_gate) runtime = _closed ? null : _runtime;
        if (runtime is null) return null;
        try
        {
            return call(runtime.Supervisor);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The engine was rebuilt mid-call, or a load pass was running; the supervisor records a
            // failed load on the plugin's status, which is what the manager shows.
            Trace.TraceWarning($"Plugins: {what} {PluginLogText.Sanitise(id, 200)} failed: {PluginLogText.Sanitise(ex.Message)}");
            return null;
        }
    }

    /// <summary>
    /// Unloads every plugin and disposes the current runtime (a no-op when there is none). May take
    /// up to the unload timeout per plugin stuck in a handler: run it on a worker. Never throws.
    /// Teardowns of earlier runtimes that are still unloading (another <see cref="Detach()"/> in
    /// progress, or one a bounded detach gave up on) are waited for too, up to
    /// <see cref="TeardownJoinTimeout"/>, so "returned" always means the plugins are gone, or the
    /// wait gave up. A runtime attached while an older teardown is still running is torn down
    /// regardless of it.
    /// </summary>
    public void Detach() => DetachCore(TeardownJoinTimeout);

    /// <summary>
    /// <see cref="Detach()"/> with a bound, for an engine rebuild: runs it on a worker and gives up
    /// waiting after <paramref name="timeout"/>, so a plugin that will not stop can never hold the
    /// rebuild (and with it the dashboard) forever. Returns whether it finished in time; when it did
    /// not it is logged and the caller carries on, so the old engine may be disposed while a stuck
    /// plugin is still unwinding against it. That trade-off is intended: the alternative is an
    /// engine that can never be rebuilt. Never throws.
    /// </summary>
    public bool Detach(TimeSpan timeout)
    {
        try
        {
            var work = Task.Run(() => DetachCore(timeout));
            if (work.Wait(timeout) && work.Result)
                return true;
            Trace.TraceWarning($"Plugins: still shutting down after {timeout.TotalSeconds:0.##}s; continuing without waiting for them.");
            return false;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Plugins: could not shut the plugin host down: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <see cref="Detach()"/> with a bound, for app exit: gives up waiting after
    /// <paramref name="timeout"/> (the process is ending anyway). Returns whether it finished in time,
    /// including when another detach was already in progress and has now finished. From here on
    /// <see cref="Attach"/> and <see cref="StartAsync"/> do nothing, so an engine rebuild that is
    /// still under way cannot bring plugins back after the exit began.
    /// </summary>
    public bool ShutdownForExit(TimeSpan timeout)
    {
        lock (_gate) _closed = true;   // a rebuild racing the exit must not attach or load again
        try
        {
            var work = Task.Run(() => DetachCore(timeout));
            return work.Wait(timeout) && work.Result;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Plugins: could not shut the plugin host down on exit: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Tears the current runtime down (if any), then waits up to <paramref name="joinTimeout"/> for
    /// the teardowns of earlier runtimes that were already running. Returns false if that wait gave up.
    /// </summary>
    private bool DetachCore(TimeSpan joinTimeout)
    {
        var clock = Stopwatch.StartNew();
        var older = TearDownCurrent();
        if (older.Length == 0) return true;
        var left = joinTimeout == Timeout.InfiniteTimeSpan ? joinTimeout : (joinTimeout > clock.Elapsed ? joinTimeout - clock.Elapsed : TimeSpan.Zero);
        // These tasks only ever complete normally (TrySetResult), so waiting cannot surface a fault.
        return Task.WaitAll(older, left);
    }

    /// <summary>
    /// Unloads and disposes the current runtime on the calling thread, if there is one, and returns
    /// the teardowns of other runtimes that were already in flight (never waited for here). Each
    /// teardown is tracked on its own, so a runtime attached while an older one is still unloading
    /// is torn down by the next call instead of being mistaken for that older one's work.
    /// </summary>
    private Task[] TearDownCurrent()
    {
        PluginRuntime runtime;
        Task starting;
        Task[] older;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            older = [.. _teardowns];
            if (_runtime is not { } current) return older;
            runtime = current;
            _runtime = null;
            starting = _starting;
            _starting = Task.CompletedTask;
            _teardowns.Add(finished.Task);
        }

        try
        {
            runtime.Supervisor.StatusChanged -= OnStatusChanged;
            // Stop loading first (no further plugin is started and everything loaded is unloaded),
            // then let a load pass that is mid-plugin finish: that plugin is in no list yet, so only
            // the end of the pass tells us its session is gone. Only then release the bridge it reads.
            runtime.Supervisor.Dispose();
            runtime.Host.Dispose();
            try { starting.Wait(StartWaitTimeout); }
            catch (AggregateException) { /* StartAsync observes its own failures */ }
            runtime.Dispose();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Plugins: shutting down the plugin host failed: {ex}");
            Report(ex);
        }
        finally
        {
            lock (_gate) _teardowns.Remove(finished.Task);
            finished.TrySetResult();
        }
        return older;
    }

    /// <summary>Equivalent to <see cref="Detach"/>, and nothing is attached or loaded afterwards.</summary>
    public void Dispose()
    {
        lock (_gate) _closed = true;
        Detach();
    }

    private void OnStatusChanged(PluginStatus status)
    {
        Trace.TraceInformation($"Plugins: {PluginLogText.Sanitise(status.Id, 200)} is now {status.State}"
            + (string.IsNullOrEmpty(status.LastError) ? "" : $" ({PluginLogText.Sanitise(status.LastError)})"));
        StatusChanged?.Invoke(status);
    }

    private void Report(Exception ex)
    {
        try { _onError(ex); }
        catch { /* reporting a fault must not become one */ }
    }

    /// <summary>
    /// The plugins root the app uses: the per-user folder, or, only when <paramref name="developerTools"/>
    /// is true (<see cref="FeatureFlags.DeveloperTools"/>), the <c>EDNEXUS_PLUGINS_DIR</c> override
    /// (<see cref="PluginPaths.Resolve(Func{string, string?})"/>). A shipped build, where developer
    /// tools are stripped, must not let an environment variable (set by anything that launches it,
    /// and possibly a UNC share on Windows) redirect where plugin code is loaded from. Null when no
    /// per-user data folder exists: the app then runs with no plugins.
    /// </summary>
    internal static string? ResolvePluginsRoot(bool developerTools, Func<string, string?> getEnvironmentVariable)
        => developerTools ? PluginPaths.Resolve(getEnvironmentVariable) : PluginPaths.DefaultRoot();

    /// <summary>The app's version for plugin <c>minAppVersion</c> gating: the entry assembly's informational version minus any <c>+build</c> suffix.</summary>
    internal static SemanticVersion ResolveAppVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(PluginService).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString() ?? "0.0.0";
        var plus = info.IndexOf('+');
        if (plus >= 0) info = info[..plus];
        return SemanticVersion.TryParse(info, out var version) && version is not null ? version : SemanticVersion.Parse("0.0.0");
    }
}
