using EliteDangerous.RavenColonial;

namespace EDNexus.Core.Colonisation;

/// <summary>
/// The shared project a delivery would be reported to, resolved from a construction depot.
/// </summary>
/// <param name="BuildId">The tracker's own id for the project, which is what a delivery is posted against.</param>
/// <param name="BuildName">Name the project was given on the shared tracker, for logs.</param>
/// <param name="Complete">Whether the tracker already considers the build finished.</param>
/// <param name="CommodityNames">
/// Canonical commodity symbol (see <see cref="CommodityName"/>) to the spelling the tracker itself
/// uses for that commodity — the only spelling a delivery may be posted under. A commodity the
/// project does not list has no entry, and so cannot be reported.
/// </param>
public sealed record SharedProjectTarget(
    string BuildId,
    string BuildName,
    bool Complete,
    IReadOnlyDictionary<string, string> CommodityNames);

/// <summary>
/// The outcome of resolving a depot to a <see cref="SharedProjectTarget"/>. A null
/// <see cref="Target"/> with a null <see cref="Error"/> is a definite "nobody tracks this depot"; a
/// non-null <see cref="Error"/> means the question could not be answered and may be asked again.
/// </summary>
public sealed record SharedProjectTargetResult(SharedProjectTarget? Target, string? Error = null);

/// <summary>
/// Reports a commander's own deliveries to the shared tracker. Backed by Raven Colonial in the app; an
/// interface so the sync service can be exercised without the network.
/// </summary>
public interface ISharedProjectContributor
{
    /// <summary>Finds the shared project registered against a construction depot.</summary>
    Task<SharedProjectTargetResult> FindTargetAsync(string systemName, long marketId, CancellationToken ct = default);

    /// <summary>
    /// Posts a delivery to a project.
    /// </summary>
    /// <param name="buildId">The project's <see cref="SharedProjectTarget.BuildId"/>.</param>
    /// <param name="commander">The commander the delivery is credited to.</param>
    /// <param name="deltas">Tracker-spelled commodity name to units delivered.</param>
    /// <returns>Null when the delivery was accepted, otherwise why it was not.</returns>
    Task<string?> ContributeAsync(
        string buildId, string commander, IReadOnlyDictionary<string, int> deltas, CancellationToken ct = default);
}

/// <summary>
/// The engine-side <see cref="ISharedProjectContributor"/> adapter over the reusable
/// <see cref="RavenColonialClient"/>.
/// </summary>
/// <remarks>
/// Raven Colonial's contribute endpoint declares no authentication: the commander is a plain path
/// segment, so the tracker cannot tell a real delivery from a spoofed one. This adapter therefore
/// never decides <em>whether</em> to send — that is the caller's opt-in — and only maps names.
/// </remarks>
public sealed class RavenColonialContributor : ISharedProjectContributor
{
    private readonly RavenColonialClient _client;

    public RavenColonialContributor(RavenColonialClient client) => _client = client;

    public async Task<SharedProjectTargetResult> FindTargetAsync(
        string systemName, long marketId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(systemName) || marketId <= 0) return new SharedProjectTargetResult(null);

        var found = await RavenProjectMatcher.FindAsync(_client, systemName, marketId, ct).ConfigureAwait(false);
        if (found.Error is not null) return new SharedProjectTargetResult(null, found.Error);
        if (found.Project is not { } project) return new SharedProjectTargetResult(null);

        // Match on the canonical form so the journal's "$Aluminium_name;" finds the tracker's
        // "aluminium" whatever case or wrapper it uses, but keep the tracker's own spelling to send.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var commodity in project.Remaining.Keys)
        {
            var key = CommodityName.Canonicalize(commodity);
            if (key.Length > 0) names.TryAdd(key, commodity);
        }

        return new SharedProjectTargetResult(new SharedProjectTarget(project.BuildId, project.BuildName, project.Complete, names));
    }

    public async Task<string?> ContributeAsync(
        string buildId, string commander, IReadOnlyDictionary<string, int> deltas, CancellationToken ct = default)
    {
        var result = await _client.ContributeAsync(buildId, commander, deltas, ct).ConfigureAwait(false);
        return result.IsOk ? null : result.Error ?? "unknown error";
    }
}

/// <summary>The shared "which project is this depot?" step used by both the read and the write adapters.</summary>
internal static class RavenProjectMatcher
{
    /// <summary>
    /// Two steps, because the by-depot endpoint is keyed on the game's id64 and the depot snapshot
    /// does not carry one: list what the system has, then pull the one whose market matches.
    /// <see cref="Match.Error"/> is set when the tracker could not be asked; a null
    /// <see cref="Match.Project"/> with no error means no such project.
    /// </summary>
    public static async Task<Match> FindAsync(
        RavenColonialClient client, string systemName, long marketId, CancellationToken ct)
    {
        var listed = await client.GetSystemProjectsAsync(systemName, ct).ConfigureAwait(false);
        if (!listed.IsOk) return new Match(null, listed.Error ?? "lookup failed");
        if (listed.Value is not { } refs) return new Match(null, null);

        var reference = refs.FirstOrDefault(r => r.MarketId == marketId);
        if (reference is null) return new Match(null, null);

        var result = await client.GetProjectAsync(reference.BuildId, ct).ConfigureAwait(false);
        return result.IsOk ? new Match(result.Value, null) : new Match(null, result.Error ?? "lookup failed");
    }

    public readonly record struct Match(RavenProject? Project, string? Error);
}
