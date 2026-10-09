using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting.Bridge;

/// <summary>
/// The read-only adapter between the engine and plugins: turns one engine's
/// <see cref="JournalEventBus"/> and <see cref="CommanderState"/> into the SDK's
/// <see cref="IPluginEvents"/> and <see cref="IReadOnlyCommanderState"/>, one
/// <see cref="PluginBridgeSession"/> per plugin. The plugin loader builds each plugin's
/// <see cref="IPluginContext"/> from a session's <see cref="PluginBridgeSession.Events"/> and
/// <see cref="PluginBridgeSession.State"/>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing a session hands out is, wraps publicly, or can be cast back to an engine type: plugins
/// get <see cref="IJournalEvent"/> views and a state view with no setters over immutable snapshots.
/// The bridge adds no mutation path — <c>StateTracker</c> stays the only writer. (This is
/// least-privilege plumbing, not a sandbox: in-process code can always use reflection.)
/// </para>
/// <para>
/// <b>Dispatch model.</b> Plugin handlers never run on the journal thread. Each plugin gets its own
/// bounded queue and background thread (see <see cref="IPluginEvents"/>), fed after every engine
/// subscriber has handled the entry. A handler that throws is reported through
/// <see cref="PluginBridgeOptions.HandlerError"/> with the plugin's id and delivery carries on;
/// a handler that blocks only delays its own plugin.
/// </para>
/// <para>
/// <b>State.</b> The bridge keeps one immutable snapshot of the commander as of the last completed
/// journal event, rebuilt on the journal thread once the engine has finished with each entry, and
/// every plugin's state view reads it. A plugin never sees an event half-applied, and by the time
/// its handler for an event runs the snapshot already includes that event.
/// </para>
/// <para>
/// A bridge is bound to one bus. The app rebuilds its <c>EngineHost</c> (and so its bus and
/// <c>CommanderState</c>) when leaving developer mode or resetting to live, so the loader must
/// dispose its sessions and this bridge, and attach new sessions to a bridge over the new host.
/// That rebuild is a requirement, not a nicety: once a snapshot has been built while developer
/// mode was on, this bridge keeps marking the commander as simulated (and keeps withholding it from
/// network-declaring plugins) for the rest of its life, because the fabricated state is still in
/// <c>CommanderState</c>. Dispose the developer-mode sources before turning
/// <see cref="PluginBridgeOptions.IsSimulated"/> off, too: the flag is sampled as each event is
/// queued, so an event published concurrently with the flip can be stamped live.
/// </para>
/// <para>
/// <b>What <c>state</c> and <c>events</c> reveal.</b> They are not anonymous telemetry. The
/// commander's in-game name and credit balance are readable through <c>state</c>
/// (<see cref="IReadOnlyCommanderState.Name"/>, <see cref="IReadOnlyCommanderState.Balance"/>) and
/// are also in the <c>LoadGame</c>, <c>Commander</c> and <c>Statistics</c> events; location, ship,
/// cargo and materials likewise. There is no finer-grained grant: a plugin granted <c>state</c> or
/// <c>events</c> together with <c>network</c> can send all of that off the machine (network access
/// cannot be enforced in-process). The consent screen must say so in those words.
/// </para>
/// </remarks>
public sealed class PluginBridge : IDisposable
{
    private readonly JournalEventBus _bus;
    private readonly CommanderStatePublisher _snapshots;
    private readonly PluginBridgeOptions _options;
    private readonly Func<bool> _isSimulated;

    /// <param name="bus">The engine bus to observe. The bridge only ever subscribes to it.</param>
    /// <param name="state">
    /// The engine's commander state. The bridge only ever reads it. Construct the bridge before the
    /// engine starts publishing: the first snapshot is taken here.
    /// </param>
    /// <param name="options">Dispatch and developer-mode options; defaults when null.</param>
    public PluginBridge(JournalEventBus bus, CommanderState state, PluginBridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(state);
        if (options is not null && options.QueueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "QueueCapacity must be at least 1.");
        if (options is not null && options.QueueByteCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "QueueByteCapacity must be at least 1.");
        _bus = bus;
        _options = options ?? new PluginBridgeOptions();
        _isSimulated = SafePredicate(_options.IsSimulated);
        // Must be registered here, before any session's completed hook, so each event's snapshot
        // is published before that event is queued to plugins.
        _snapshots = new CommanderStatePublisher(bus, state, _isSimulated);
    }

    /// <summary>
    /// Creates the event feed and state view for one plugin. Capabilities decide what is wired:
    /// without <see cref="PluginCapabilities.Events"/> the feed refuses every subscription, and
    /// without <see cref="PluginCapabilities.State"/> the state view refuses every read, both with
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    /// <remarks>
    /// For consent purposes <see cref="PluginCapabilities.Events"/> is data-equivalent to
    /// <see cref="PluginCapabilities.State"/>: the journal feed carries everything the state is
    /// derived from, so a plugin with <c>events</c> alone can rebuild the commander's location,
    /// balance and inventory. Present the two to the user accordingly.
    /// </remarks>
    /// <param name="manifest">The plugin's validated manifest; its id attributes errors.</param>
    /// <param name="grantedCapabilities">
    /// Required: the capabilities the user has granted (what <c>PluginHost</c>'s consent callback
    /// returned). There is no "grant everything declared" default — an empty collection grants
    /// nothing. Capabilities not declared by the manifest are ignored even if granted.
    /// </param>
    public PluginBridgeSession Attach(PluginManifest manifest, IEnumerable<string> grantedCapabilities)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(grantedCapabilities);
        var granted = new HashSet<string>(manifest.Capabilities, StringComparer.Ordinal);
        granted.IntersectWith(grantedCapabilities);

        // Developer mode feeds the bus fabricated events. EDDN, Inara, Discord and the Twitch card all
        // go silent while it is on, so a plugin that can phone home is held to the same rule: it sees
        // no simulated events and an unknown commander. Plugins that cannot phone home still see
        // everything, so their cards stay exercisable, with each event flagged IsSimulated.
        // This keys off what the manifest declares, not what is granted: network access cannot be
        // enforced in-process, so revoking the grant does not stop a plugin phoning home, and the
        // fabricated data must stay withheld (fail closed).
        var networked = manifest.Declares(PluginCapabilities.Network);
        // State is withheld per snapshot: each records whether it was built from developer-mode
        // events, so a fabricated commander stays hidden even if the predicate flips back before
        // this session is disposed (e.g. the app tears the dev-mode host down first).
        var onError = _options.HandlerError ?? (static _ => { });

        var events = granted.Contains(PluginCapabilities.Events)
            ? new PluginEvents(_bus, manifest.Id, networked, _isSimulated, onError, _options.QueueCapacity, _options.QueueByteCapacity)
            : null;

        var view = granted.Contains(PluginCapabilities.State)
            ? new CommanderStateView(_snapshots, networked, _isSimulated)
            : null;

        return new PluginBridgeSession(
            manifest.Id,
            events,
            events ?? (IPluginEvents)new DeniedPluginEvents(manifest.Id),
            view,
            view ?? (IReadOnlyCommanderState)new DeniedCommanderState(manifest.Id),
            new DeniedPluginStorage(manifest.Id),
            new DeniedUiRegistry(manifest.Id));
    }

    /// <summary>
    /// Stops refreshing the state snapshot, unhooks from the bus and state, and drops the bridge's
    /// references into the engine, so a state view a plugin kept hold of no longer pins the
    /// <see cref="CommanderState"/>. Dispose the sessions first; a view left attached keeps
    /// returning the last snapshot taken (still withheld from networked plugins if it was simulated).
    /// </summary>
    public void Dispose() => _snapshots.Dispose();

    /// <summary>
    /// Wraps the developer-mode predicate so it never throws onto the journal thread; a predicate
    /// that fails is treated as "simulated" (fail closed, the same way the reporters would).
    /// </summary>
    private static Func<bool> SafePredicate(Func<bool>? predicate)
    {
        if (predicate is null) return static () => false;
        return () =>
        {
            try { return predicate(); }
            catch { return true; }
        };
    }
}

/// <summary>Options for a <see cref="PluginBridge"/>.</summary>
public sealed class PluginBridgeOptions
{
    /// <summary>
    /// Live predicate: true while the bus is carrying developer-mode (fabricated) events. Evaluated on
    /// the journal thread as each event is published (and stamped onto that event's state
    /// snapshot), and on plugin threads as network-declaring plugins read state, so keep it cheap.
    /// A predicate that throws is treated as true. The app passes the same predicate it gives
    /// <c>EngineHost</c> as <c>reportingSuppressed</c>.
    /// </summary>
    public Func<bool>? IsSimulated { get; init; }

    /// <summary>
    /// Receives every exception a plugin handler throws, tagged with the plugin's id, so the host
    /// can log it and quarantine a repeat offender. Called on the plugin's dispatch thread.
    /// Exceptions it throws are swallowed.
    /// </summary>
    public Action<PluginHandlerError>? HandlerError { get; init; }

    /// <summary>
    /// How many undelivered (subscribed-to) events a plugin may fall behind before the oldest are
    /// dropped. The default comfortably holds a startup replay of a long session's journal. Bounds
    /// the count only; <see cref="QueueByteCapacity"/> bounds the memory.
    /// </summary>
    public int QueueCapacity { get; init; } = 8192;

    /// <summary>
    /// Approximately how many bytes of undelivered event payload a plugin may have queued before
    /// the oldest are dropped, whichever of this and <see cref="QueueCapacity"/> is reached first.
    /// Without it a plugin blocked in a handler, with a catch-all subscription, would retain up to
    /// <see cref="QueueCapacity"/> payloads, which for shipyard or outfitting dumps is hundreds of
    /// megabytes. An event larger than the whole budget is dropped on arrival. Default 16 MiB.
    /// </summary>
    public long QueueByteCapacity { get; init; } = 16L * 1024 * 1024;
}

/// <summary>A plugin event handler threw.</summary>
/// <remarks>
/// Text only, never the exception object: the exception belongs to the plugin, so a sink that kept
/// it (a log ring buffer, a "recent errors" list) would pin the plugin's load context and stop it
/// from ever unloading.
/// </remarks>
/// <param name="PluginId">The owning plugin's manifest id.</param>
/// <param name="EventName">The journal event being delivered.</param>
/// <param name="ExceptionType">The full name of the exception type the handler threw.</param>
/// <param name="Message">The exception's message, sanitised for display; never throws, even if the plugin's exception does.</param>
public sealed record PluginHandlerError(string PluginId, string EventName, string ExceptionType, string Message);

/// <summary>
/// One plugin's attachment to a <see cref="PluginBridge"/>: the <see cref="IPluginEvents"/> and
/// <see cref="IReadOnlyCommanderState"/> to put in its <see cref="IPluginContext"/>, plus delivery
/// diagnostics. Dispose it when the plugin unloads: that removes every handler the plugin
/// registered (so the bus holds no references into its <c>AssemblyLoadContext</c>), discards any
/// queued events, stops its dispatch thread, and cuts its state view off from the engine (it reads
/// as an unknown commander from then on). Handlers run on the session's own thread, so they can
/// still be running — concurrently with the plugin's <c>Shutdown</c>, and after <see cref="Dispose"/>
/// returns — until <see cref="WaitForExit"/> says otherwise.
/// </summary>
public sealed class PluginBridgeSession : IDisposable, IPluginSessionControl
{
    private readonly PluginEvents? _dispatcher;
    private readonly CommanderStateView? _view;

    internal PluginBridgeSession(
        string pluginId,
        PluginEvents? dispatcher,
        IPluginEvents events,
        CommanderStateView? view,
        IReadOnlyCommanderState state,
        IPluginStorage storage,
        IUiRegistry ui)
    {
        PluginId = pluginId;
        _dispatcher = dispatcher;
        _view = view;
        Events = events;
        State = state;
        Storage = storage;
        Ui = ui;
    }

    /// <summary>The plugin this session belongs to.</summary>
    public string PluginId { get; }

    /// <summary>The plugin's event feed, for <see cref="IPluginContext.Events"/>.</summary>
    public IPluginEvents Events { get; }

    /// <summary>The plugin's read-only state view, for <see cref="IPluginContext.State"/>.</summary>
    public IReadOnlyCommanderState State { get; }

    /// <summary>
    /// The plugin's storage, for <see cref="IPluginContext.Storage"/>. The bridge has no storage
    /// backend yet, so this always refuses (<see cref="UnauthorizedAccessException"/>) rather than
    /// letting a context factory hand the plugin a stub that silently discards its data; a real
    /// implementation replaces it for plugins granted <see cref="PluginCapabilities.Storage"/>.
    /// </summary>
    public IPluginStorage Storage { get; }

    /// <summary>
    /// The plugin's UI registry, for <see cref="IPluginContext.Ui"/>. The bridge has no UI
    /// contribution points yet, so this always refuses, as <see cref="Storage"/> does; a real
    /// implementation replaces it for plugins granted <see cref="PluginCapabilities.UiDashboard"/> or
    /// <see cref="PluginCapabilities.UiOverlay"/>.
    /// </summary>
    public IUiRegistry Ui { get; }

    /// <summary>
    /// Subscribed-to events dropped because the plugin fell <see cref="PluginBridgeOptions.QueueCapacity"/>
    /// events or <see cref="PluginBridgeOptions.QueueByteCapacity"/> bytes behind.
    /// </summary>
    public long DroppedEventCount => _dispatcher?.DroppedEventCount ?? 0;

    /// <summary>Handler invocations that threw, for quarantine decisions.</summary>
    public long HandlerErrorCount => _dispatcher?.HandlerErrorCount ?? 0;

    /// <summary>Events queued for the plugin but not yet delivered.</summary>
    public int PendingEventCount => _dispatcher?.PendingCount ?? 0;

    /// <summary>Approximate memory held by <see cref="PendingEventCount"/>.</summary>
    public long PendingEventBytes => _dispatcher?.PendingBytes ?? 0;

    /// <summary>
    /// After <see cref="Dispose"/>, waits up to <paramref name="timeout"/> for the dispatch thread to
    /// finish the handler it may be running. Returns false if the plugin is stuck in a handler:
    /// callers must then treat the plugin as <b>cannot unload</b>, because its code is still on a
    /// stack and unloading its <c>AssemblyLoadContext</c> will not complete.
    /// </summary>
    public bool WaitForExit(TimeSpan timeout) => _dispatcher?.WaitForExit(timeout) ?? true;

    /// <inheritdoc />
    public void Dispose()
    {
        _dispatcher?.Dispose();
        _view?.Revoke();
    }
}
