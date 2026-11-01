namespace EDNexus.Core.Mining;

/// <summary>
/// One material a prospector limpet found in an asteroid, and how much of the rock it makes up.
/// </summary>
/// <param name="Name">Display name (localised where the journal gives one).</param>
/// <param name="Symbol">Canonical key (see <see cref="Colonisation.CommodityName"/>) for pricing lookups.</param>
/// <param name="Proportion">Percentage (0–100) of the asteroid's mass this material accounts for.</param>
public sealed record ProspectedMaterial(string Name, string Symbol, double Proportion);

/// <summary>
/// One <c>ProspectedAsteroid</c> result: everything the limpet found in that rock. The journal draws
/// no distinction between a laser-minable surface deposit and one exposed by a sub-surface displacement
/// missile — both simply appear in <see cref="Materials"/> — so the one distinction EDNexus can make
/// from the data is core mining (<see cref="HasMotherlode"/>) versus everything else.
/// </summary>
/// <param name="Content">Overall material-content quality: "Low", "Medium", "High", or "" if unknown.</param>
/// <param name="Remaining">Percentage (0–100) of the asteroid's mass left to mine.</param>
public sealed record ProspectResult(
    DateTimeOffset Timestamp,
    IReadOnlyList<ProspectedMaterial> Materials,
    string? MotherlodeName,
    string? MotherlodeSymbol,
    string Content,
    double Remaining)
{
    /// <summary>True when this rock has a deep-core seam requiring seismic charges to crack open.</summary>
    public bool HasMotherlode => !string.IsNullOrEmpty(MotherlodeSymbol);
}

/// <summary>
/// One <c>MiningRefined</c> event: the refinery finished converting fragments into a single unit
/// (one tonne) of cargo. The journal fires one of these per unit, with no quantity field.
/// </summary>
/// <param name="Position">
/// Where the commander's SRV was on the planet surface when the unit came in, or null for anything
/// else (ship-based asteroid mining, or no live position fix). The journal event itself carries no
/// position; this comes from the latest <c>Status.json</c> update.
/// </param>
public sealed record RefinedUnit(DateTimeOffset Timestamp, string Symbol, string Name, MiningFix? Position = null);

/// <summary>A point on a planet's surface, as reported by <c>Status.json</c> while driving an SRV.</summary>
/// <param name="SystemAddress">The system's address, from the last arrival event; null if not yet seen.</param>
/// <param name="StarSystem">The system's name, from the last arrival event; null if not yet seen.</param>
/// <param name="Body">The body's full name (e.g. "Swoiphs AW-C d109 5 a").</param>
/// <param name="PlanetRadius">Body radius in metres, when Status.json reports it.</param>
public sealed record MiningFix(
    long? SystemAddress, string? StarSystem, string Body, double Latitude, double Longitude, double? PlanetRadius);
