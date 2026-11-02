using EDNexus.Plugins.Hosting.Bridge;

namespace EDNexus.Plugins.Hosting;

/// <summary>
/// Manages plugin lifecycle on top of <see cref="PluginHost"/>: per-plugin <see cref="PluginState"/>
/// with the last error, runtime enable / disable (persisted through an
/// <see cref="IPluginEnablementStore"/>), reload, and automatic quarantine of a plugin whose calls
/// keep failing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fault isolation.</b> Every call into plugin code is already contained where it is made:
/// the constructor / <c>Initialize</c> / <c>Shutdown</c> in <see cref="PluginHost"/> and
/// <see cref="LoadedPlugin"/>, event handlers on the plugin's own dispatch thread in the bridge.
/// The supervisor is the per-plugin error sink those report to: pass <see cref="ReportHandlerError"/>
/// as <see cref="PluginBridgeOptions.HandlerError"/>. Failures are counted in a rolling window
/// (<see cref="PluginFaultPolicy"/>, measured on a monotonic clock); exceeding it quarantines the
/// plugin. The sink only records and schedules: it never blocks on plugin code or takes the
/// lifecycle lock.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Start"/>, <see cref="Enable"/>, <see cref="Disable"/> and
/// <see cref="Reload"/> run one at a time (a single lifecycle lock, because <see cref="PluginHost"/>
/// itself loads one plugin at a time), run plugin code (constructor, <c>Initialize</c>,
/// <c>Shutdown</c>, the consent callback) on the calling thread, write the settings file (outside
/// the lifecycle lock), and can wait up to <see cref="PluginHost.UnloadTimeout"/> for a stuck
/// handler. <b>Call them from a background thread, never the UI thread.</b> A quarantine takes the
/// same lock on a thread-pool thread, so a hung plugin delays other plugins' lifecycle operations
/// (not event delivery, and not <see cref="Statuses"/> / <see cref="GetStatus"/>, which never wait
/// on it) for that time. <see cref="StatusChanged"/> is raised after the lock is released, once
/// per operation, on the thread that performed it; a handler may call back into the supervisor.
/// </para>
/// <para>
/// <b>Reload caveat.</b> Reload unloads the plugin's collectible load context and loads the files
/// on disk afresh. The old assembly is only truly collected once nothing references it; a plugin
/// that leaked a thread, a timer or a static into the host keeps it alive (see
/// <see cref="PluginUnloadResult.Stuck"/>). The new copy loads regardless, so the rebuilt code
/// runs; the old one just lingers in memory until the process exits. Faults are attributed to the
/// plugin's current load: while a reload unloads the old copy its reports are dropped, and the new
/// copy's reports are counted from the moment it starts loading. The bridge's error callback does
/// not yet say which load a report came from, so a stuck old copy that is still running after the
/// new one has started loading can still be charged to it (callers that know the load can pass its
/// <see cref="GetGeneration"/> to <see cref="ReportFault(string, string, long)"/>). UI contributions
/// (<see cref="Abstractions.IUiRegistry"/>) are not yet implemented, so there are none to remove
/// on disable; they will be torn down with the plugin's context.
/// </para>
/// </remarks>
public sealed class PluginSupervisor : IDisposable
{
    /// <summary>Largest <see cref="PluginFaultPolicy.Threshold"/> accepted (it bounds the fault window's memory).</summary>
    public const int MaxThreshold = 100_000;

    private readonly PluginHost _host;
    private readonly IPluginEnablementStore _store;
    private readonly PluginFaultPolicy _policy;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan> _monotonic;

    private readonly object _gate = new();        // guards _entries only; never held across plugin code or I/O
    private readonly object _lifecycle = new();   // serialises load / unload / quarantine; no disk I/O under it
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _starting;

    private sealed class Entry
    {
        public string? Name;
        public PluginState State;
        public string? LastError;
        public DateTimeOffset? LastErrorAt;
        public long TotalFaults;
        public readonly Queue<TimeSpan> Window = new();
        public bool QuarantinePending;
        /// <summary>Bumped each time a load of this plugin begins.</summary>
        public long Generation;
        /// <summary>Whether fault reports count: false while the old copy unloads and once the plugin is not running.</summary>
        public bool Accepting;
    }

    /// <summary>
    /// Raised after a plugin's status changes, once per operation, with no supervisor lock held
    /// (exceptions swallowed).
    /// </summary>
    public event Action<PluginStatus>? StatusChanged;

    /// <param name="host">The host to supervise; its <see cref="PluginHost.LoadFilter"/> is replaced.</param>
    /// <param name="store">Where the commander's enable / disable choice lives.</param>
    /// <param name="policy">The quarantine policy (default: more than 10 failures in a minute).</param>
    /// <param name="clock">Wall clock, used only for <see cref="PluginStatus.LastErrorAt"/>.</param>
    /// <param name="monotonic">Monotonic time for the fault window (default: <see cref="System.Diagnostics.Stopwatch"/>).</param>
    public PluginSupervisor(PluginHost host, IPluginEnablementStore store, PluginFaultPolicy? policy = null,
        Func<DateTimeOffset>? clock = null, Func<TimeSpan>? monotonic = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(store);
        _policy = policy ?? new PluginFaultPolicy();
        if (_policy.Threshold < 1 || _policy.Threshold > MaxThreshold)
            throw new ArgumentOutOfRangeException(nameof(policy), $"Threshold must be between 1 and {MaxThreshold}.");
        if (_policy.Window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(policy), "Window must be positive.");
        _host = host;
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        var origin = System.Diagnostics.Stopwatch.GetTimestamp();
        _monotonic = monotonic ?? (() => System.Diagnostics.Stopwatch.GetElapsedTime(origin));
        host.LoadFilter = id => !store.IsDisabled(id);
    }

    /// <summary>Every known plugin and where it stands, ordered by id.</summary>
    public IReadOnlyList<PluginStatus> Statuses
    {
        get
        {
            lock (_gate)
                return _entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => Snapshot(e.Key, e.Value)).ToList();
        }
    }

    /// <summary>One plugin's status, or null if the id is unknown.</summary>
    public PluginStatus? GetStatus(string id)
    {
        lock (_gate)
            return _entries.TryGetValue(id, out var e) ? Snapshot(id, e) : null;
    }

    /// <summary>
    /// The number of the plugin's current (or latest) load, or 0 if unknown. Pass it to
    /// <see cref="ReportFault(string, string, long)"/> to have a report ignored if the plugin has
    /// been reloaded since.
    /// </summary>
    public long GetGeneration(string id)
    {
        lock (_gate)
            return _entries.TryGetValue(id, out var e) ? e.Generation : 0;
    }

    /// <summary>
    /// Runs <see cref="PluginHost.LoadAll"/> (skipping plugins the commander disabled) and records
    /// each result as a status. Fault reports made while plugins initialise are counted.
    /// </summary>
    /// <exception cref="InvalidOperationException">Plugins are already loaded or a load pass is running (see <see cref="PluginHost.LoadAll"/>).</exception>
    /// <exception cref="ObjectDisposedException">The supervisor or its host is disposed.</exception>
    public PluginDiscoveryReport Start()
    {
        ThrowIfDisposed();
        PluginDiscoveryReport report;
        var ids = new List<string>();
        var quarantine = new List<string>();
        lock (_lifecycle)
        {
            lock (_gate) _starting = true;
            try
            {
                report = _host.LoadAll();
            }
            finally
            {
                lock (_gate) _starting = false;
            }
            lock (_gate)
            {
                foreach (var result in report.Plugins)
                {
                    if (result.Manifest?.Id is not { } id) continue;
                    ids.Add(id);
                    if (ApplyLocked(id, result))
                        quarantine.Add(id);
                }
            }
        }
        foreach (var id in quarantine) ScheduleQuarantine(id);
        foreach (var id in ids) Raise(id);
        return report;
    }

    /// <summary>
    /// Persistently disables a plugin: its handlers are unsubscribed and it is unloaded. Returns
    /// false if the id is unknown. Idempotent. See the threading note on the class.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The supervisor is disposed.</exception>
    public bool Disable(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        ThrowIfDisposed();
        if (!Known(id)) return false;
        _store.SetDisabled(id, true);   // may write the settings file: outside every supervisor lock
        try
        {
            lock (_lifecycle)
            {
                // A concurrent Enable that persisted after us wins: the store is the truth.
                if (!_store.IsDisabled(id)) return true;
                Mutate(id, e => { e.Accepting = false; e.QuarantinePending = false; e.State = PluginState.Loading; });
                var unloadError = UnloadQuietly(id);
                Mutate(id, e =>
                {
                    e.State = PluginState.Disabled;
                    e.Window.Clear();
                    if (unloadError is not null) { e.LastError = unloadError; e.LastErrorAt = _clock(); }
                });
            }
        }
        finally
        {
            Raise(id);
        }
        return true;
    }

    /// <summary>
    /// Clears the persisted disable (and any quarantine) and loads the plugin from disk. Returns the
    /// load result, or null if the id is unknown, the plugin is already running, or a concurrent
    /// <see cref="Disable"/> superseded this call. See the threading note on the class.
    /// </summary>
    /// <exception cref="InvalidOperationException">A load pass is already running (see <see cref="PluginHost.LoadPlugin"/>); the plugin is then marked failed.</exception>
    /// <exception cref="ObjectDisposedException">The supervisor or its host is disposed.</exception>
    public PluginLoadResult? Enable(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        ThrowIfDisposed();
        if (!Known(id)) return null;
        _store.SetDisabled(id, false);   // may write the settings file: outside every supervisor lock
        try
        {
            lock (_lifecycle)
            {
                if (_store.IsDisabled(id)) return null;
                if (GetStatus(id)!.State == PluginState.Enabled)
                    return null;
                return LoadFresh(id, carriedError: null);
            }
        }
        finally
        {
            Raise(id);
        }
    }

    /// <summary>
    /// Unloads the plugin (if running) and loads whatever is on disk now, resetting its fault count
    /// and last error; this is how a rebuilt assembly is picked up and how a quarantined plugin is
    /// retried. A plugin the commander disabled stays disabled (returns null), as does an unknown
    /// id. See the threading note on the class.
    /// </summary>
    /// <exception cref="InvalidOperationException">A load pass is already running (see <see cref="PluginHost.LoadPlugin"/>); the plugin is then marked failed.</exception>
    /// <exception cref="ObjectDisposedException">The supervisor or its host is disposed.</exception>
    public PluginLoadResult? Reload(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        ThrowIfDisposed();
        try
        {
            lock (_lifecycle)
            {
                if (!Known(id) || _store.IsDisabled(id)) return null;
                // Pending before the unload: reports from the old copy stop counting now.
                Mutate(id, e => { e.Accepting = false; e.QuarantinePending = false; e.State = PluginState.Loading; });
                var unloadError = UnloadQuietly(id);
                return LoadFresh(id, unloadError);
            }
        }
        finally
        {
            if (Known(id)) Raise(id);
        }
    }

    /// <summary>
    /// The sink for <see cref="PluginBridgeOptions.HandlerError"/>. Safe to call from any thread
    /// (including a plugin's own dispatch thread); never throws and never blocks on plugin code.
    /// </summary>
    public void ReportHandlerError(PluginHandlerError error)
    {
        try
        {
            ReportFault(error.PluginId, $"handler for '{error.EventName}' threw {error.ExceptionType}: {error.Message}");
        }
        catch { /* an error sink must not fault the thread that reports to it */ }
    }

    /// <summary>
    /// Records one failed call into a plugin's code (any kind: handler, UI build, ...) against its
    /// current load, and quarantines the plugin if the rolling-window threshold is exceeded.
    /// </summary>
    public void ReportFault(string pluginId, string description) => ReportFaultCore(pluginId, description, null);

    /// <summary>
    /// As <see cref="ReportFault(string, string)"/>, but ignored unless <paramref name="generation"/>
    /// is still the plugin's current load (see <see cref="GetGeneration"/>).
    /// </summary>
    public void ReportFault(string pluginId, string description, long generation) => ReportFaultCore(pluginId, description, generation);

    private void ReportFaultCore(string pluginId, string description, long? generation)
    {
        var quarantine = false;
        try
        {
            var text = TextRules.ForDisplay(description, 500);
            lock (_gate)
            {
                if (_disposed) return;
                if (!_entries.TryGetValue(pluginId, out var e))
                {
                    // Start(): handlers can fail before the plugin has a status at all.
                    if (!_starting) return;
                    _entries[pluginId] = e = new Entry { State = PluginState.Loading, Generation = 1, Accepting = true };
                }
                // Only a running or loading plugin can be faulted: late reports from an
                // unloaded one (or from the copy a reload is replacing) are noise.
                if (!e.Accepting || (generation is { } g && g != e.Generation))
                    return;
                var now = _monotonic();
                e.TotalFaults++;
                e.LastError = text;
                e.LastErrorAt = _clock();
                e.Window.Enqueue(now);
                while (e.Window.Count > 0 && now - e.Window.Peek() > _policy.Window)
                    e.Window.Dequeue();
                // Bounded: only "more than Threshold" matters, so older entries are never needed.
                while (e.Window.Count > _policy.Threshold + 1)
                    e.Window.Dequeue();
                // While Loading the verdict waits for the load result, so a plugin is never quarantined mid-load.
                if (e.State == PluginState.Enabled && e.Window.Count > _policy.Threshold && !e.QuarantinePending)
                    quarantine = e.QuarantinePending = true;
            }
        }
        catch { /* never fault the reporting thread */ }
        if (quarantine)
            ScheduleQuarantine(pluginId);
    }

    private void ScheduleQuarantine(string id)
        => ThreadPool.UnsafeQueueUserWorkItem(_ => Quarantine(id), null);

    private void Quarantine(string id)
    {
        try
        {
            lock (_lifecycle)
            {
                lock (_gate)
                {
                    // Disabled / reloaded / disposed in the meantime: nothing to do.
                    if (_disposed || !_entries.TryGetValue(id, out var e) || e.State != PluginState.Enabled || !e.QuarantinePending)
                        return;
                    e.Accepting = false;
                }
                var unloadError = UnloadQuietly(id);
                Mutate(id, e =>
                {
                    e.State = PluginState.Quarantined;
                    e.QuarantinePending = false;
                    var message = $"quarantined: more than {_policy.Threshold} failures within {_policy.Window.TotalSeconds:0.##}s. Last error: {e.LastError}";
                    e.LastError = unloadError is null ? message : TextRules.ForDisplay(message + "; " + unloadError, 500);
                });
            }
            Raise(id);
        }
        catch { /* runs on the pool: nothing may escape */ }
    }

    /// <summary>Stops the supervisor: later lifecycle calls throw and fault reports are ignored. The host is not disposed.</summary>
    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        _host.LoadFilter = null;
    }

    // ---- internals ------------------------------------------------------------------------

    private void ThrowIfDisposed()
    {
        lock (_gate)
            ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool Known(string id)
    {
        lock (_gate) return _entries.ContainsKey(id);
    }

    /// <summary>Unloads the plugin; returns an error description if that reported problems.</summary>
    private string? UnloadQuietly(string id)
    {
        var unloaded = _host.Unload(id);
        return unloaded is { Errors.Count: > 0 }
            ? TextRules.ForDisplay("while unloading: " + string.Join("; ", unloaded.Errors), 500)
            : null;
    }

    /// <summary>Begins a new load (bumping the generation, so faults count from now), loads, and records the outcome. Caller holds the lifecycle lock.</summary>
    private PluginLoadResult LoadFresh(string id, string? carriedError)
    {
        Mutate(id, e =>
        {
            e.Generation++;
            e.State = PluginState.Loading;
            e.Accepting = true;
            e.QuarantinePending = false;
            e.Window.Clear();
            e.TotalFaults = 0;
            e.LastError = carriedError;
            e.LastErrorAt = carriedError is null ? null : _clock();
        });

        PluginLoadResult result;
        try
        {
            result = _host.LoadPlugin(id);
        }
        catch (Exception ex)
        {
            Mutate(id, e =>
            {
                e.State = PluginState.Failed;
                e.Accepting = false;
                e.LastError = TextRules.ForDisplay(PluginHost.Describe("loading failed", ex), 500);
                e.LastErrorAt = _clock();
            });
            throw;
        }

        bool quarantine;
        lock (_gate)
            quarantine = ApplyLocked(id, result);
        if (quarantine)
            ScheduleQuarantine(id);
        return result;
    }

    /// <summary>Records a load result. Caller holds <see cref="_gate"/>. Returns whether faults during the load already warrant quarantine.</summary>
    private bool ApplyLocked(string id, PluginLoadResult result)
    {
        if (!_entries.TryGetValue(id, out var e))
            _entries[id] = e = new Entry { Generation = 1 };
        e.Name = result.Manifest?.Name ?? e.Name;
        e.QuarantinePending = false;
        switch (result.Status)
        {
            case PluginLoadStatus.Loaded:
                e.State = PluginState.Enabled;
                e.Accepting = true;
                // Faults reported while it was loading were kept: count them now.
                if (e.Window.Count > _policy.Threshold)
                    return e.QuarantinePending = true;
                break;
            case PluginLoadStatus.Disabled:
                e.State = PluginState.Disabled;
                e.Accepting = false;
                e.Window.Clear();
                break;
            default:
                e.State = PluginState.Failed;
                e.Accepting = false;
                e.Window.Clear();
                e.LastError = TextRules.ForDisplay($"{result.Status}: {result.ReasonSummary}", 500);
                e.LastErrorAt = _clock();
                break;
        }
        return false;
    }

    private void Mutate(string id, Action<Entry> change)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var e))
                change(e);
        }
    }

    private static PluginStatus Snapshot(string id, Entry e)
        => new(id, e.Name, e.State, e.LastError, e.LastErrorAt, e.TotalFaults);

    private void Raise(string id)
    {
        var status = GetStatus(id);
        if (status is null) return;
        try { StatusChanged?.Invoke(status); }
        catch { /* a faulty subscriber must not break lifecycle */ }
    }
}
