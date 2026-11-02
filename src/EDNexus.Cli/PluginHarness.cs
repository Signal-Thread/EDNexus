using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;
using EDNexus.Plugins.Hosting.Bridge;

namespace EDNexus.Cli;

/// <summary>
/// Headless plugin runner for <c>--plugins</c> / <c>--plugin &lt;path&gt;</c>: loads plugins through the
/// real <see cref="PluginHost"/> and <see cref="PluginBridge"/> against the CLI's engine, then
/// reports per-plugin results.
/// <para>
/// Security: plugin code runs in-process with full trust, against the <em>real</em> journal being
/// replayed (the CMDR name and balance are visible to a plugin granted <c>state</c>). The user is
/// not asked for consent (there is no UI): every capability a plugin declares is granted, and a
/// warning is written to stderr for any plugin that declares <c>network</c> with <c>events</c> or
/// <c>state</c>. Only run plugins you trust.
/// </para>
/// <para>
/// Stand-ins: UI registrations are counted, not rendered, and storage is an in-memory stub with none
/// of the real backend's key validation or quotas (the summary says so).
/// </para>
/// </summary>
internal sealed class PluginHarness : IDisposable
{
    /// <summary>Longest wait for queued plugin events after a <c>--once</c> replay.</summary>
    public static readonly TimeSpan ReplayDrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Longest wait for queued plugin events when stopping a live session.</summary>
    public static readonly TimeSpan LiveDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the queues must stay empty before <see cref="Drain"/> believes it: an event is
    /// dequeued before its handler runs, so "empty" alone can mean "last handler still running".
    /// </summary>
    private static readonly TimeSpan DrainSettle = TimeSpan.FromMilliseconds(150);

    private const int MaxCopiedFiles = 2000;
    private const long MaxCopiedBytes = 256L * 1024 * 1024;
    private const int MaxStoredErrors = 100;
    private const int MaxPrintedErrors = 10;
    private const int MaxTextLength = 500;

    private readonly PluginBridge _bridge;
    private readonly PluginHost? _host;
    private readonly List<string> _problems = [];
    private readonly ConcurrentDictionary<string, PluginStats> _stats = new(StringComparer.Ordinal);
    private string? _tempRoot;
    private bool _disposed;
    private PluginDiscoveryReport? _report;

    /// <param name="bus">The engine bus; create the harness before the replay starts.</param>
    /// <param name="state">The engine's commander state (read only).</param>
    /// <param name="pluginsRoot">Root to scan; ignored when <paramref name="singlePluginFolder"/> is set.
    /// When both are null the default plugins folder is used; if there is none, nothing is loaded.</param>
    /// <param name="singlePluginFolder">Load only this plugin folder (copied to a scratch root first).</param>
    /// <param name="appVersion">App version plugins are gated against; defaults to <see cref="ResolveAppVersion"/>.</param>
    public PluginHarness(JournalEventBus bus, CommanderState state, string? pluginsRoot, string? singlePluginFolder, SemanticVersion? appVersion = null)
    {
        PluginBridge? bridge = null;
        try
        {
            string? root;
            if (singlePluginFolder is not null)
            {
                var source = Path.GetFullPath(singlePluginFolder);
                if (!Directory.Exists(source))
                    throw new DirectoryNotFoundException($"Plugin folder '{source}' does not exist.");
                var trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(trimmed);
                var pathRoot = (Path.GetPathRoot(source) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (name.Length == 0 || string.Equals(pathRoot, trimmed, StringComparison.Ordinal))
                    throw new ArgumentException($"'{source}' is a filesystem root, not a plugin folder.");
                // The host scans a root of <id>/ folders; stage just this one so siblings are not loaded.
                _tempRoot = Path.Combine(Path.GetTempPath(), "ednexus-cli-plugin-" + Guid.NewGuid().ToString("N"));
                CopyDirectory(source, Path.Combine(_tempRoot, name), new CopyBudget());
                root = _tempRoot;
                Root = source;
            }
            else
            {
                // No fallback to a shared folder such as %TEMP%: another local user could plant a
                // plugin there (see PluginPaths.Resolve). No per-user folder means no plugins.
                root = pluginsRoot ?? PluginPaths.Resolve();
                Root = root ?? "(no plugins folder)";
            }

            bridge = new PluginBridge(bus, state, new PluginBridgeOptions
            {
                // TODO(#243): route through the shared quarantine sink once it owns this hook.
                HandlerError = err => _stats.GetOrAdd(err.PluginId, _ => new PluginStats())
                    .AddError($"{err.EventName}: {err.ExceptionType}: {err.Message}"),
            });
            _bridge = bridge;

            if (root is not null)
                _host = new PluginHost(root, appVersion ?? ResolveAppVersion(), m => m.Capabilities.ToList(), BuildContext);
        }
        catch
        {
            bridge?.Dispose();
            DeleteTempRoot();
            throw;
        }
    }

    /// <summary>
    /// The EDNexus version plugins are gated against: <c>EDNEXUS_APP_VERSION</c> when set and valid,
    /// otherwise the entry assembly's informational version (as <c>EngineHost</c> does), minus any
    /// <c>+build</c> suffix.
    /// </summary>
    public static SemanticVersion ResolveAppVersion()
    {
        if (SemanticVersion.TryParse(Environment.GetEnvironmentVariable("EDNEXUS_APP_VERSION"), out var env) && env is not null)
            return env;
        var asm = Assembly.GetEntryAssembly() ?? typeof(PluginHarness).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString() ?? "0.0.0";
        var plus = info.IndexOf('+');
        if (plus >= 0) info = info[..plus];
        return SemanticVersion.TryParse(info, out var v) && v is not null ? v : SemanticVersion.Parse("0.0.0");
    }

    /// <summary>The path reported to the user (the single plugin folder, or the plugins root).</summary>
    public string Root { get; }

    /// <summary>The scratch root a <c>--plugin</c> copy was staged in, or null.</summary>
    public string? ScratchRoot => _tempRoot;

    /// <summary>Whether a plugins folder could be determined (false: nothing is loaded).</summary>
    public bool HasPluginsFolder => _host is not null;

    /// <summary>
    /// What a strict run fails on: non-Loaded plugins, handler/log errors, a drain timeout, unload
    /// problems. Complete once <see cref="Unload"/> (or <see cref="Dispose"/>) has run.
    /// </summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            List<string> all;
            lock (_problems) all = [.. _problems];
            if (_report is { } r)
            {
                foreach (var p in r.Plugins.Where(p => p.Status != PluginLoadStatus.Loaded))
                    all.Add($"{SanitizeText(p.Manifest?.Id ?? p.FolderName)}: {p.Status}");
                foreach (var p in r.Plugins.Where(p => p.Manifest is not null && p.Plugin is not null))
                {
                    var n = _stats.TryGetValue(p.Manifest!.Id, out var s) ? s.ErrorTotal : 0;
                    if (n > 0) all.Add($"{p.Manifest.Id}: {n} error(s)");
                }
            }
            return all;
        }
    }

    private IPluginContext BuildContext(PluginManifest manifest, IReadOnlySet<string> granted)
    {
        var session = _bridge.Attach(manifest, granted);
        var stats = _stats.GetOrAdd(manifest.Id, _ => new PluginStats());
        stats.Session = session;
        stats.UiSurfaces = string.Join("+", new[] { PluginCapabilities.UiDashboard, PluginCapabilities.UiOverlay }.Where(granted.Contains));
        if (granted.Contains(PluginCapabilities.Network)
            && (granted.Contains(PluginCapabilities.Events) || granted.Contains(PluginCapabilities.State)))
        {
            Console.Error.WriteLine(
                $"  [plugins] WARNING: '{SanitizeText(manifest.Id)}' declares network with events/state; it can send the replayed real journal/CMDR data off this machine (no consent prompt here).");
        }
        return new HarnessContext(
            manifest,
            new CountingEvents(session.Events, stats),
            session.State,
            new HarnessLog(stats),
            granted.Contains(PluginCapabilities.Storage) ? new MemoryStorage(stats) : session.Storage,
            // IUiRegistry.Register cannot tell a dashboard registration from an overlay one, so the
            // stub is offered when either surface was granted and the summary names which were.
            stats.UiSurfaces.Length > 0 ? new CountingUi(stats) : session.Ui,
            session);
    }

    /// <summary>
    /// Loads and initialises every plugin. Never throws for a bad plugin; returns null when there is
    /// no plugins folder. Note <see cref="PluginHost.LoadAll"/> first runs install recovery on the
    /// plugins root, which can move folders there, so a bare <c>--plugins</c> is not strictly read-only.
    /// </summary>
    public PluginDiscoveryReport? Load() => _report = _host?.LoadAll();

    /// <summary>
    /// Waits (bounded) for plugins' queued events to be delivered, after the replay. Returns false,
    /// after writing a warning to stderr, when <paramref name="timeout"/> expired first: the counts
    /// are then incomplete.
    /// </summary>
    public bool Drain(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        DateTime? emptySince = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_stats.Values.Any(s => s.Session is { PendingEventCount: > 0 }))
                emptySince = null;
            else
            {
                emptySince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - emptySince >= DrainSettle) return true;
            }
            Thread.Sleep(20);
        }
        const string msg = "plugin event drain timed out; counts may be incomplete";
        Console.Error.WriteLine($"  [plugins] {msg}");
        lock (_problems) _problems.Add(msg);
        return false;
    }

    /// <summary>Writes the per-plugin summary.</summary>
    public void PrintSummary(TextWriter w)
    {
        w.WriteLine("\n======== Plugins ========");
        w.WriteLine($"  Source    : {Root}");
        if (_host is null) { w.WriteLine("  No plugins folder could be determined; nothing was loaded."); return; }
        if (_report is null) { w.WriteLine("  (not loaded)"); return; }
        foreach (var e in _report.Errors) w.WriteLine($"  ! {SanitizeText(e)}");
        if (_report.Plugins.Count == 0) { w.WriteLine("  No plugins found."); return; }

        foreach (var r in _report.Plugins)
        {
            var name = r.Manifest is { } m ? $"{m.Id} {m.Version}" : r.FolderName;
            w.WriteLine($"  {SanitizeText(name)}  [{r.Status}]");
            foreach (var reason in r.Reasons) w.WriteLine($"      reason  : {SanitizeText(reason)}");
            if (r.Manifest is null || r.Plugin is null) continue;

            var s = _stats.GetOrAdd(r.Manifest.Id, _ => new PluginStats());
            var errors = s.Errors;
            w.WriteLine($"      caps    : {(r.Manifest.Capabilities.Count == 0 ? "(none)" : string.Join(", ", r.Manifest.Capabilities))}");
            w.WriteLine($"      events  : {s.EventsSeen} seen, {s.Session?.DroppedEventCount ?? 0} dropped");
            // TODO(#56): replace with the real dashboard/overlay registries.
            w.WriteLine($"      ui      : {s.UiRegistrations} registration(s) (stub: counted, not rendered; surfaces granted: {(s.UiSurfaces.Length == 0 ? "none" : s.UiSurfaces)}, not attributable per surface)");
            // TODO(#60): replace with the real storage backend (key validation, quotas).
            w.WriteLine($"      storage : {s.StorageWrites} write(s), {s.StorageRemoves} remove(s) (in-memory stub, discarded; real limits not applied)");
            w.WriteLine($"      log     : {s.LogLines} line(s)");
            w.WriteLine($"      errors  : {s.ErrorTotal}");
            foreach (var err in errors.Take(MaxPrintedErrors)) w.WriteLine($"          {err}");
            if (s.ErrorTotal > MaxPrintedErrors) w.WriteLine($"          ... and {s.ErrorTotal - MaxPrintedErrors} more");
        }
    }

    /// <summary>Unloads plugins, printing any unload problem to stderr and recording it. Idempotent.</summary>
    public void Unload()
    {
        if (_host is null) return;
        try
        {
            foreach (var u in _host.UnloadAll())
            {
                foreach (var e in u.Errors)
                {
                    Console.Error.WriteLine($"  [plugin {SanitizeText(u.Id)} unload] {SanitizeText(e)}");
                    lock (_problems) _problems.Add($"{SanitizeText(u.Id)}: unload: {SanitizeText(e)}");
                }
                if (u.Stuck)
                    lock (_problems) _problems.Add($"{SanitizeText(u.Id)}: handler stuck at unload");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [plugin unload failed] {SanitizeText(ex.Message)}");
            lock (_problems) _problems.Add("unload failed: " + SanitizeText(ex.Message));
        }
    }

    /// <summary>Unloads plugins and removes scratch files. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
        _host?.Dispose();
        _bridge.Dispose();
        DeleteTempRoot();
    }

    private void DeleteTempRoot()
    {
        if (_tempRoot is null) return;
        // The unloaded plugin assembly can stay locked briefly (Windows) until the load context is collected.
        for (var attempt = 0; attempt < 10 && Directory.Exists(_tempRoot); attempt++)
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
        }
        if (Directory.Exists(_tempRoot))
            Console.Error.WriteLine($"  [plugins] could not remove scratch folder '{_tempRoot}'.");
        _tempRoot = null;
    }

    /// <summary>Replaces control characters (terminal escapes) and caps length; for plugin-supplied text.</summary>
    internal static string SanitizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, MaxTextLength + 3));
        foreach (var c in text)
        {
            if (sb.Length >= MaxTextLength) { sb.Append("..."); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }

    private sealed class CopyBudget
    {
        public int Files;
        public long Bytes;
    }

    /// <summary>Bounded copy: skips reparse points (links/junctions), caps file count and total bytes.</summary>
    private static void CopyDirectory(string from, string to, CopyBudget budget)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            var info = new FileInfo(f);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            budget.Bytes += info.Length;
            if (++budget.Files > MaxCopiedFiles || budget.Bytes > MaxCopiedBytes)
                throw new InvalidOperationException($"Plugin folder is too large to stage (limit {MaxCopiedFiles} files / {MaxCopiedBytes / (1024 * 1024)} MB).");
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        }
        foreach (var d in Directory.GetDirectories(from))
        {
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)), budget);
        }
    }

    private sealed class PluginStats
    {
        private readonly List<string> _errors = [];
        private long _events, _ui, _storage, _storageRemoves, _log, _errorTotal;
        public PluginBridgeSession? Session { get; set; }
        public string UiSurfaces { get; set; } = "";
        public long EventsSeen => Interlocked.Read(ref _events);
        public long UiRegistrations => Interlocked.Read(ref _ui);
        public long StorageWrites => Interlocked.Read(ref _storage);
        public long StorageRemoves => Interlocked.Read(ref _storageRemoves);
        public long LogLines => Interlocked.Read(ref _log);
        public long ErrorTotal => Interlocked.Read(ref _errorTotal);
        public List<string> Errors { get { lock (_errors) return [.. _errors]; } }
        public void AddError(string e)
        {
            Interlocked.Increment(ref _errorTotal);
            lock (_errors)
                if (_errors.Count < MaxStoredErrors) _errors.Add(SanitizeText(e));
        }
        public void Event() => Interlocked.Increment(ref _events);
        public void Ui() => Interlocked.Increment(ref _ui);
        public void Storage() => Interlocked.Increment(ref _storage);
        public void StorageRemove() => Interlocked.Increment(ref _storageRemoves);
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
        private void Write(string level, string m) { stats.Log(); Console.Error.WriteLine($"  [plugin {level}] {SanitizeText(m)}"); }
        public void Debug(string message) => Write("debug", message);
        public void Info(string message) => Write("info", message);
        public void Warn(string message) => Write("warn", message);
        public void Error(string message, Exception? exception = null)
        {
            stats.AddError("log: " + message + (exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})"));
            Write("error", message);
        }
    }

    /// <summary>Stub UI registry: counts registrations. TODO(#56): replace with the real registries.</summary>
    private sealed class CountingUi(PluginStats stats) : IUiRegistry
    {
        public void Register(string id, object descriptor) => stats.Ui();
    }

    /// <summary>In-memory storage stub, no real limits. TODO(#60): replace with the real backend.</summary>
    private sealed class MemoryStorage(PluginStats stats) : IPluginStorage
    {
        private readonly ConcurrentDictionary<string, string> _data = new();
        public string? GetString(string key) => _data.TryGetValue(key, out var v) ? v : null;
        public void SetString(string key, string value) { stats.Storage(); _data[key] = value; }
        public void Remove(string key) { stats.StorageRemove(); _data.TryRemove(key, out _); }
    }
}

/// <summary>Runs an action once on dispose (a <c>using</c> guard for a variable assigned later).</summary>
internal sealed class DisposeAction(Action action) : IDisposable
{
    public void Dispose() => action();
}
