namespace EliteDangerous.RavenColonial;

/// <summary>
/// A construction project as Raven Colonial holds it — the shared view, aggregated across every
/// commander contributing to the build, which is the whole point of syncing with it.
/// </summary>
/// <param name="BuildId">Raven Colonial's own id for the project (a GUID).</param>
/// <param name="BuildName">Commander-given name for the build.</param>
/// <param name="BuildType">Site type key, e.g. "prometheus".</param>
/// <param name="SystemName">System the site sits in.</param>
/// <param name="SystemAddress">The game's system address (id64), or null when the project has none.</param>
/// <param name="MarketId">Construction-depot market id, which is how a local project is matched to this one.</param>
/// <param name="Remaining">
/// Commodity name (journal symbol, e.g. "cmmcomposite") to units still needed, already net of what
/// every contributor has delivered.
/// </param>
/// <param name="SumRemaining">Total units still needed across all commodities.</param>
/// <param name="MaxNeed">Total units the build needed to begin with.</param>
/// <param name="Complete">Whether the build is finished.</param>
/// <param name="Architect">Commander who started the build, when named.</param>
/// <param name="Contributors">Commander names known to be working the project.</param>
public sealed record RavenProject(
    string BuildId,
    string BuildName,
    string BuildType,
    string SystemName,
    long? SystemAddress,
    long? MarketId,
    IReadOnlyDictionary<string, int> Remaining,
    int SumRemaining,
    int MaxNeed,
    bool Complete,
    string? Architect,
    IReadOnlyList<string> Contributors);

/// <summary>A project as it appears in a system listing — the summary, without commodity totals.</summary>
public sealed record RavenProjectRef(
    string BuildId,
    string BuildName,
    string BuildType,
    string SystemName,
    long? SystemAddress,
    long? MarketId,
    bool Complete,
    string? Architect);

/// <summary>What <see cref="RavenColonialClient.ContributeAsync"/> actually sent for an accepted delivery.</summary>
/// <param name="BuildId">The project the delivery was posted to.</param>
/// <param name="Sent">Commodity name to units, after blank names and non-positive amounts were dropped.</param>
public sealed record RavenContribution(string BuildId, IReadOnlyDictionary<string, int> Sent);

/// <summary>
/// The result of a Raven Colonial call. Mirrors the EDSM, Spansh and Galnet clients' convention:
/// transport, HTTP and parse failures never throw — they surface as <see cref="IsOk"/> false with an
/// <see cref="Error"/>, and a successful-but-unknown project comes back as <see cref="IsOk"/> true
/// with a null <see cref="Value"/>.
/// </summary>
public sealed class RavenResult<T> where T : class
{
    /// <summary>True when the request reached Raven Colonial and parsed cleanly (even if nothing matched).</summary>
    public bool IsOk { get; init; }

    /// <summary>The parsed payload, or null when there was no project for the query.</summary>
    public T? Value { get; init; }

    /// <summary>Failure detail when <see cref="IsOk"/> is false; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>The HTTP status behind a failure, when the service answered at all (so a caller can tell a rejected key from an outage); null otherwise.</summary>
    public int? StatusCode { get; init; }

    public static RavenResult<T> Ok(T? value) => new() { IsOk = true, Value = value };

    public static RavenResult<T> Failure(string message, int? statusCode = null)
        => new() { IsOk = false, Error = message, StatusCode = statusCode };
}

/// <summary>
/// A fleet carrier as Raven Colonial holds it, with the cargo the squadron's tools have reported for it.
/// </summary>
/// <param name="MarketId">The carrier's market id (the same number as its <c>CarrierID</c> in the journal).</param>
/// <param name="Name">The carrier's callsign, e.g. "X8J-LVZ".</param>
/// <param name="DisplayName">The name its owner gave it, when Raven has it.</param>
/// <param name="Owner">The commander who owns it, when named.</param>
/// <param name="SystemName">The system it is in, when known.</param>
/// <param name="Cargo">Commodity (Raven's lower-case spelling, see <see cref="RavenCommodityKey"/>) to tons held; empty commodities omitted.</param>
public sealed record RavenCarrier(
    long MarketId,
    string Name,
    string? DisplayName,
    string? Owner,
    string? SystemName,
    IReadOnlyDictionary<string, int> Cargo);

/// <summary>What <see cref="RavenColonialClient.PatchCarrierCargoAsync"/> sent, and what Raven says the carrier holds afterwards.</summary>
/// <param name="Sent">Commodity (Raven's spelling) to signed tons, after merging and dropping zero changes.</param>
/// <param name="Totals">
/// The carrier's cargo as the reply reports it, in Raven's spelling. Empty when the reply carried none.
/// Comparing it with what was expected is how a second tool reporting the same cargo is noticed.
/// </param>
public sealed record RavenCarrierCargoUpdate(
    IReadOnlyDictionary<string, int> Sent,
    IReadOnlyDictionary<string, int> Totals);
