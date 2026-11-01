using System.Globalization;
using System.Text.Json;

namespace EliteDangerous.Inara;

/// <summary>Outcome category of an <see cref="InaraReadClient"/> call.</summary>
public enum InaraReadStatus
{
    /// <summary>Data was returned (fresh, cached, or stale-on-error). Check <see cref="InaraReadResult{T}.IsStale"/> and <see cref="InaraReadResult{T}.Age"/>.</summary>
    Ok,
    /// <summary>Inara answered but has no matching data.</summary>
    NotFound,
    /// <summary>The consent gate or a missing API key blocked the call; no request was made and no cached data is served.</summary>
    Disabled,
    /// <summary>Rate limited (by Inara or by our own local limiter) and nothing usable cached to serve.</summary>
    RateLimited,
    /// <summary>Bad API key or another non-retryable refusal.</summary>
    Rejected,
    /// <summary>Network, transport, size-limit, invalid input, or parse/shape failure and nothing usable cached to serve.</summary>
    Error,
    /// <summary>The caller cancelled the call (or the client was disposed).</summary>
    Cancelled,
}

/// <summary>Result of a read call. The client never throws; inspect <see cref="Status"/>.</summary>
/// <param name="Status">Outcome category.</param>
/// <param name="Value">The data when <see cref="Status"/> is <see cref="InaraReadStatus.Ok"/>.</param>
/// <param name="FromCache">True when served from the local cache (fresh or stale).</param>
/// <param name="IsStale">True when the cached value is past its TTL and was served because a refresh failed.</param>
/// <param name="Message">Short, sanitised, length-capped diagnostic. Never contains the API key.</param>
/// <param name="RetryAfter">How long until a live request will be allowed, when known.</param>
/// <param name="Age">How long ago the value was fetched (monotonic). Null when there is no value.</param>
/// <param name="FetchedAt">Wall-clock time the value was fetched, for "as of ..." display. Null when there is no value.</param>
public sealed record InaraReadResult<T>(
    InaraReadStatus Status, T? Value, bool FromCache = false, bool IsStale = false, string? Message = null,
    TimeSpan? RetryAfter = null, TimeSpan? Age = null, DateTimeOffset? FetchedAt = null)
{
    /// <summary>True when <see cref="Value"/> is usable (fresh, cached, or stale-on-error).</summary>
    public bool HasValue => Status == InaraReadStatus.Ok && Value is not null;
}

/// <summary>A commander's public Inara profile.</summary>
/// <remarks>Response field names are unverified best-effort guesses against Inara's docs; see <see cref="InaraReadClient"/>. Members may be null when Inara omits them, and the shape may change.</remarks>
public sealed record InaraCommanderProfile(
    string? CommanderName, string? InaraName, string? InaraUrl,
    string? SquadronName, string? SquadronRank, string? Allegiance, string? Power,
    IReadOnlyList<InaraRank> Ranks, IReadOnlyList<InaraReputation> Reputations);

/// <summary>A pilot rank entry. <see cref="Progress"/> is 0..1 when supplied.</summary>
/// <remarks>Response field names are unverified best-effort guesses; see <see cref="InaraReadClient"/>.</remarks>
public sealed record InaraRank(string Name, int? Value, double? Progress);

/// <summary>A major-faction reputation (-100..100).</summary>
/// <remarks>Response field names are unverified best-effort guesses; see <see cref="InaraReadClient"/>.</remarks>
public sealed record InaraReputation(string Faction, double? Reputation);

/// <summary>Kinds of service for <see cref="InaraReadClient.GetNearestServicesAsync"/>.</summary>
/// <remarks>The wire <c>serviceType</c> codes these map to are unverified best-effort guesses.</remarks>
public enum InaraServiceKind { Engineer, MaterialTrader, TechnologyBroker, InterstellarFactors, Shipyard, Outfitting }

/// <summary>A station or engineer base near a reference system.</summary>
/// <remarks>Response field names are unverified best-effort guesses; see <see cref="InaraReadClient"/>.</remarks>
public sealed record InaraNearbyService(string? Name, string? StarSystem, double? DistanceLy, double? DistanceLs, string? StationType, long? MarketId);

/// <summary>An engineer with unlock requirements and the blueprints they offer.</summary>
/// <remarks>Response field names are unverified best-effort guesses; see <see cref="InaraReadClient"/>.</remarks>
public sealed record InaraEngineer(string Name, string? StarSystem, string? Station, string? UnlockRequirements, IReadOnlyList<InaraBlueprint> Blueprints);

/// <summary>An engineering blueprint with its material list.</summary>
/// <remarks>Response field names are unverified best-effort guesses; see <see cref="InaraReadClient"/>.</remarks>
public sealed record InaraBlueprint(string Name, int? Grade, IReadOnlyList<InaraMaterialCost> Materials);

/// <summary>A material requirement. <see cref="Count"/> is null when Inara's count was missing or out of range (never silently zero).</summary>
/// <remarks>Response field names are unverified best-effort guesses; see <see cref="InaraReadClient"/>.</remarks>
public sealed record InaraMaterialCost(string Name, int? Count);

/// <summary>An active community goal.</summary>
/// <remarks>Response field names are unverified best-effort guesses; see <see cref="InaraReadClient"/>.</remarks>
public sealed record InaraCommunityGoal(
    long? Id, string? Title, string? StarSystem, string? Station, DateTimeOffset? Expires,
    bool IsCompleted, long? ContributorsNum, string? Objective, IReadOnlyList<InaraGoalTier> Tiers);

/// <summary>One reward tier of a community goal.</summary>
/// <remarks>Response field names are unverified best-effort guesses; see <see cref="InaraReadClient"/>.</remarks>
public sealed record InaraGoalTier(string? Name, long? Target, string? Reward);

internal static class InaraJson
{
    public static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string? First(JsonElement e, params string[] names)
    {
        foreach (var n in names) if (Str(e, n) is { Length: > 0 } s) return s;
        return null;
    }

    public static double? Dbl(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    public static long? Lng(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var d) ? d : null;

    /// <summary>An <see cref="int"/> field; values outside the int range are treated as missing rather than wrapped.</summary>
    public static int? Int(JsonElement e, string name)
        => Lng(e, name) is { } l && l >= int.MinValue && l <= int.MaxValue ? (int)l : null;

    public static bool Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Arr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object) : Enumerable.Empty<JsonElement>();

    public static DateTimeOffset? Date(JsonElement e, string name)
        => Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>
/// Defensive parsers from Inara <c>eventData</c> to the typed records. A row with none of its
/// recognised key fields is treated as malformed (null) rather than reported as an all-empty record,
/// so a wrong field-name guess fails safe instead of looking like real data.
/// </summary>
internal static class InaraReadParsers
{
    /// <summary>Parses an object profile; throws <see cref="FormatException"/> when no recognised profile field is present.</summary>
    public static InaraCommanderProfile? Profile(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return null;
        var name = InaraJson.Str(d, "commanderName");
        var user = InaraJson.Str(d, "userName");
        var url = InaraJson.Str(d, "inaraURL");
        var ranks = InaraJson.Arr(d, "commanderRanksPilot")
            .Select(r => InaraJson.Str(r, "rankName") is { Length: > 0 } n ? new InaraRank(n, InaraJson.Int(r, "rankValue"), InaraJson.Dbl(r, "rankProgress")) : null)
            .OfType<InaraRank>().ToList();
        var reps = InaraJson.Arr(d, "commanderReputationMajorFaction")
            .Select(r => InaraJson.Str(r, "majorfactionName") is { Length: > 0 } n ? new InaraReputation(n, InaraJson.Dbl(r, "majorfactionReputation")) : null)
            .OfType<InaraReputation>().ToList();
        if (name is null && user is null && url is null && ranks.Count == 0 && reps.Count == 0)
            throw new FormatException("profile reply has no recognised fields");

        var squad = d.TryGetProperty("commanderSquadron", out var s) ? s : default;
        return new(name, user, url,
            InaraJson.Str(squad, "squadronName"), InaraJson.Str(squad, "squadronMemberRank"),
            InaraJson.Str(d, "preferredAllegianceName"), InaraJson.Str(d, "preferredPowerName"),
            ranks, reps);
    }

    public static InaraNearbyService? Service(JsonElement d)
    {
        var svc = new InaraNearbyService(
            InaraJson.First(d, "stationName", "engineerName", "name"), InaraJson.Str(d, "starsystemName"),
            InaraJson.Dbl(d, "distance"), InaraJson.Dbl(d, "distanceToArrival"), InaraJson.Str(d, "stationType"), InaraJson.Lng(d, "marketID"));
        return svc.Name is null && svc.StarSystem is null && svc.MarketId is null ? null : svc;
    }

    public static InaraEngineer? Engineer(JsonElement d)
    {
        if (InaraJson.First(d, "engineerName", "name") is not { } name) return null;
        var blueprints = InaraJson.Arr(d, "blueprints")
            .Select(b => InaraJson.First(b, "blueprintName_Localised", "blueprintName") is { } bn
                ? new InaraBlueprint(bn, InaraJson.Int(b, "blueprintGrade"),
                    InaraJson.Arr(b, "materials")
                        .Select(m => InaraJson.First(m, "materialName_Localised", "materialName") is { } mn ? new InaraMaterialCost(mn, InaraJson.Int(m, "materialCount")) : null)
                        .OfType<InaraMaterialCost>().ToList())
                : null)
            .OfType<InaraBlueprint>().ToList();
        return new(name, InaraJson.Str(d, "starsystemName"), InaraJson.Str(d, "stationName"),
            InaraJson.First(d, "unlockRequirements", "requirements"), blueprints);
    }

    public static InaraCommunityGoal? Goal(JsonElement d)
    {
        var id = InaraJson.Lng(d, "communitygoalGameID") ?? InaraJson.Lng(d, "communitygoalID");
        var title = InaraJson.Str(d, "communitygoalName");
        if (id is null && title is null) return null;
        return new(id, title, InaraJson.Str(d, "starsystemName"), InaraJson.Str(d, "stationName"), InaraJson.Date(d, "goalExpiry"),
            InaraJson.Bool(d, "isCompleted"), InaraJson.Lng(d, "contributorsNum"), InaraJson.First(d, "goalObjectiveText", "goalObjective"),
            InaraJson.Arr(d, "tiers").Select(t => new InaraGoalTier(InaraJson.Str(t, "tierName"), InaraJson.Lng(t, "tierTarget"), InaraJson.Str(t, "tierReward"))).ToList());
    }
}
