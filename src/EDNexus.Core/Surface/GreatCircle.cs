namespace EDNexus.Core.Surface;

/// <summary>
/// Pure great-circle maths on a sphere. Angles are degrees, distances are in the unit of the radius
/// supplied (the game reports planet radii in metres).
/// </summary>
public static class GreatCircle
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Within this many degrees of +/-90 a point counts as being at a pole.</summary>
    private const double PoleToleranceDegrees = 1e-12;

    /// <summary>Bearing components smaller than this mean the two points coincide (or are antipodal).</summary>
    private const double CoincidentEpsilon = 1e-15;

    /// <summary>Wrap an angle into [0, 360).</summary>
    public static double NormalizeDegrees(double degrees)
    {
        var d = degrees % 360.0;
        if (d < 0) d += 360.0;
        return d >= 360.0 ? 0 : d;
    }

    /// <summary>Wrap a longitude into [-180, 180).</summary>
    public static double NormalizeLongitude(double degrees) => NormalizeDegrees(degrees + 180.0) - 180.0;

    /// <summary>Haversine distance between two points on a sphere of the given radius.</summary>
    public static double Distance(double lat1, double lon1, double lat2, double lon2, double radius)
        => radius * CentralAngle(lat1, lon1, lat2, lon2);

    /// <summary>Central angle in radians between two points (numerically stable for tiny and antipodal separations).</summary>
    public static double CentralAngle(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * DegToRad;
        var p2 = lat2 * DegToRad;
        var dp = p2 - p1;
        var dl = (lon2 - lon1) * DegToRad;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        a = Math.Clamp(a, 0, 1);
        return 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>
    /// Initial bearing (forward azimuth) in degrees [0, 360), clockwise from north, of the great
    /// circle from point 1 to point 2. Coincident points return 0.
    /// </summary>
    public static double InitialBearing(double lat1, double lon1, double lat2, double lon2)
    {
        // At a pole "north" is undefined and the longitude is arbitrary, so the formula would return a
        // direction that depends on the meaningless longitude. Every way out of a pole is south
        // (north pole) or north (south pole).
        if (Math.Abs(lat1) >= 90 - PoleToleranceDegrees)
            return Math.Abs(lat1 - lat2) < PoleToleranceDegrees ? 0 : (lat1 > 0 ? 180 : 0);

        var p1 = lat1 * DegToRad;
        var p2 = lat2 * DegToRad;
        var dl = (lon2 - lon1) * DegToRad;
        var y = Math.Sin(dl) * Math.Cos(p2);
        var x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        if (Math.Abs(x) < CoincidentEpsilon && Math.Abs(y) < CoincidentEpsilon) return 0;   // coincident points
        return NormalizeDegrees(Math.Atan2(y, x) * RadToDeg);
    }

    /// <summary>
    /// A bearing relative to a heading, in (-180, 180]: 0 is dead ahead, positive is to the right,
    /// negative to the left, 180 is directly behind.
    /// </summary>
    public static double RelativeBearing(double bearing, double heading)
    {
        var d = NormalizeDegrees(bearing - heading);
        return d > 180.0 ? d - 360.0 : d;
    }

    /// <summary>
    /// The point reached by travelling <paramref name="distance"/> from a start along a bearing.
    /// Returns (NaN, NaN) when the radius is not a positive finite number or any input is non-finite.
    /// </summary>
    public static (double Latitude, double Longitude) Destination(double lat, double lon, double bearing, double distance, double radius)
    {
        if (!(radius > 0) || !double.IsFinite(radius)
            || !double.IsFinite(lat) || !double.IsFinite(lon) || !double.IsFinite(bearing) || !double.IsFinite(distance))
            return (double.NaN, double.NaN);
        var d = distance / radius;
        var p1 = lat * DegToRad;
        var b = bearing * DegToRad;
        var sinP2 = Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(b);
        var p2 = Math.Asin(Math.Clamp(sinP2, -1, 1));
        var l2 = lon * DegToRad + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * sinP2);
        return (p2 * RadToDeg, NormalizeLongitude(l2 * RadToDeg));
    }
}
