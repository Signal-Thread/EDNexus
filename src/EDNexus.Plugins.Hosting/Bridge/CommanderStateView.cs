using System.Collections.Concurrent;
using System.Collections.Frozen;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting.Bridge;

/// <summary>
/// Keeps an immutable <see cref="CommanderStateSnapshot"/> of the engine's state as of the last
/// completed journal event, for every plugin attached to one bridge.
/// </summary>
/// <remarks>
/// <para>
/// Plugins read state from their own threads, while <c>StateTracker</c> mutates
/// <see cref="CommanderState"/> on the journal thread — rebuilding inventories as <c>Clear()</c>
/// plus adds, and changing related scalars (system, body) one property at a time. Reading the live
/// object from a plugin thread could therefore observe an event half-applied. Instead, the
/// snapshot is rebuilt on the journal thread from <see cref="JournalEventBus.SubscribeCompleted"/>,
/// once every engine handler has finished with the entry, and published with a single volatile
/// write. Readers only ever see a whole snapshot.
/// </para>
/// <para>
/// Collections are re-frozen only when <see cref="CommanderState.CargoChanged"/> or
/// <see cref="CommanderState.MaterialsChanged"/> fired during the event (observed through read-only
/// subscriptions); otherwise the previous frozen copies are reused, so the per-event cost is a
/// handful of scalar reads.
/// </para>
/// <para>
/// Each snapshot also records whether it was built while the bus was carrying developer-mode
/// (fabricated) events (<see cref="CommanderStateSnapshot.Simulated"/>). The developer-mode predicate
/// is evaluated once, when the snapshot is built, so a snapshot of a fabricated commander stays
/// marked as such even after the predicate flips back — whatever order the app tears the old host,
/// the predicate and this bridge's sessions down in.
/// </para>
/// <para>
/// <see cref="Publish"/> is serialised, so when two threads publish on the same bus (developer
/// mode's Randomize on the UI thread, the journal watcher on the thread pool) the last snapshot
/// written is the one built last, after the latest completed event. That only orders the
/// snapshots: <c>StateTracker</c> itself is not synchronised across publishers, so two events
/// applied concurrently can still interleave in <see cref="CommanderState"/>, and a snapshot taken
/// then reflects that interleaving.
/// </para>
/// <para>
/// The first snapshot is taken at construction. Build the bridge before the engine starts pumping
/// (or accept that it is corrected by the next event). After <see cref="Dispose"/> the publisher
/// drops every reference into the engine; <see cref="Current"/> keeps returning the last snapshot.
/// </para>
/// </remarks>
internal sealed class CommanderStatePublisher : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<bool> _isSimulated;
    private CommanderState? _state;
    private IDisposable? _hook;
    private CommanderStateSnapshot _current;
    private IReadOnlyDictionary<string, int> _cargo;
    private IReadOnlyDictionary<string, int> _raw;
    private IReadOnlyDictionary<string, int> _manufactured;
    private IReadOnlyDictionary<string, int> _encoded;
    private int _cargoDirty;
    private int _materialsDirty;

    /// <param name="bus">The engine bus; the publisher hooks its completed-event stage.</param>
    /// <param name="state">The engine's commander state, only ever read.</param>
    /// <param name="isSimulated">
    /// The developer-mode predicate, stamped onto each snapshot as it is built. Must not throw
    /// (the bridge wraps it to fail closed).
    /// </param>
    public CommanderStatePublisher(JournalEventBus bus, CommanderState state, Func<bool> isSimulated)
    {
        _state = state;
        _isSimulated = isSimulated;
        _cargo = CommanderStateSnapshot.Freeze(state.Cargo);
        _raw = CommanderStateSnapshot.Freeze(state.Materials.Raw);
        _manufactured = CommanderStateSnapshot.Freeze(state.Materials.Manufactured);
        _encoded = CommanderStateSnapshot.Freeze(state.Materials.Encoded);
        _current = Build(state);

        state.CargoChanged += MarkCargoDirty;
        state.MaterialsChanged += MarkMaterialsDirty;
        _hook = bus.SubscribeCompleted(_ => Publish());
    }

    /// <summary>The snapshot as of the last completed event.</summary>
    public CommanderStateSnapshot Current => Volatile.Read(ref _current);

    private void MarkCargoDirty() => Volatile.Write(ref _cargoDirty, 1);

    private void MarkMaterialsDirty() => Volatile.Write(ref _materialsDirty, 1);

    /// <summary>Runs on the publishing thread after every engine handler has seen the entry.</summary>
    private void Publish()
    {
        lock (_gate)
        {
            if (_state is not { } state) return;   // disposed
            if (Interlocked.Exchange(ref _cargoDirty, 0) == 1)
                _cargo = CommanderStateSnapshot.Freeze(state.Cargo);
            if (Interlocked.Exchange(ref _materialsDirty, 0) == 1)
            {
                _raw = CommanderStateSnapshot.Freeze(state.Materials.Raw);
                _manufactured = CommanderStateSnapshot.Freeze(state.Materials.Manufactured);
                _encoded = CommanderStateSnapshot.Freeze(state.Materials.Encoded);
            }
            Volatile.Write(ref _current, Build(state));
        }
    }

    private CommanderStateSnapshot Build(CommanderState state) => new()
    {
        Name = state.Name,
        Balance = state.Balance,
        Ship = state.Ship,
        ShipName = state.ShipName,
        StarSystem = state.StarSystem,
        Body = state.Body,
        Docked = state.Docked,
        StationDisplayName = state.StationDisplayName,
        LastUpdated = state.LastUpdated,
        Cargo = _cargo,
        RawMaterials = _raw,
        ManufacturedMaterials = _manufactured,
        EncodedMaterials = _encoded,
        Simulated = _isSimulated(),
    };

    /// <summary>
    /// Unhooks from the bus and state and drops both references, so a view that outlives the
    /// bridge pins only immutable snapshots, not the engine.
    /// </summary>
    public void Dispose()
    {
        CommanderState? state;
        IDisposable? hook;
        lock (_gate)
        {
            state = _state;
            hook = _hook;
            _state = null;
            _hook = null;
        }
        hook?.Dispose();
        if (state is null) return;
        state.CargoChanged -= MarkCargoDirty;
        state.MaterialsChanged -= MarkMaterialsDirty;
    }
}

/// <summary>
/// The <see cref="IReadOnlyCommanderState"/> a plugin with the <c>state</c> capability sees. Every
/// member reads the bridge's latest published <see cref="CommanderStateSnapshot"/> — state as of
/// the last completed journal event. There is no setter and no reference to the live
/// <see cref="CommanderState"/>, so <c>StateTracker</c> stays the only writer. Separate property
/// reads may straddle an event; <see cref="Snapshot"/> returns one consistent snapshot.
/// </summary>
/// <param name="publisher">The bridge's snapshot source. Dropped on <see cref="Revoke"/>.</param>
/// <param name="networked">
/// Whether the plugin declares <c>network</c>. A networked view reads as an unknown commander
/// whenever the snapshot was built from developer-mode events
/// (<see cref="CommanderStateSnapshot.Simulated"/>) or <paramref name="isSimulated"/> is true now
/// (see <see cref="PluginBridgeOptions.IsSimulated"/>). Either is enough: the snapshot flag keeps a
/// fabricated commander hidden after the predicate flips back, and the live check hides the last
/// live snapshot as soon as developer mode turns on.
/// </param>
/// <param name="isSimulated">The developer-mode predicate. Must not throw.</param>
internal sealed class CommanderStateView(CommanderStatePublisher publisher, bool networked, Func<bool> isSimulated)
    : IReadOnlyCommanderState
{
    private CommanderStatePublisher? _publisher = publisher;

    private CommanderStateSnapshot Current
    {
        get
        {
            if (Volatile.Read(ref _publisher) is not { } source) return CommanderStateSnapshot.Unknown;
            var snapshot = source.Current;
            return networked && (snapshot.Simulated || isSimulated()) ? CommanderStateSnapshot.Unknown : snapshot;
        }
    }

    /// <summary>
    /// Cuts the view off from the engine (on session dispose): from then on it reads as an unknown
    /// commander, and a plugin that kept hold of it no longer pins the engine's object graph.
    /// </summary>
    public void Revoke() => Volatile.Write(ref _publisher, null);

    public string? Name => Current.Name;
    public long Balance => Current.Balance;
    public string? Ship => Current.Ship;
    public string? ShipName => Current.ShipName;
    public string? StarSystem => Current.StarSystem;
    public string? Body => Current.Body;
    public bool Docked => Current.Docked;
    public string? StationDisplayName => Current.StationDisplayName;
    public DateTimeOffset LastUpdated => Current.LastUpdated;
    public IReadOnlyDictionary<string, int> Cargo => Current.Cargo;
    public IReadOnlyDictionary<string, int> RawMaterials => Current.RawMaterials;
    public IReadOnlyDictionary<string, int> ManufacturedMaterials => Current.ManufacturedMaterials;
    public IReadOnlyDictionary<string, int> EncodedMaterials => Current.EncodedMaterials;

    public IReadOnlyCommanderState Snapshot() => Current;
}

/// <summary>
/// An immutable point-in-time copy of <see cref="CommanderState"/>. The collections are
/// <see cref="FrozenDictionary{TKey,TValue}"/> copies, which reject mutation even through a cast to
/// <see cref="IDictionary{TKey,TValue}"/>.
/// </summary>
internal sealed class CommanderStateSnapshot : IReadOnlyCommanderState
{
    private static readonly IReadOnlyDictionary<string, int> Empty =
        FrozenDictionary<string, int>.Empty.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The snapshot of a commander nothing is known about.</summary>
    public static CommanderStateSnapshot Unknown { get; } = new();

    public string? Name { get; internal init; }
    public long Balance { get; internal init; }
    public string? Ship { get; internal init; }
    public string? ShipName { get; internal init; }
    public string? StarSystem { get; internal init; }
    public string? Body { get; internal init; }
    public bool Docked { get; internal init; }
    public string? StationDisplayName { get; internal init; }
    public DateTimeOffset LastUpdated { get; internal init; }
    public IReadOnlyDictionary<string, int> Cargo { get; internal init; } = Empty;
    public IReadOnlyDictionary<string, int> RawMaterials { get; internal init; } = Empty;
    public IReadOnlyDictionary<string, int> ManufacturedMaterials { get; internal init; } = Empty;
    public IReadOnlyDictionary<string, int> EncodedMaterials { get; internal init; } = Empty;

    /// <summary>
    /// Whether the bus was carrying developer-mode (fabricated) events when this snapshot was built.
    /// Not part of the plugin-facing interface; networked views use it to withhold the snapshot.
    /// </summary>
    internal bool Simulated { get; init; }

    public IReadOnlyCommanderState Snapshot() => this;

    /// <summary>
    /// A frozen copy of an engine inventory. Only consistent when called on the journal thread
    /// between events (as <see cref="CommanderStatePublisher"/> does): mid-event, a
    /// <c>Clear()</c>-and-refill rebuild can be observed half done.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Freeze(ConcurrentDictionary<string, int> live)
        => live.IsEmpty ? Empty : live.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}
