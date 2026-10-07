namespace EDNexus.Plugins.Abstractions;

/// <summary>
/// A read-only snapshot of the commander's current state, mirroring the host's internal
/// <c>CommanderState</c> read model. Only the host's <c>StateTracker</c> may mutate the
/// underlying state; plugins only ever see this read-only view.
/// </summary>
/// <remarks>
/// A stable surface that grows additively: members added after the initial SDK contract carry
/// default implementations, so neither plugins nor test doubles built against an older version
/// break. Further read-only projections (on-foot inventory, etc.) land alongside the work that
/// consumes them.
/// <para>
/// The host's view always reflects the commander as of the <b>last completed journal event</b>:
/// every member reads one immutable snapshot the host rebuilds after the engine has finished
/// applying each event, so no member ever shows an event half-applied. Two separate property reads
/// may still straddle an event; read them off one <see cref="Snapshot"/> when they must agree.
/// </para>
/// </remarks>
public interface IReadOnlyCommanderState
{
    /// <summary>
    /// The commander's in-game name, or <see langword="null"/> until known. Personally identifying,
    /// and covered by the same <c>state</c> grant as everything else here (there is no narrower one).
    /// </summary>
    string? Name { get; }

    /// <summary>Current credit balance. Sensitive, and covered by the same <c>state</c> grant as everything else here.</summary>
    long Balance { get; }

    /// <summary>The current ship type (internal symbol), or <see langword="null"/> until known.</summary>
    string? Ship { get; }

    /// <summary>The commander-assigned name of the current ship, or <see langword="null"/> if unset.</summary>
    string? ShipName { get => null; }

    /// <summary>The current star system, or <see langword="null"/> until known.</summary>
    string? StarSystem { get; }

    /// <summary>The current body within <see cref="StarSystem"/>, or <see langword="null"/> when not near one.</summary>
    string? Body { get => null; }

    /// <summary>Whether the commander is currently docked at a station or carrier.</summary>
    bool Docked { get; }

    /// <summary>
    /// The docked station's display name (a commander's own fleet carrier resolves to its given
    /// name rather than its callsign), or <see langword="null"/> when not docked.
    /// </summary>
    string? StationDisplayName { get => null; }

    /// <summary>UTC timestamp of the last event that updated this state.</summary>
    DateTimeOffset LastUpdated { get; }

    /// <summary>
    /// The cargo hold: commodity name to tons, as of the last completed journal event. An immutable
    /// copy, never the host's live collection, so mutating it (even via a cast) is impossible.
    /// </summary>
    IReadOnlyDictionary<string, int> Cargo { get => EmptyInventory.Instance; }

    /// <summary>Raw engineering materials: name to count. An immutable copy, like <see cref="Cargo"/>.</summary>
    IReadOnlyDictionary<string, int> RawMaterials { get => EmptyInventory.Instance; }

    /// <summary>Manufactured engineering materials: name to count. An immutable copy, like <see cref="Cargo"/>.</summary>
    IReadOnlyDictionary<string, int> ManufacturedMaterials { get => EmptyInventory.Instance; }

    /// <summary>Encoded engineering materials (data): name to count. An immutable copy, like <see cref="Cargo"/>.</summary>
    IReadOnlyDictionary<string, int> EncodedMaterials { get => EmptyInventory.Instance; }

    /// <summary>
    /// A consistent, immutable copy of every member of this state as of the last completed journal
    /// event. Separate reads off the view can straddle an event; read related values off one
    /// snapshot instead.
    /// Implementations that are already immutable may return themselves (the default).
    /// </summary>
    IReadOnlyCommanderState Snapshot() => this;
}

/// <summary>The shared empty inventory the default <see cref="IReadOnlyCommanderState"/> members return.</summary>
internal static class EmptyInventory
{
    /// <summary>An immutable, empty, ordinal-ignore-case dictionary.</summary>
    public static IReadOnlyDictionary<string, int> Instance { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}
