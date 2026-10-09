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

    public static RavenResult<T> Ok(T? value) => new() { IsOk = true, Value = value };

    public static RavenResult<T> Failure(string message) => new() { IsOk = false, Error = message };
}
