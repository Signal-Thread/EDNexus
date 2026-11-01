namespace EliteDangerous.EliteBgs;

/// <summary>A background-simulation state (Boom, War, Retreat, ...) with its trend where EliteBGS reports one.</summary>
/// <param name="State">The state name as EliteBGS spells it (lower-case, e.g. "boom").</param>
/// <param name="Trend">Trend value for pending/recovering states; null for active states.</param>
public sealed record BgsState(string State, int? Trend = null);

/// <summary>An open or recent conflict between two factions in a system.</summary>
public sealed record BgsConflict(
    string Type,
    string? Status,
    string? OpponentName,
    string? Stake,
    int? DaysWon);

/// <summary>
/// A faction's standing in one system: influence (a 0..1 fraction, clamped), happiness and simulation
/// states. Like every model in this file, the EliteBGS v5 field names behind it are unverified
/// against the live API (see <see cref="EliteBgsClient"/>).
/// </summary>
public sealed record BgsFactionPresence(
    string SystemName,
    string? State,
    double? Influence,
    string? Happiness,
    IReadOnlyList<BgsState> ActiveStates,
    IReadOnlyList<BgsState> PendingStates,
    IReadOnlyList<BgsState> RecoveringStates,
    IReadOnlyList<BgsConflict> Conflicts,
    DateTimeOffset? UpdatedAt);

/// <summary>A faction and every system it is present in.</summary>
public sealed record BgsFaction(
    string Name,
    string? Allegiance,
    string? Government,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<BgsFactionPresence> Presence);

/// <summary>One faction as listed inside a system, with its standing there.</summary>
public sealed record BgsSystemFaction(
    string Name,
    string? State,
    double? Influence,
    IReadOnlyList<BgsState> ActiveStates,
    IReadOnlyList<BgsState> PendingStates,
    IReadOnlyList<BgsState> RecoveringStates);

/// <summary>A system, its controlling faction and every faction present, highest influence first.</summary>
public sealed record BgsSystem(
    string Name,
    string? ControllingFaction,
    string? Allegiance,
    string? Government,
    string? Security,
    long? Population,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<BgsSystemFaction> Factions,
    IReadOnlyList<BgsConflict> Conflicts);

/// <summary>
/// The result of an EliteBGS lookup. Mirrors the Edsm/Spansh convention: transport, HTTP, consent and
/// parse failures never throw — they surface as <see cref="IsOk"/> false with an <see cref="Error"/>
/// (and <see cref="IsRateLimited"/> when the API pushed back), and a successful-but-unknown name comes
/// back as <see cref="IsOk"/> true with a null <see cref="Value"/>.
/// </summary>
public sealed class EliteBgsResult<T> where T : class
{
    /// <summary>True when the request reached EliteBGS (or the cache) and parsed cleanly, even if nothing matched.</summary>
    public bool IsOk { get; init; }

    /// <summary>The parsed payload, or null when EliteBGS had no data for the query.</summary>
    public T? Value { get; init; }

    /// <summary>Failure detail when <see cref="IsOk"/> is false; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>True when the failure was a rate-limit rejection (or the client's cool-down after one).</summary>
    public bool IsRateLimited { get; init; }

    /// <summary>True when the value was served from the in-memory cache rather than the network.</summary>
    public bool FromCache { get; init; }

    public static EliteBgsResult<T> Ok(T? value, bool fromCache = false) => new() { IsOk = true, Value = value, FromCache = fromCache };

    public static EliteBgsResult<T> Failure(string message, bool rateLimited = false) =>
        new() { IsOk = false, Error = message, IsRateLimited = rateLimited };
}
