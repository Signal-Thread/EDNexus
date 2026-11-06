using System.Diagnostics;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;

namespace EDNexus.Plugins.Hosting;

/// <summary>Everything <see cref="PluginRuntime.Create"/> needs beyond the engine's bus and state.</summary>
public sealed class PluginRuntimeOptions
{
    /// <summary>
    /// The plugins root. When null, <see cref="PluginPaths.Resolve()"/> (the per-user folder, or
    /// <c>EDNEXUS_PLUGINS_DIR</c>). The runtime is the only place this value is used: the host's
    /// scan root and the bridge's storage root come from the same string.
    /// </summary>
    public string? PluginsRoot { get; init; }

    /// <summary>The running EDNexus version, for each manifest's <c>minAppVersion</c>.</summary>
    public required SemanticVersion AppVersion { get; init; }

    /// <summary>
    /// Decides, for each plugin about to load, whether it may run and with which capabilities (see
    /// <see cref="PluginHost"/>). Required, with no allow-all default; <see cref="PluginConsent"/>
    /// has the production policy. Evaluated on every load, including a reload, against the manifest
    /// and the content hashes of the entry assembly and plugin files as read from disk then.
    /// </summary>
    public required Func<PluginConsentRequest, IReadOnlyCollection<string>?> Consent { get; init; }

    /// <summary>Where the commander's enable / disable choice lives.</summary>
    public required IPluginEnablementStore EnablementStore { get; init; }

    /// <summary>
    /// Optional. Only used by <see cref="PluginRuntime.Uninstalled"/> to revoke the uninstalled
    /// plugin's consent; the consent decision itself is <see cref="Consent"/>.
    /// </summary>
    public IPluginGrantStore? GrantStore { get; init; }

    /// <summary>True while the engine carries developer-mode (fabricated) events (see <see cref="PluginBridgeOptions.IsSimulated"/>).</summary>
    public Func<bool>? IsSimulated { get; init; }

    /// <summary>The quarantine policy; default more than 10 failures in a minute.</summary>
    public PluginFaultPolicy? FaultPolicy { get; init; }

    /// <summary>How long an unload waits for a stuck handler; default 5 seconds.</summary>
    public TimeSpan? UnloadTimeout { get; init; }

    /// <summary>
    /// Called with every handler error, on the plugin's dispatch thread, before it is passed to the
    /// supervisor (exceptions swallowed). For logging and tooling.
    /// </summary>
    public Action<PluginHandlerError>? HandlerErrorObserver { get; init; }

    /// <summary>
    /// Builds a plugin's context log; default writes to <see cref="Trace"/>, tagged with the plugin id.
    /// Ignored when <see cref="ContextFactory"/> is set.
    /// </summary>
    public Func<PluginManifest, IPluginLog>? LogFactory { get; init; }

    /// <summary>
    /// Replaces the default <see cref="PluginContext"/> (the CLI harness wraps events and storage to
    /// count them). It receives the already-attached bridge session. The context it returns must be
    /// <see cref="IDisposable"/> and dispose the session (the host disposes the context at unload)
    /// and should implement <see cref="IPluginSessionControl"/>; if it throws, the runtime disposes
    /// the session.
    /// </summary>
    public Func<PluginManifest, IReadOnlySet<string>, PluginBridgeSession, IPluginContext>? ContextFactory { get; init; }
}

/// <summary>
/// One engine's plugin machinery, built in one place: a <see cref="PluginHost"/>, the
/// <see cref="PluginBridge"/> over the engine's bus and state, and the <see cref="PluginSupervisor"/>
/// over both. <see cref="Create"/> is the only code that decides what ties them together:
/// <list type="bullet">
/// <item><description>the plugins root is resolved once and handed both to <see cref="PluginHost"/> (where to scan) and
/// to <see cref="PluginBridgeOptions.PluginsRoot"/> (where each plugin's sandboxed <c>data</c> folder lives, so storage works);</description></item>
/// <item><description><see cref="PluginBridgeOptions.HandlerError"/> reaches <see cref="PluginSupervisor.ReportHandlerError"/>, bound late
/// because the supervisor needs the host and the host's context factory needs the bridge;</description></item>
/// <item><description>the host's context factory attaches a bridge session, tells the supervisor its generation
/// (<see cref="PluginSupervisor.RecordAttach"/>) so a stale copy's faults are ignored after a reload, and wraps it in a context.</description></item>
/// </list>
/// A bridge is bound to one engine bus, so build a new runtime when the app rebuilds its engine and
/// dispose the old one first.
/// </summary>
/// <remarks>
/// <b>Threading.</b> <see cref="Start"/>, <see cref="Uninstalled"/>, <see cref="Dispose"/> and every
/// lifecycle call on <see cref="Supervisor"/> run plugin code, write <c>settings.json</c> and can
/// wait for a stuck handler: call them off the UI thread. <see cref="PluginSupervisor.StatusChanged"/>
/// is raised on the thread that did the work; marshal it to the UI thread.
/// </remarks>
public sealed class PluginRuntime : IDisposable
{
    private readonly PluginRuntimeOptions _options;
    private int _disposed;

    private PluginRuntime(JournalEventBus bus, CommanderState state, string root, PluginRuntimeOptions options)
    {
        _options = options;
        PluginsRoot = root;

        Bridge = new PluginBridge(bus, state, new PluginBridgeOptions
        {
            IsSimulated = options.IsSimulated,
            PluginsRoot = root,
            // Late-bound: the supervisor does not exist yet, and a handler cannot fault before a
            // plugin has been loaded, which happens only after construction finishes.
            HandlerError = OnHandlerError,
        });
        try
        {
            Host = new PluginHost(root, options.AppVersion, options.Consent, BuildContext)
            {
                UnloadTimeout = options.UnloadTimeout ?? TimeSpan.FromSeconds(5),
            };
            Supervisor = new PluginSupervisor(Host, options.EnablementStore, options.FaultPolicy);
        }
        catch
        {
            Host?.Dispose();
            Bridge.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the runtime for one engine, or returns <see langword="null"/> when no plugins root can
    /// be determined (no per-user data folder): the app then runs with no plugins rather than fall
    /// back to a shared location. The root need not exist yet. Construct it before the engine
    /// starts publishing (the bridge takes its first state snapshot here). Never loads a plugin:
    /// call <see cref="Start"/>.
    /// </summary>
    public static PluginRuntime? Create(JournalEventBus bus, CommanderState state, PluginRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);
        var root = options.PluginsRoot ?? PluginPaths.Resolve();
        return root is null ? null : new PluginRuntime(bus, state, Path.GetFullPath(root), options);
    }

    /// <summary>The (full) plugins root, the single value host and bridge were both built from.</summary>
    public string PluginsRoot { get; }

    /// <summary>The host that loads plugins from <see cref="PluginsRoot"/>. Drive it through <see cref="Supervisor"/>.</summary>
    public PluginHost Host { get; }

    /// <summary>The bridge that gives plugins events, state and storage.</summary>
    public PluginBridge Bridge { get; }

    /// <summary>Lifecycle, enable / disable, reload and quarantine; what a management UI talks to.</summary>
    public PluginSupervisor Supervisor { get; }

    /// <summary>
    /// Discovers and loads every plugin that is not disabled and that the consent callback allows.
    /// Same as <see cref="PluginSupervisor.Start"/>; run it off the UI thread. A plugin that fails
    /// is reported in the returned report and never stops the others.
    /// </summary>
    public PluginDiscoveryReport Start() => Supervisor.Start();

    /// <summary>
    /// Call after the plugin's folder has been removed: unloads it if needed, drops the supervisor's
    /// entry, clears its disable flag and revokes its consent, so a later reinstall starts clean.
    /// Returns whether the supervisor knew the plugin. Run it off the UI thread.
    /// </summary>
    public bool Uninstalled(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var known = Supervisor.Forget(id);
        _options.EnablementStore.SetDisabled(id, false);
        _options.GrantStore?.Remove(id);
        return known;
    }

    /// <summary>
    /// Withdraws the commander's consent for <paramref name="id"/> and stops it now: the one entry
    /// point a UI should use to revoke. Consent is only evaluated when a plugin loads, so removing a
    /// grant from the <see cref="PluginRuntimeOptions.GrantStore"/> alone leaves a running plugin
    /// holding its capabilities (<c>network</c> included) until it is next unloaded; this removes the
    /// grant and then reloads the plugin, which re-runs <see cref="PluginRuntimeOptions.Consent"/>
    /// against the now-empty grant, so the old copy is unloaded (its session, handlers and storage cut
    /// off) and the plugin ends up <see cref="PluginLoadStatus.Denied"/> (<see cref="PluginState.Failed"/>).
    /// </summary>
    /// <remarks>
    /// Revoking is not disabling: no disable flag is persisted, so a later grant lets the plugin run
    /// again without an <see cref="PluginSupervisor.Enable"/>. An unknown id, a plugin that is not
    /// running, or a repeat call is a no-op for that part. Never throws (a grant store or unload
    /// failure is absorbed; check <see cref="PluginRevocation.StillRunning"/>). Runs plugin code
    /// (<c>Shutdown</c>) and writes <c>settings.json</c>: call it off the UI thread, like the
    /// other lifecycle calls. It stops the plugin only for a consent policy that reads the grant
    /// store, as <see cref="PluginConsent.FromGrants"/> (the app's) does.
    /// </remarks>
    /// <param name="id">The plugin's manifest id.</param>
    public PluginRevocation Revoke(string id)
    {
        var removed = false;
        try
        {
            if (string.IsNullOrEmpty(id)) return default;
            try { removed = _options.GrantStore?.Remove(id) ?? false; }
            catch { /* still stop it: the reload below is what matters, and a failed removal shows in GrantRemoved */ }

            if (Volatile.Read(ref _disposed) != 0) return new PluginRevocation(removed, false);
            // Only a running (or loading) plugin has anything to stop; reloading a stopped one would
            // just churn its status.
            if (Supervisor.GetStatus(id) is not { State: PluginState.Enabled or PluginState.Loading })
                return new PluginRevocation(removed, false);
            var reloaded = Supervisor.Reload(id);
            return new PluginRevocation(removed, reloaded?.Status == PluginLoadStatus.Loaded);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Reload throwing (disposed mid-call, ...): report what the host says is still loaded.
            try { return new PluginRevocation(removed, Host.Loaded.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal))); }
            catch { return new PluginRevocation(removed, false); }
        }
    }

    /// <summary>
    /// Unloads every plugin and releases the bridge. Idempotent. May take up to
    /// <see cref="PluginHost.UnloadTimeout"/> per plugin with a stuck handler: run it off the UI
    /// thread, or bound the wait.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Supervisor.Dispose();   // stops lifecycle calls and fault reports first
        try
        {
            Host.Dispose();     // unloads every plugin, which disposes their sessions
        }
        finally
        {
            Bridge.Dispose();
        }
    }

    private void OnHandlerError(PluginHandlerError error)
    {
        try { _options.HandlerErrorObserver?.Invoke(error); }
        catch { /* an observer must not stop the report reaching the supervisor */ }
        Supervisor?.ReportHandlerError(error);
    }

    private IPluginContext BuildContext(PluginManifest manifest, IReadOnlySet<string> granted)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var session = Bridge.Attach(manifest, granted);
        // Before the plugin can subscribe (so before it can fault): from here on only this session's
        // reports count for the plugin, and an older copy's are dropped.
        Supervisor.RecordAttach(manifest.Id, session.Generation);
        try
        {
            return _options.ContextFactory is { } factory
                ? factory(manifest, granted, session)
                : new PluginContext(manifest, session, _options.LogFactory?.Invoke(manifest) ?? new TracePluginLog(manifest.Id));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}

/// <summary>What <see cref="PluginRuntime.Revoke"/> did.</summary>
/// <param name="GrantRemoved">Whether a persisted grant existed and was removed.</param>
/// <param name="StillRunning">
/// Whether the plugin is still running afterwards: true only when the consent policy allowed it again
/// despite the removed grant (a policy that does not read the grant store), or the unload failed.
/// </param>
public readonly record struct PluginRevocation(bool GrantRemoved, bool StillRunning);

/// <summary>
/// The <see cref="IPluginContext"/> the app hands a plugin: the bridge session's state, events,
/// storage and UI registry plus a log. Disposing it (the host does, at unload) disposes the session;
/// <see cref="IPluginSessionControl"/> lets the host wait for handlers still running at that point.
/// </summary>
public sealed class PluginContext(PluginManifest manifest, PluginBridgeSession session, IPluginLog log)
    : IPluginContext, IDisposable, IPluginSessionControl
{
    /// <inheritdoc />
    public IReadOnlyCommanderState State => session.State;

    /// <inheritdoc />
    public IPluginEvents Events => session.Events;

    /// <inheritdoc />
    public IPluginLog Log => log;

    /// <inheritdoc />
    public IPluginStorage Storage => session.Storage;

    /// <inheritdoc />
    public IUiRegistry Ui => session.Ui;

    /// <inheritdoc />
    public PluginManifest Manifest => manifest;

    /// <inheritdoc />
    public void Dispose() => session.Dispose();

    /// <inheritdoc />
    public bool WaitForExit(TimeSpan timeout) => session.WaitForExit(timeout);
}

/// <summary>
/// Writes a plugin's log lines to <see cref="Trace"/> (the app's log file), tagged with its id and
/// sanitised, within a per-plugin budget so that a plugin logging in a loop cannot grow the
/// per-launch log file without bound.
/// </summary>
/// <remarks>
/// <b>Budget.</b> A token bucket: <see cref="BurstLines"/> lines may be written at once, refilled at
/// <see cref="RefillLinesPerSecond"/>; a line that finds the bucket empty is dropped and counted.
/// The count is reported as one "N lines suppressed" notice, either on the next line let through
/// once the bucket has recovered to <see cref="RecoveredLines"/>, or, while the plugin keeps
/// flooding, every <see cref="NoticeInterval"/> at most. Lines still pending when a plugin goes quiet
/// are reported at its next line (there is no timer). A single line is cut to
/// <see cref="MaxLineLength"/> characters. So one plugin's sustained output is at most about
/// <see cref="RefillLinesPerSecond"/> lines a second of <see cref="MaxLineLength"/> characters each.
/// The budget is per instance, which is per plugin load.
/// </remarks>
internal sealed class TracePluginLog : IPluginLog
{
    /// <summary>Lines a plugin may write at once (the bucket's capacity).</summary>
    public const int BurstLines = 30;

    /// <summary>Lines per second the bucket refills at: the sustained rate a plugin can log.</summary>
    public const double RefillLinesPerSecond = 1;

    /// <summary>Tokens the bucket must hold again for a pending "suppressed" notice to be flushed on a line that is let through.</summary>
    public const int RecoveredLines = BurstLines / 2;

    /// <summary>The longest a line may be, in characters, before the cut marker (the whole line: id, level and message).</summary>
    public const int MaxLineLength = 1024;

    /// <summary>At most one "suppressed" notice per this long while a plugin keeps flooding.</summary>
    public static readonly TimeSpan NoticeInterval = TimeSpan.FromSeconds(30);

    private readonly string _pluginId;
    private readonly Func<TimeSpan> _clock;
    private readonly Action<string> _sink;

    private readonly object _gate = new();
    private double _tokens = BurstLines;
    private TimeSpan _lastRefill;
    private long _suppressed;
    private TimeSpan _noticeDue;

    /// <param name="pluginId">The plugin's manifest id, tagged on every line.</param>
    /// <param name="clock">Monotonic time for the budget (default: a <see cref="Stopwatch"/>); injectable so tests need no sleeps.</param>
    /// <param name="sink">Where a finished line goes (default: <see cref="Trace.WriteLine(string)"/>).</param>
    public TracePluginLog(string pluginId, Func<TimeSpan>? clock = null, Action<string>? sink = null)
    {
        _pluginId = pluginId;
        var origin = Stopwatch.GetTimestamp();
        _clock = clock ?? (() => Stopwatch.GetElapsedTime(origin));
        _sink = sink ?? (static line => Trace.WriteLine(line));
        _lastRefill = _clock();
    }

    public void Debug(string message) => Write("debug", message, null);
    public void Info(string message) => Write("info", message, null);
    public void Warn(string message) => Write("warn", message, null);
    public void Error(string message, Exception? exception = null) => Write("error", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var notice = Admit(out var admitted);
            if (notice is not null)
                _sink($"Plugin {TextRules.ForDisplay(_pluginId, 150)} [warn]: {notice}");
            if (!admitted)
                return;
            // Text only: a plugin's message and exception are plugin-controlled (control characters,
            // overridden Message), and holding the exception would pin its load context.
            var text = $"Plugin {TextRules.ForDisplay(_pluginId, 150)} [{level}]: {TextRules.ForDisplay(message, MaxLineLength)}";
            if (exception is not null)
            {
                var (typeName, exceptionMessage) = PluginHost.Summarise(exception);
                text += $" ({typeName}: {exceptionMessage})";
            }
            _sink(TextRules.ForDisplay(text, MaxLineLength));
        }
        catch { /* logging must not throw into the plugin */ }
    }

    /// <summary>Takes a token if there is one; returns a "suppressed" notice that is due, if any.</summary>
    private string? Admit(out bool admitted)
    {
        lock (_gate)
        {
            var now = _clock();
            var elapsed = now - _lastRefill;
            if (elapsed > TimeSpan.Zero)
            {
                _tokens = Math.Min(BurstLines, _tokens + elapsed.TotalSeconds * RefillLinesPerSecond);
                _lastRefill = now;
            }

            admitted = _tokens >= 1;
            string? notice = null;
            if (admitted)
            {
                // Flush the count once the bucket has genuinely recovered (or the notice is overdue).
                if (_suppressed > 0 && (_tokens >= RecoveredLines || now >= _noticeDue))
                    notice = TakeNotice(now);
                _tokens -= 1;
            }
            else
            {
                if (_suppressed == 0)
                    _noticeDue = now + NoticeInterval;
                _suppressed++;
                if (now >= _noticeDue)
                    notice = TakeNotice(now);
            }
            return notice;
        }
    }

    private string TakeNotice(TimeSpan now)
    {
        var count = _suppressed;
        _suppressed = 0;
        _noticeDue = now + NoticeInterval;
        return $"{count} log line{(count == 1 ? "" : "s")} suppressed (this plugin logs faster than {RefillLinesPerSecond:0.##} line per second after a burst of {BurstLines})";
    }
}
