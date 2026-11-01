using System.Globalization;
using System.Text.Json;

namespace EliteDangerous.Inara;

/// <summary>Outcome category of an <see cref="InaraReadClient"/> call.</summary>
public enum InaraReadStatus
{
    /// <summary>Data was returned (fresh, cached, or stale-on-error).</summary>
    Ok,
    /// <summary>Inara answered but has no matching data.</summary>
    NotFound,
    /// <summary>The consent gate or a missing API key blocked the call; no request was made.</summary>
    Disabled,
    /// <summary>Rate limited (by Inara or by our own local limiter) and nothing cached to serve.</summary>
    RateLimited,
    /// <summary>Bad API key or another non-retryable refusal.</summary>
    Rejected,
    /// <summary>Network, transport or parse failure and nothing cached to serve.</summary>
    Error,
}

/// <summary>Result of a read call. The client never throws; inspect <see cref="Status"/>.</summary>
public sealed record InaraReadResult<T>(InaraReadStatus Status, T? Value, bool FromCache = false, bool IsStale = false, string? Message = null, TimeSpan? RetryAfter = null)
{
    /// <summary>True when <see cref="Value"/> is usable (fresh, cached, or stale-on-error).</summary>
    public bool HasValue => Status == InaraReadStatus.Ok && Value is not null;
}

/// <summary>A commander's public Inara profile.</summary>
public sealed record InaraCommanderProfile(
    string? CommanderName, string? InaraName, string? InaraUrl,
    string? SquadronName, string? SquadronRank, string? Allegiance, string? Power,
    IReadOnlyList<InaraRank> Ranks, IReadOnlyList<InaraReputation> Reputations);

/// <summary>A pilot rank entry. <see cref="Progress"/> is 0..1 when supplied.</summary>
public sealed record InaraRank(string Name, int Value, double? Progress);

/// <summary>A major-faction reputation (-100..100).</summary>
public sealed record InaraReputation(string Faction, double Reputation);

/// <summary>Kinds of service for <see cref="InaraReadClient.GetNearestServicesAsync"/>.</summary>
public enum InaraServiceKind { Engineer, MaterialTrader, TechnologyBroker, InterstellarFactors, Shipyard, Outfitting }

/// <summary>A station or engineer base near a reference system.</summary>
public sealed record InaraNearbyService(string? Name, string? StarSystem, double? DistanceLy, double? DistanceLs, string? StationType, long? MarketId);

/// <summary>An engineer with unlock requirements and the blueprints they offer.</summary>
public sealed record InaraEngineer(string Name, string? StarSystem, string? Station, string? UnlockRequirements, IReadOnlyList<InaraBlueprint> Blueprints);

/// <summary>An engineering blueprint with its material list.</summary>
public sealed record InaraBlueprint(string Name, int? Grade, IReadOnlyList<InaraMaterialCost> Materials);

/// <summary>A material requirement.</summary>
public sealed record InaraMaterialCost(string Name, int Count);

/// <summary>An active community goal.</summary>
public sealed record InaraCommunityGoal(
    long? Id, string Title, string? StarSystem, string? Station, DateTimeOffset? Expires,
    bool IsCompleted, long? ContributorsNum, string? Objective, IReadOnlyList<InaraGoalTier> Tiers);

/// <summary>One reward tier of a community goal.</summary>
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

    public static bool Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Arr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object) : Enumerable.Empty<JsonElement>();

    public static DateTimeOffset? Date(JsonElement e, string name)
        => Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>Defensive parsers from Inara <c>eventData</c> to the typed records. Missing fields become null/empty.</summary>
internal static class InaraReadParsers
{
    public static InaraCommanderProfile Profile(JsonElement d)
    {
        var squad = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("commanderSquadron", out var s) ? s : default;
        return new(
            InaraJson.Str(d, "commanderName"), InaraJson.Str(d, "userName"), InaraJson.Str(d, "inaraURL"),
            InaraJson.Str(squad, "squadronName"), InaraJson.Str(squad, "squadronMemberRank"),
            InaraJson.Str(d, "preferredAllegianceName"), InaraJson.Str(d, "preferredPowerName"),
            InaraJson.Arr(d, "commanderRanksPilot").Select(r => new InaraRank(InaraJson.Str(r, "rankName") ?? "?", (int)(InaraJson.Lng(r, "rankValue") ?? 0), InaraJson.Dbl(r, "rankProgress"))).ToList(),
            InaraJson.Arr(d, "commanderReputationMajorFaction").Select(r => new InaraReputation(InaraJson.Str(r, "majorfactionName") ?? "?", InaraJson.Dbl(r, "majorfactionReputation") ?? 0)).ToList());
    }

    public static InaraNearbyService Service(JsonElement d) => new(
        InaraJson.First(d, "stationName", "engineerName", "name"), InaraJson.Str(d, "starsystemName"),
        InaraJson.Dbl(d, "distance"), InaraJson.Dbl(d, "distanceToArrival"), InaraJson.Str(d, "stationType"), InaraJson.Lng(d, "marketID"));

    public static InaraEngineer Engineer(JsonElement d) => new(
        InaraJson.First(d, "engineerName", "name") ?? "?", InaraJson.Str(d, "starsystemName"), InaraJson.Str(d, "stationName"),
        InaraJson.First(d, "unlockRequirements", "requirements"),
        InaraJson.Arr(d, "blueprints").Select(b => new InaraBlueprint(
            InaraJson.First(b, "blueprintName_Localised", "blueprintName") ?? "?", (int?)InaraJson.Lng(b, "blueprintGrade"),
            InaraJson.Arr(b, "materials").Select(m => new InaraMaterialCost(InaraJson.First(m, "materialName_Localised", "materialName") ?? "?", (int)(InaraJson.Lng(m, "materialCount") ?? 0))).ToList())).ToList());

    public static InaraCommunityGoal Goal(JsonElement d) => new(
        InaraJson.Lng(d, "communitygoalGameID") ?? InaraJson.Lng(d, "communitygoalID"), InaraJson.Str(d, "communitygoalName") ?? "?",
        InaraJson.Str(d, "starsystemName"), InaraJson.Str(d, "stationName"), InaraJson.Date(d, "goalExpiry"),
        InaraJson.Bool(d, "isCompleted"), InaraJson.Lng(d, "contributorsNum"), InaraJson.First(d, "goalObjectiveText", "goalObjective"),
        InaraJson.Arr(d, "tiers").Select(t => new InaraGoalTier(InaraJson.Str(t, "tierName"), InaraJson.Lng(t, "tierTarget"), InaraJson.Str(t, "tierReward"))).ToList());
}
