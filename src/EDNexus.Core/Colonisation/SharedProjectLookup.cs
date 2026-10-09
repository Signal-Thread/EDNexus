using EliteDangerous.RavenColonial;

namespace EDNexus.Core.Colonisation;

/// <summary>
/// The shared state of a construction project, as squadmates have left it — what the local depot
/// snapshot cannot tell a commander on its own.
/// </summary>
/// <param name="BuildName">Name the project was given on the shared tracker.</param>
/// <param name="SystemName">System the site sits in.</param>
/// <param name="Remaining">
/// Canonical commodity symbol (see <see cref="CommodityName"/>) to tons still needed, net of every
/// contributor's deliveries.
/// </param>
/// <param name="SumRemaining">Total tons still needed across all commodities.</param>
/// <param name="MaxNeed">Total tons the build needed to begin with.</param>
/// <param name="Complete">Whether the shared tracker considers the build finished.</param>
/// <param name="Architect">Commander who started the build, when named.</param>
/// <param name="Contributors">Commanders known to be working the project.</param>
/// <param name="SourceName">Where this came from, for the card footer.</param>
public sealed record SharedProject(
    string BuildName,
    string SystemName,
    IReadOnlyDictionary<string, int> Remaining,
    int SumRemaining,
    int MaxNeed,
    bool Complete,
    string? Architect,
    IReadOnlyList<string> Contributors,
    string SourceName);

/// <summary>
/// Looks up the shared, multi-commander view of a construction project. Backed by Raven Colonial in
/// the app; an interface so the colonisation card can be exercised without the network.
/// </summary>
public interface ISharedProjectLookup
{
    /// <summary>Human-readable source name for the card footer.</summary>
    string SourceName { get; }

    /// <summary>
    /// The shared project registered against a construction depot, or null when the depot is not
    /// tracked (which is the common case — most builds are solo and never registered).
    /// </summary>
    /// <remarks>
    /// Keyed by system name rather than the game's id64 because that is what the depot snapshot
    /// carries; the market id is what actually identifies the site within the system.
    /// </remarks>
    Task<SharedProject?> GetForDepotAsync(string systemName, long marketId, CancellationToken ct = default);
}

/// <summary>
/// The engine-side <see cref="ISharedProjectLookup"/> adapter over the reusable
/// <see cref="RavenColonialClient"/>. It maps Raven-shaped records onto the engine's
/// <see cref="SharedProject"/>, normalising commodity keys through <see cref="CommodityName"/> so
/// they line up with the depot's own resource symbols.
/// </summary>
/// <remarks>
/// Read-only on purpose. The API declares no authentication, so a delivery pushed up would be
/// attributed by nothing more than a commander name in the URL — writing under that model is a
/// decision for the commander, not a detail to slip into a read-side adapter. The opt-in write lives
/// in <see cref="RavenColonialContributor"/> and <see cref="RavenContributionSync"/>.
/// </remarks>
public sealed class RavenColonialProjectLookup : ISharedProjectLookup
{
    private readonly RavenColonialClient _client;

    public string SourceName => "Raven Colonial";

    public RavenColonialProjectLookup(RavenColonialClient client) => _client = client;

    public async Task<SharedProject?> GetForDepotAsync(
        string systemName, long marketId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(systemName) || marketId <= 0) return null;

        var found = await RavenProjectMatcher.FindAsync(_client, systemName, marketId, ct).ConfigureAwait(false);
        if (found.Project is not { } project) return null;

        var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (commodity, units) in project.Remaining)
        {
            // Raven keys commodities by the journal symbol; fold them through the same normaliser the
            // depot lines use so a caller can join the two without knowing either spelling.
            var key = CommodityName.Canonicalize(commodity);
            if (key.Length == 0) continue;
            remaining[key] = remaining.TryGetValue(key, out var already) ? already + units : units;
        }

        return new SharedProject(
            BuildName: project.BuildName,
            SystemName: project.SystemName,
            Remaining: remaining,
            SumRemaining: project.SumRemaining,
            MaxNeed: project.MaxNeed,
            Complete: project.Complete,
            Architect: project.Architect,
            Contributors: project.Contributors,
            SourceName: SourceName);
    }
}
