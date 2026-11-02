using EDNexus.Core.Settings;

namespace EDNexus.Core.Mining;

/// <summary>
/// Pure helpers over the persisted list of <see cref="KnownMiningSpot"/>s: folding a newly refined
/// unit into it, finding the spots worth mining in a system, and phrasing them for a voice callout.
/// Every method returns new data and never mutates its input, so the list can be swapped atomically.
/// </summary>
public static class MiningSpotBook
{
    /// <summary>Units refined within this many metres of a spot (same body and commodity) join it.</summary>
    public const double MergeRadiusMetres = 1000;

    /// <summary>Fallback when Status.json gave no planet radius: roughly a kilometre on a small moon.</summary>
    private const double MergeRadiusDegrees = 0.05;

    /// <summary>
    /// Return a copy of <paramref name="spots"/> with <paramref name="unit"/> recorded: folded into the
    /// first matching spot within <see cref="MergeRadiusMetres"/>, or added as a new one. A unit without
    /// a surface position is not a planetary spot and leaves the list unchanged.
    /// </summary>
    public static List<KnownMiningSpot> Record(IReadOnlyList<KnownMiningSpot> spots, RefinedUnit unit, int averagePrice)
    {
        var copy = spots.Select(Clone).ToList();
        if (unit.Position is not { } at) return copy;

        var match = copy.FirstOrDefault(s => s.Symbol == unit.Symbol && SameBody(s, at) && IsNear(s, at));
        if (match is null)
        {
            copy.Add(new KnownMiningSpot
            {
                SystemAddress = at.SystemAddress,
                StarSystem = at.StarSystem ?? "",
                Body = at.Body,
                Latitude = at.Latitude,
                Longitude = at.Longitude,
                Symbol = unit.Symbol,
                Name = unit.Name,
                AveragePrice = averagePrice,
                Tonnes = 1,
                FirstMined = unit.Timestamp,
                LastMined = unit.Timestamp,
            });
        }
        else
        {
            match.Tonnes += 1;
            match.AveragePrice = averagePrice;
            match.LastMined = unit.Timestamp;
        }
        return copy;
    }

    /// <summary>Spots in the given system whose commodity is worth at least <paramref name="minValue"/>, most valuable first.</summary>
    public static IReadOnlyList<KnownMiningSpot> WorthMiningIn(
        IReadOnlyList<KnownMiningSpot> spots, long? systemAddress, string? starSystem, int minValue)
    {
        return spots
            .Where(s => InSystem(s, systemAddress, starSystem))
            .Where(s => minValue <= 0 || s.AveragePrice >= minValue)
            .OrderByDescending(s => s.AveragePrice)
            .ThenBy(s => s.Body, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Phrase the spots for speech, one clause per commodity-and-body, most valuable first, e.g.
    /// "Known mining spots in Swoiphs AW-C d109: Monazite on 5 a, 4 spots; Thortveitite on 5 c."
    /// </summary>
    public static string DescribeForCallout(string systemName, IReadOnlyList<KnownMiningSpot> spots)
    {
        var clauses = spots
            .GroupBy(s => (s.Name, s.Body))
            .Select(g => (g.Key.Name, Body: ShortBodyName(systemName, g.Key.Body), Count: g.Count(), Price: g.Max(s => s.AveragePrice)))
            .OrderByDescending(g => g.Price)
            .Select(g => g.Count > 1 ? $"{g.Name} on {g.Body}, {g.Count} spots" : $"{g.Name} on {g.Body}");
        return $"Known mining spots in {systemName}: {string.Join("; ", clauses)}.";
    }

    /// <summary>
    /// Great-circle (haversine) distance in metres between two surface points on a body of the given
    /// radius.
    /// </summary>
    public static double SurfaceDistanceMetres(double lat1, double lon1, double lat2, double lon2, double radius)
        => Surface.GreatCircle.Distance(lat1, lon1, lat2, lon2, radius);

    /// <summary>"Swoiphs AW-C d109 5 a" → "5 a" inside Swoiphs AW-C d109; unchanged otherwise.</summary>
    private static string ShortBodyName(string systemName, string body)
        => systemName.Length > 0 && body.Length > systemName.Length
           && body.StartsWith(systemName + " ", StringComparison.OrdinalIgnoreCase)
            ? body[(systemName.Length + 1)..]
            : body;

    private static bool InSystem(KnownMiningSpot s, long? systemAddress, string? starSystem)
    {
        if (systemAddress is long address && s.SystemAddress is long spotAddress) return address == spotAddress;
        return starSystem is { Length: > 0 } && string.Equals(s.StarSystem, starSystem, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameBody(KnownMiningSpot s, MiningFix at)
        => string.Equals(s.Body, at.Body, StringComparison.OrdinalIgnoreCase);

    private static bool IsNear(KnownMiningSpot s, MiningFix at)
    {
        if (at.PlanetRadius is double radius && radius > 0)
            return SurfaceDistanceMetres(s.Latitude, s.Longitude, at.Latitude, at.Longitude, radius) <= MergeRadiusMetres;
        return Math.Abs(s.Latitude - at.Latitude) <= MergeRadiusDegrees
            && Math.Abs(s.Longitude - at.Longitude) <= MergeRadiusDegrees;
    }

    private static KnownMiningSpot Clone(KnownMiningSpot s) => new()
    {
        SystemAddress = s.SystemAddress,
        StarSystem = s.StarSystem,
        Body = s.Body,
        Latitude = s.Latitude,
        Longitude = s.Longitude,
        Symbol = s.Symbol,
        Name = s.Name,
        AveragePrice = s.AveragePrice,
        Tonnes = s.Tonnes,
        FirstMined = s.FirstMined,
        LastMined = s.LastMined,
    };
}
