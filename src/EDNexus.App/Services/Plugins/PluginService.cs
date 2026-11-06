using System.Diagnostics;
using System.Reflection;
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
/// <b>Consent.</b> There is no consent UI yet (the management UI is #61), so the default is
/// <b>deny</b>: a plugin runs only if <see cref="Grants"/> holds a grant for its id made for the
/// exact manifest, entry assembly and plugin files now on disk (<see cref="PluginConsentRequest"/>),
/// and then only with the capabilities granted. Grants and disable flags of plugins whose folder is
/// gone are pruned after the load pass (<see cref="PluginChoicePruner"/>). With an empty store, which is every fresh install, no
/// plugin code is loaded at all. The consent callback can be replaced through the constructor and
/// <see cref="Grants"/> is the store the future UI writes to. The decision is taken by
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
    private readonly Func<PluginConsentRequest, IReadOnlyCollection<string>?> _consent;
    private readonly Action<Exception> _onError;
    private readonly AppSettings _settings;

    private readonly object _gate = new();
    private PluginRuntime? _runtime;
    private Task _starting = Task.CompletedTask;

    /// <param name="settings">The app settings, where enable / disable and consent are persisted.</param>
    /// <param name="save">Writes the settings file (e.g. <c>SettingsStore.TrySave</c>).</param>
    /// <param name="postToUi">Runs an action on the UI thread, where settings are saved.</param>
    /// <param name="isSimulated">True while developer mode feeds the engine fabricated events.</param>
    /// <param name="onError">Receives host faults (never a plugin's own misbehaviour); default: ignore.</param>
    /// <param name="appVersion">The running app version; default read from the entry assembly.</param>
    /// <param name="pluginsRoot">The plugins root; default <see cref="PluginPaths.Resolve()"/>. Tests only.</param>
    /// <param name="consent">Replaces the default deny-unless-granted policy. The future consent UI plugs in here or through <see cref="Grants"/>.</param>
    public PluginService(
        AppSettings settings,
        Action<AppSettings> save,
        Action<Action> postToUi,
        Func<bool> isSimulated,
        Action<Exception>? onError = null,
        SemanticVersion? appVersion = null,
        string? pluginsRoot = null,
        Func<PluginConsentRequest, IReadOnlyCollection<string>?>? consent = null)
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
        _pluginsRoot = pluginsRoot;
        _consent = consent ?? PluginConsent.FromGrants(Grants);
    }

    /// <summary>Where the commander's enable / disable choice is persisted.</summary>
    public IPluginEnablementStore Enablement { get; }

    /// <summary>
    /// Where the commander's consent is persisted. Empty until the management UI records grants, so
    /// until then no plugin runs.
    /// </summary>
    public IPluginGrantStore Grants { get; }

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
        try
        {
            Detach();   // one runtime per engine; a leftover one is bound to a bus that is going away
            var runtime = PluginRuntime.Create(bus, state, new PluginRuntimeOptions
            {
                PluginsRoot = _pluginsRoot,
                AppVersion = _appVersion,
                Consent = _consent,
                EnablementStore = Enablement,
                GrantStore = Grants,
                IsSimulated = _isSimulated,
                HandlerErrorObserver = error => Trace.TraceWarning(
                    $"Plugin {error.PluginId}: handler for '{error.EventName}' threw {error.ExceptionType}: {error.Message}"),
            });
            if (runtime is null)
            {
                Trace.TraceInformation("Plugins: no per-user plugins folder could be determined; plugins are off.");
                return false;
            }
            runtime.Supervisor.StatusChanged += OnStatusChanged;
            lock (_gate) _runtime = runtime;
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
        PluginRuntime? runtime;
        lock (_gate) runtime = _runtime;
        if (runtime is null) return Task.CompletedTask;

        var task = Task.Run(() =>
        {
            try
            {
                var report = runtime.Start();
                foreach (var error in report.Errors)
                    Trace.TraceWarning($"Plugins: {error}");
                foreach (var result in report.Plugins)
                    Trace.TraceInformation($"Plugins: {result.Manifest?.Id ?? result.FolderName}: {result.Status}"
                        + (result.Status == PluginLoadStatus.Loaded ? "" : $" ({result.ReasonSummary})"));
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
        });
        lock (_gate) _starting = task;
        return task;
    }

    /// <summary>
    /// Unloads every plugin and disposes the current runtime (a no-op when there is none). May take
    /// up to the unload timeout per plugin stuck in a handler: run it on a worker. Never throws.
    /// </summary>
    public void Detach()
    {
        PluginRuntime? runtime;
        Task starting;
        lock (_gate)
        {
            runtime = _runtime;
            _runtime = null;
            starting = _starting;
            _starting = Task.CompletedTask;
        }
        if (runtime is null) return;

        try
        {
            runtime.Supervisor.StatusChanged -= OnStatusChanged;
            runtime.Dispose();
            // Dispose stops further loading; let a load that was mid-plugin finish unwinding so its
            // plugin is not left running against the bridge that was just released.
            try { starting.Wait(TimeSpan.FromSeconds(10)); }
            catch (AggregateException) { /* StartAsync observes its own failures */ }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Plugins: shutting down the plugin host failed: {ex}");
            Report(ex);
        }
    }

    /// <summary>
    /// <see cref="Detach"/> with a bound, for an engine rebuild: runs it on a worker and gives up
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
            if (Task.Run(Detach).Wait(timeout))
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
    /// <see cref="Detach"/> with a bound, for app exit: gives up waiting after
    /// <paramref name="timeout"/> (the process is ending anyway). Returns whether it finished in time.
    /// </summary>
    public bool ShutdownForExit(TimeSpan timeout)
    {
        try
        {
            return Task.Run(Detach).Wait(timeout);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Plugins: could not shut the plugin host down on exit: {ex.Message}");
            return false;
        }
    }

    /// <summary>Equivalent to <see cref="Detach"/>.</summary>
    public void Dispose() => Detach();

    private void OnStatusChanged(PluginStatus status)
    {
        Trace.TraceInformation($"Plugins: {status.Id} is now {status.State}"
            + (string.IsNullOrEmpty(status.LastError) ? "" : $" ({status.LastError})"));
        StatusChanged?.Invoke(status);
    }

    private void Report(Exception ex)
    {
        try { _onError(ex); }
        catch { /* reporting a fault must not become one */ }
    }

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
