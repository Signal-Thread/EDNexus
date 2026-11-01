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
/// (<see cref="PluginFaultPolicy"/>); exceeding it quarantines the plugin. Nothing here ever lets a
/// plugin exception reach the journal thread or the UI thread: the sink only records and schedules.
/// </para>
/// <para>
/// Quarantine unloads the plugin on a thread-pool thread, never inline: the sink runs on the
/// plugin's own dispatch thread, and unloading waits for that thread to leave the plugin's code. A
/// plugin that never returns therefore delays only its own unload, never the pump or other plugins.
/// </para>
/// <para>
/// <b>Reload caveat.</b> Reload unloads the plugin's collectible load context and loads the files
/// on disk afresh. The old assembly is only truly collected once nothing references it; a plugin
/// that leaked a thread, a timer or a static into the host keeps it alive (see
/// <see cref="PluginUnloadResult.Stuck"/>). The new copy loads regardless, so the rebuilt code
/// runs; the old one just lingers in memory until the process exits. UI contributions
/// (<see cref="Abstractions.IUiRegistry"/>) are not yet implemented, so there are none to remove
/// on disable; they will be torn down with the plugin's context.
/// </para>
/// </remarks>
public sealed class PluginSupervisor : IDisposable
{
    private readonly PluginHost _host;
    private readonly IPluginEnablementStore _store;
    private readonly PluginFaultPolicy _policy;
    private readonly Func<DateTimeOffset> _clock;

    private readonly object _gate = new();        // guards _entries only; never held across plugin code
    private readonly object _lifecycle = new();   // serialises load / unload / quarantine
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    private sealed class Entry
    {
        public string? Name;
        public PluginState State;
        public string? LastError;
        public DateTimeOffset? LastErrorAt;
        public long TotalFaults;
        public readonly Queue<DateTimeOffset> Window = new();
        public bool QuarantinePending;
    }

    /// <summary>Raised (off any lock, exceptions swallowed) after a plugin's status changes.</summary>
    public event Action<PluginStatus>? StatusChanged;

    public PluginSupervisor(PluginHost host, IPluginEnablementStore store, PluginFaultPolicy? policy = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(store);
        _policy = policy ?? new PluginFaultPolicy();
        if (_policy.Threshold < 1)
            throw new ArgumentOutOfRangeException(nameof(policy), "Threshold must be at least 1.");
        if (_policy.Window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(policy), "Window must be positive.");
        _host = host;
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
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
    /// Runs <see cref="PluginHost.LoadAll"/> (skipping plugins the commander disabled) and records
    /// each result as a status.
    /// </summary>
    public PluginDiscoveryReport Start()
    {
        var report = _host.LoadAll();
        foreach (var result in report.Plugins)
        {
            if (result.Manifest?.Id is { } id)
                Apply(id, result);
        }
        return report;
    }

    /// <summary>
    /// Persistently disables a plugin: its handlers are unsubscribed and it is unloaded. Returns
    /// false if the id is unknown. Idempotent.
    /// </summary>
    public bool Disable(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_lifecycle)
        {
            if (!Known(id)) return false;
            _store.SetDisabled(id, true);
            UnloadQuietly(id);
            Mutate(id, e => { e.State = PluginState.Disabled; e.QuarantinePending = false; e.Window.Clear(); });
        }
        Raise(id);
        return true;
    }

    /// <summary>
    /// Clears the persisted disable (and any quarantine) and loads the plugin from disk. Returns the
    /// load result, or null if the id is unknown or the plugin is already running.
    /// </summary>
    public PluginLoadResult? Enable(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        PluginLoadResult? result;
        lock (_lifecycle)
        {
            if (!Known(id)) return null;
            _store.SetDisabled(id, false);
            if (GetStatus(id)!.State == PluginState.Enabled)
                return null;
            result = LoadFresh(id);
        }
        Raise(id);
        return result;
    }

    /// <summary>
    /// Unloads the plugin (if running) and loads whatever is on disk now, resetting its fault count;
    /// this is how a rebuilt assembly is picked up and how a quarantined plugin is retried. A plugin
    /// the commander disabled stays disabled (returns null), as does an unknown id.
    /// </summary>
    public PluginLoadResult? Reload(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        PluginLoadResult? result;
        lock (_lifecycle)
        {
            if (!Known(id) || _store.IsDisabled(id)) return null;
            UnloadQuietly(id);
            result = LoadFresh(id);
        }
        Raise(id);
        return result;
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
    /// Records one failed call into a plugin's code (any kind: handler, UI build, ...) and
    /// quarantines the plugin if the rolling-window threshold is exceeded.
    /// </summary>
    public void ReportFault(string pluginId, string description)
    {
        var text = TextRules.ForDisplay(description, 500);
        var now = _clock();
        var quarantine = false;
        lock (_gate)
        {
            // Only a running plugin can be faulted: late reports from an already-unloaded one are noise.
            if (_disposed || !_entries.TryGetValue(pluginId, out var e) || e.State != PluginState.Enabled)
                return;
            e.TotalFaults++;
            e.LastError = text;
            e.LastErrorAt = now;
            e.Window.Enqueue(now);
            while (e.Window.Count > 0 && now - e.Window.Peek() > _policy.Window)
                e.Window.Dequeue();
            if (e.Window.Count > _policy.Threshold && !e.QuarantinePending)
                quarantine = e.QuarantinePending = true;
        }
        if (quarantine)
            ThreadPool.UnsafeQueueUserWorkItem(_ => Quarantine(pluginId), null);
    }

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
                }
                UnloadQuietly(id);
                Mutate(id, e =>
                {
                    e.State = PluginState.Quarantined;
                    e.QuarantinePending = false;
                    e.LastError = $"quarantined: more than {_policy.Threshold} failures within {_policy.Window.TotalSeconds:0.##}s. Last error: {e.LastError}";
                });
            }
            Raise(id);
        }
        catch { /* runs on the pool: nothing may escape */ }
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        _host.LoadFilter = null;
    }

    // ---- internals ------------------------------------------------------------------------

    private bool Known(string id)
    {
        lock (_gate) return _entries.ContainsKey(id);
    }

    private void UnloadQuietly(string id)
    {
        var unloaded = _host.Unload(id);
        if (unloaded is { Errors.Count: > 0 })
            Mutate(id, e =>
            {
                e.LastError = TextRules.ForDisplay("while unloading: " + string.Join("; ", unloaded.Errors), 500);
                e.LastErrorAt = _clock();
            });
    }

    private PluginLoadResult LoadFresh(string id)
    {
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
                e.LastError = TextRules.ForDisplay(PluginHost.Describe("loading failed", ex), 500);
                e.LastErrorAt = _clock();
            });
            throw;
        }
        Apply(id, result, resetFaults: true);
        return result;
    }

    private void Apply(string id, PluginLoadResult result, bool resetFaults = false)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e))
                _entries[id] = e = new Entry();
            e.Name = result.Manifest?.Name ?? e.Name;
            e.QuarantinePending = false;
            e.Window.Clear();
            if (resetFaults) e.TotalFaults = 0;
            switch (result.Status)
            {
                case PluginLoadStatus.Loaded:
                    e.State = PluginState.Enabled;
                    break;
                case PluginLoadStatus.Disabled:
                    e.State = PluginState.Disabled;
                    break;
                default:
                    e.State = PluginState.Failed;
                    e.LastError = TextRules.ForDisplay($"{result.Status}: {result.ReasonSummary}", 500);
                    e.LastErrorAt = _clock();
                    break;
            }
        }
        Raise(id);
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
