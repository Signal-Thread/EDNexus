using System.Collections.Concurrent;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;
using EDNexus.Plugins.Hosting.Bridge;

namespace EDNexus.Cli;

/// <summary>
/// Headless plugin runner for <c>--plugins</c> / <c>--plugin &lt;path&gt;</c>: loads plugins through the
/// real <see cref="PluginHost"/> and <see cref="PluginBridge"/> against the CLI's engine, then
/// reports per-plugin results. The user is not asked for consent (there is no UI): every capability a
/// plugin declares is granted. UI and storage registrations are counted, not rendered or persisted.
/// </summary>
public sealed class PluginHarness : IDisposable
{
    private readonly PluginBridge _bridge;
    private readonly PluginHost _host;
    private readonly ConcurrentDictionary<string, PluginStats> _stats = new(StringComparer.Ordinal);
    private readonly string? _tempRoot;
    private PluginDiscoveryReport? _report;

    /// <param name="bus">The engine bus; create the harness before the replay starts.</param>
    /// <param name="state">The engine's commander state (read only).</param>
    /// <param name="pluginsRoot">Root to scan; ignored when <paramref name="singlePluginFolder"/> is set.</param>
    /// <param name="singlePluginFolder">Load only this plugin folder (copied to a scratch root first).</param>
    public PluginHarness(JournalEventBus bus, CommanderState state, string? pluginsRoot, string? singlePluginFolder)
    {
        string root;
        if (singlePluginFolder is not null)
        {
            var source = Path.GetFullPath(singlePluginFolder);
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException($"Plugin folder '{source}' does not exist.");
            // The host scans a root of <id>/ folders; stage just this one so siblings are not loaded.
            _tempRoot = Path.Combine(Path.GetTempPath(), "ednexus-cli-plugin-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(source, Path.Combine(_tempRoot, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
            root = _tempRoot;
            Root = source;
        }
        else
        {
            root = pluginsRoot ?? PluginPaths.Resolve() ?? Path.Combine(Path.GetTempPath(), "ednexus-no-plugins");
            Root = root;
        }

        _bridge = new PluginBridge(bus, state, new PluginBridgeOptions
        {
            HandlerError = err => _stats.GetOrAdd(err.PluginId, _ => new PluginStats())
                .AddError($"{err.EventName}: {err.ExceptionType}: {err.Message}"),
        });

        var appVersion = SemanticVersion.TryParse(Environment.GetEnvironmentVariable("EDNEXUS_APP_VERSION"), out var v) && v is not null
            ? v
            : SemanticVersion.Parse("0.0.1");
        _host = new PluginHost(root, appVersion, m => m.Capabilities.ToList(), BuildContext);
    }

    /// <summary>The path reported to the user (the single plugin folder, or the plugins root).</summary>
    public string Root { get; }

    private IPluginContext BuildContext(PluginManifest manifest, IReadOnlySet<string> granted)
    {
        var session = _bridge.Attach(manifest, granted);
        var stats = _stats.GetOrAdd(manifest.Id, _ => new PluginStats());
        stats.Session = session;
        return new HarnessContext(
            manifest,
            new CountingEvents(session.Events, stats),
            session.State,
            new HarnessLog(stats),
            granted.Contains(PluginCapabilities.Storage) ? new MemoryStorage(stats) : session.Storage,
            granted.Contains(PluginCapabilities.UiDashboard) || granted.Contains(PluginCapabilities.UiOverlay)
                ? new CountingUi(stats) : session.Ui,
            session);
    }

    /// <summary>Loads and initialises every plugin. Never throws for a bad plugin.</summary>
    public PluginDiscoveryReport Load() => _report = _host.LoadAll();

    /// <summary>Waits (bounded) for plugins' queued events to be delivered, after the replay.</summary>
    public void Drain(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && _stats.Values.Any(s => s.Session is { PendingEventCount: > 0 }))
            Thread.Sleep(20);
    }

    /// <summary>Writes the per-plugin summary.</summary>
    public void PrintSummary(TextWriter w)
    {
        w.WriteLine("\n======== Plugins ========");
        w.WriteLine($"  Source    : {Root}");
        if (_report is null) { w.WriteLine("  (not loaded)"); return; }
        foreach (var e in _report.Errors) w.WriteLine($"  ! {e}");
        if (_report.Plugins.Count == 0) { w.WriteLine("  No plugins found."); return; }

        foreach (var r in _report.Plugins)
        {
            var name = r.Manifest is { } m ? $"{m.Id} {m.Version}" : r.FolderName;
            w.WriteLine($"  {name}  [{r.Status}]");
            foreach (var reason in r.Reasons) w.WriteLine($"      reason  : {reason}");
            if (r.Manifest is null || r.Plugin is null) continue;

            var s = _stats.GetOrAdd(r.Manifest.Id, _ => new PluginStats());
            var errors = s.Errors;
            w.WriteLine($"      caps    : {(r.Manifest.Capabilities.Count == 0 ? "(none)" : string.Join(", ", r.Manifest.Capabilities))}");
            w.WriteLine($"      events  : {s.EventsSeen} seen, {s.Session?.DroppedEventCount ?? 0} dropped");
            w.WriteLine($"      ui      : {s.UiRegistrations} widget/panel registration(s) (counted, not rendered)");
            w.WriteLine($"      storage : {s.StorageWrites} write(s) (in-memory, discarded)");
            w.WriteLine($"      log     : {s.LogLines} line(s)");
            w.WriteLine($"      errors  : {errors.Count}");
            foreach (var err in errors.Take(10)) w.WriteLine($"          {err}");
            if (errors.Count > 10) w.WriteLine($"          ... and {errors.Count - 10} more");
        }
    }

    /// <summary>Unloads plugins (printing any unload problems) and removes scratch files.</summary>
    public void Dispose()
    {
        try
        {
            foreach (var u in _host.UnloadAll())
                foreach (var e in u.Errors)
                    Console.Error.WriteLine($"  [plugin {u.Id} unload] {e}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"  [plugin unload failed] {ex.Message}"); }
        _host.Dispose();
        _bridge.Dispose();
        if (_tempRoot is not null)
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private sealed class PluginStats
    {
        private readonly List<string> _errors = [];
        private long _events, _ui, _storage, _log;
        public PluginBridgeSession? Session { get; set; }
        public long EventsSeen => Interlocked.Read(ref _events);
        public long UiRegistrations => Interlocked.Read(ref _ui);
        public long StorageWrites => Interlocked.Read(ref _storage);
        public long LogLines => Interlocked.Read(ref _log);
        public List<string> Errors { get { lock (_errors) return [.. _errors]; } }
        public void AddError(string e) { lock (_errors) _errors.Add(e); }
        public void Event() => Interlocked.Increment(ref _events);
        public void Ui() => Interlocked.Increment(ref _ui);
        public void Storage() => Interlocked.Increment(ref _storage);
        public void Log() => Interlocked.Increment(ref _log);
    }

    private sealed class HarnessContext(
        PluginManifest manifest, IPluginEvents events, IReadOnlyCommanderState state,
        IPluginLog log, IPluginStorage storage, IUiRegistry ui, PluginBridgeSession session)
        : IPluginContext, IDisposable, IPluginSessionControl
    {
        public IReadOnlyCommanderState State => state;
        public IPluginEvents Events => events;
        public IPluginLog Log => log;
        public IPluginStorage Storage => storage;
        public IUiRegistry Ui => ui;
        public PluginManifest Manifest => manifest;
        public void Dispose() => session.Dispose();
        public bool WaitForExit(TimeSpan timeout) => session.WaitForExit(timeout);
    }

    /// <summary>Counts events actually delivered to the plugin's handlers.</summary>
    private sealed class CountingEvents(IPluginEvents inner, PluginStats stats) : IPluginEvents
    {
        private Action<IJournalEvent> Wrap(Action<IJournalEvent> h) => e => { stats.Event(); h(e); };
        public void Subscribe(string eventName, Action<IJournalEvent> handler) => inner.Subscribe(eventName, Wrap(handler));
        public void SubscribeAny(Action<IJournalEvent> handler) => inner.SubscribeAny(Wrap(handler));
        public IDisposable On(string eventName, Action<IJournalEvent> handler) => inner.On(eventName, Wrap(handler));
        public IDisposable OnAny(Action<IJournalEvent> handler) => inner.OnAny(Wrap(handler));
    }

    private sealed class HarnessLog(PluginStats stats) : IPluginLog
    {
        private void Write(string level, string m) { stats.Log(); Console.Error.WriteLine($"  [plugin {level}] {m}"); }
        public void Debug(string message) => Write("debug", message);
        public void Info(string message) => Write("info", message);
        public void Warn(string message) => Write("warn", message);
        public void Error(string message, Exception? exception = null)
        {
            stats.AddError("log: " + message + (exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})"));
            Write("error", message);
        }
    }

    private sealed class CountingUi(PluginStats stats) : IUiRegistry
    {
        public void Register(string id, object descriptor) => stats.Ui();
    }

    private sealed class MemoryStorage(PluginStats stats) : IPluginStorage
    {
        private readonly ConcurrentDictionary<string, string> _data = new();
        public string? GetString(string key) => _data.TryGetValue(key, out var v) ? v : null;
        public void SetString(string key, string value) { stats.Storage(); _data[key] = value; }
        public void Remove(string key) => _data.TryRemove(key, out _);
    }
}
