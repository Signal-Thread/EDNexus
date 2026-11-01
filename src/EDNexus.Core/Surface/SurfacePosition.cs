using EDNexus.Core.Journal;

namespace EDNexus.Core.Surface;

/// <summary>
/// Where the commander is on (or close to) a planet, from the game's <c>Status.json</c>. Only exists
/// while the game reports a latitude and longitude (the <c>HasLatLong</c> flag); the state tracker
/// clears it on takeoff, launch and supercruise.
/// </summary>
/// <param name="Latitude">Degrees, -90 (south) to +90 (north).</param>
/// <param name="Longitude">Degrees, -180 (west) to +180 (east).</param>
/// <param name="Altitude">Metres above the surface, or null when the game did not say.</param>
/// <param name="Heading">Compass heading in degrees [0, 360), or null when the game did not say.</param>
/// <param name="PlanetRadius">Radius of the body in metres, or null when neither Status.json nor a Scan gave it.</param>
/// <param name="BodyName">The body the position is on, or null when unknown.</param>
public sealed record SurfacePosition(
    double Latitude,
    double Longitude,
    double? Altitude,
    double? Heading,
    double? PlanetRadius,
    string? BodyName)
{
    /// <summary>
    /// Great-circle distance in metres from this position to another point on the same body, or null
    /// when the planet radius is unknown.
    /// </summary>
    public double? DistanceTo(double latitude, double longitude)
        => PlanetRadius is double r && r > 0 ? GreatCircle.Distance(Latitude, Longitude, latitude, longitude, r) : null;

    /// <summary>Initial bearing in degrees [0, 360) from here to another point.</summary>
    public double BearingTo(double latitude, double longitude)
        => GreatCircle.InitialBearing(Latitude, Longitude, latitude, longitude);

    /// <summary>
    /// Bearing to another point relative to the current heading, in (-180, 180]: positive means the
    /// target is to the right. Null when the heading is unknown.
    /// </summary>
    public double? RelativeBearingTo(double latitude, double longitude)
        => Heading is double h ? GreatCircle.RelativeBearing(BearingTo(latitude, longitude), h) : null;

    /// <summary>
    /// Read a position out of a <c>Status.json</c> payload. Returns null unless the coordinates are
    /// present, finite and in range. Callers must check the <c>HasLatLong</c> flag themselves.
    /// </summary>
    public static SurfacePosition? FromStatus(JournalEntry e, double? fallbackRadius)
    {
        if (e.GetDouble("Latitude") is not double lat || e.GetDouble("Longitude") is not double lon) return null;
        if (!double.IsFinite(lat) || !double.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180) return null;

        var radius = e.GetDouble("PlanetRadius");
        if (radius is not double pr || !(pr > 0) || !double.IsFinite(pr))
            radius = fallbackRadius is double fb && fb > 0 ? fb : null;

        var altitude = e.GetDouble("Altitude");
        if (altitude is double a && !double.IsFinite(a)) altitude = null;

        var heading = e.GetDouble("Heading");
        heading = heading is double h && double.IsFinite(h) ? GreatCircle.NormalizeDegrees(h) : null;

        return new SurfacePosition(lat, lon, altitude, heading, radius, e.GetString("BodyName"));
    }
}
