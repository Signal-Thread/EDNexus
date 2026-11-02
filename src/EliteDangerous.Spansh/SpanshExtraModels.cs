namespace EliteDangerous.Spansh;

/// <summary>
/// A survey-route request shared by Spansh's Road to Riches and exobiology routers: starting at
/// <see cref="From"/>, hop between systems within <see cref="RadiusLy"/> of the route that have bodies
/// worth at least <see cref="MinValue"/> credits, ending at <see cref="To"/> (or back at the start when
/// <see cref="Loop"/> is set).
/// </summary>
public sealed class SpanshSurveyRouteQuery
{
    public required string From { get; init; }

    /// <summary>Destination; when null or blank the router plots from <see cref="From"/> only (or a loop).</summary>
    public string? To { get; init; }

    /// <summary>The ship's laden jump range, in light years.</summary>
    public required double RangeLy { get; init; }

    /// <summary>How far (ly) the router may stray from the straight path to reach a valuable system.</summary>
    public int RadiusLy { get; init; } = 25;

    /// <summary>Maximum number of systems in the result.</summary>
    public int MaxSystems { get; init; } = 25;

    /// <summary>Ignore bodies further than this from the arrival star (ls).</summary>
    public int MaxDistanceLs { get; init; } = 1_000_000;

    /// <summary>
    /// Ignore bodies worth less than this many credits. Sent as <c>min_value</c> to both routers; whether
    /// the exobiology router honours it is unverified.
    /// </summary>
    public int MinValue { get; init; } = 100_000;

    /// <summary>Return to the start system at the end of the route.</summary>
    public bool Loop { get; init; }

    /// <summary>Skip Thargoid-ravaged systems.</summary>
    public bool AvoidThargoids { get; init; } = true;

    /// <summary>Road to Riches only: value bodies by their mapped (not just scanned) payout. Ignored by the exobiology router.</summary>
    public bool UseMappingValue { get; init; } = true;
}

/// <summary>A biological landmark Spansh predicts on a body, e.g. a genus with its count and estimated payout.</summary>
public sealed record SpanshLandmark(string Type, string Subtype, int Count, long Value);

/// <summary>
/// A body flagged by a survey router: where it is, what it is worth, and (for the exobiology router)
/// the predicted biological landmarks.
/// </summary>
public sealed record SpanshSurveyBody(
    string Name,
    string SubType,
    double DistanceToArrivalLs,
    long ScanValue,
    long MappingValue,
    bool IsTerraformable,
    IReadOnlyList<SpanshLandmark> Landmarks);

/// <summary>One system on a survey route with the bodies worth visiting there.</summary>
public sealed record SpanshSurveySystem(
    string Name,
    int Jumps,
    double DistanceJumpedLy,
    IReadOnlyList<SpanshSurveyBody> Bodies);

/// <summary>
/// The parsed result of a Road to Riches / exobiology plot. Never throws: failures surface as
/// <see cref="IsOk"/> false with an <see cref="Error"/>.
/// </summary>
public sealed class SpanshSurveyRouteResult
{
    public bool IsOk { get; init; }
    public string? Error { get; init; }

    /// <summary>The ordered systems, first stop first. Empty on failure.</summary>
    public IReadOnlyList<SpanshSurveySystem> Systems { get; init; } = Array.Empty<SpanshSurveySystem>();

    public static SpanshSurveyRouteResult Ok(IReadOnlyList<SpanshSurveySystem> systems)
        => new() { IsOk = true, Systems = systems };

    public static SpanshSurveyRouteResult Failure(string message)
        => new() { IsOk = false, Error = message };
}

/// <summary>
/// A tourist-router request: visit every system in <see cref="Systems"/>, starting from
/// <see cref="From"/>, in a sensible order.
/// </summary>
public sealed class SpanshTouristRouteQuery
{
    public required string From { get; init; }
    /// <summary>Target systems; sent comma-joined, so names containing a comma are not representable. Must be non-empty.</summary>
    public required IReadOnlyList<string> Systems { get; init; }
    public required double RangeLy { get; init; }
}

/// <summary>Well-known Spansh body sub-type names for <see cref="SpanshBodyQuery.SubTypes"/>.</summary>
public static class SpanshBodyTypes
{
    public const string EarthLike = "Earth-like world";
    public const string Ammonia = "Ammonia world";
    public const string WaterWorld = "Water world";
    public const string HighMetalContent = "High metal content world";
}

/// <summary>
/// A body search: find bodies near <see cref="ReferenceSystem"/> of the given sub-types, optionally
/// restricted to landable ones.
/// </summary>
public sealed class SpanshBodyQuery
{
    public required string ReferenceSystem { get; init; }
    public required IReadOnlyList<string> SubTypes { get; init; }
    public bool? Landable { get; init; }

    /// <summary>Number of rows requested; clamped to 1..100 before sending.</summary>
    public int MaxResults { get; init; } = 10;
}

/// <summary>A body returned by a body search.</summary>
public sealed record SpanshBody(
    string Name,
    string SystemName,
    string SubType,
    double DistanceLy,
    double DistanceToArrivalLs,
    bool IsLandable);

/// <summary>The parsed result of a body search; failures never throw.</summary>
public sealed class SpanshBodiesResult
{
    public bool IsOk { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<SpanshBody> Bodies { get; init; } = Array.Empty<SpanshBody>();

    public static SpanshBodiesResult Ok(IReadOnlyList<SpanshBody> bodies) => new() { IsOk = true, Bodies = bodies };
    public static SpanshBodiesResult Failure(string message) => new() { IsOk = false, Error = message };
}

/// <summary>Well-known Spansh station service names for <see cref="SpanshServiceQuery.Services"/>.</summary>
public static class SpanshServices
{
    public const string Shipyard = "Shipyard";
    public const string Outfitting = "Outfitting";
    public const string MaterialTrader = "Material Trader";
    public const string InterstellarFactors = "Interstellar Factors Contact";
    public const string TechnologyBroker = "Technology Broker";
    public const string Refuel = "Refuel";
    public const string Repair = "Repair";
}

/// <summary>
/// A "nearest service" search: find stations near <see cref="ReferenceSystem"/> offering every service
/// in <see cref="Services"/>, optionally requiring a large landing pad.
/// </summary>
public sealed class SpanshServiceQuery
{
    public required string ReferenceSystem { get; init; }
    public required IReadOnlyList<string> Services { get; init; }
    public bool RequireLargePad { get; init; }

    /// <summary>Number of rows requested; clamped to 1..100 before sending.</summary>
    public int MaxResults { get; init; } = 10;
}

/// <summary>A station returned by a service search.</summary>
public sealed record SpanshServiceStation(
    string SystemName,
    string StationName,
    string StationType,
    double DistanceLy,
    double DistanceToArrivalLs,
    bool HasLargePad,
    IReadOnlyList<string> Services);

/// <summary>The parsed result of a service search; failures never throw.</summary>
public sealed class SpanshServiceStationsResult
{
    public bool IsOk { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<SpanshServiceStation> Stations { get; init; } = Array.Empty<SpanshServiceStation>();

    public static SpanshServiceStationsResult Ok(IReadOnlyList<SpanshServiceStation> stations) => new() { IsOk = true, Stations = stations };
    public static SpanshServiceStationsResult Failure(string message) => new() { IsOk = false, Error = message };
}
