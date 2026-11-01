using EDNexus.Core.Surface;
using Xunit;

namespace EDNexus.Tests.Surface;

public class GreatCircleTests
{
    private const double EarthKm = 6371.0;

    [Fact]
    public void Distance_london_to_paris_is_known_value()
    {
        // Published haversine result for these coordinates on R=6371 km is ~343.5 km.
        var d = GreatCircle.Distance(51.5074, -0.1278, 48.8566, 2.3522, EarthKm);
        Assert.InRange(d, 343.0, 344.5);
    }

    [Fact]
    public void Distance_quarter_circle_along_equator_and_to_pole()
    {
        Assert.Equal(Math.PI / 2 * 1000, GreatCircle.Distance(0, 0, 0, 90, 1000), 6);
        Assert.Equal(Math.PI / 2 * 1000, GreatCircle.Distance(0, 33, 90, 0, 1000), 6);
    }

    [Fact]
    public void Distance_antipodes_is_half_circumference()
    {
        // Near-antipodal haversine loses a few parts in 1e9; far below anything a surface tool can see.
        Assert.Equal(Math.PI * 1000, GreatCircle.Distance(10, 20, -10, -160, 1000), 3);
        Assert.Equal(Math.PI * 1000, GreatCircle.Distance(90, 0, -90, 0, 1000), 3);
    }

    [Fact]
    public void Distance_poles_ignore_longitude()
    {
        Assert.Equal(0, GreatCircle.Distance(90, -170, 90, 40, 1000), 6);
        Assert.Equal(GreatCircle.Distance(80, 0, 90, 0, 1000), GreatCircle.Distance(80, 123, 90, -77, 1000), 6);
    }

    [Fact]
    public void Distance_across_antimeridian_takes_the_short_way()
    {
        // 179 E to 179 W is 2 degrees of longitude, not 358.
        var d = GreatCircle.Distance(0, 179, 0, -179, 1000);
        Assert.Equal(2 * Math.PI / 180 * 1000, d, 6);
        Assert.Equal(d, GreatCircle.Distance(0, -179, 0, 179, 1000), 9);
    }

    [Fact]
    public void Distance_tiny_separation_is_accurate_and_nonzero()
    {
        // One metre of arc on a 3,200 km world.
        const double r = 3_200_000;
        var dLat = 1.0 / r * 180 / Math.PI;
        var d = GreatCircle.Distance(12.3, 45.6, 12.3 + dLat, 45.6, r);
        Assert.Equal(1.0, d, 4);
        Assert.Equal(0, GreatCircle.Distance(12.3, 45.6, 12.3, 45.6, r));
    }

    [Fact]
    public void Distance_scales_with_radius_and_zero_radius_is_zero()
    {
        Assert.Equal(2 * GreatCircle.Distance(1, 2, 3, 4, 100), GreatCircle.Distance(1, 2, 3, 4, 200), 9);
        Assert.Equal(0, GreatCircle.Distance(1, 2, 3, 4, 0));
    }

    [Theory]
    [InlineData(0, 0, 10, 0, 0)]       // due north
    [InlineData(0, 0, -10, 0, 180)]    // due south
    [InlineData(0, 0, 0, 10, 90)]      // due east along the equator
    [InlineData(0, 0, 0, -10, 270)]    // due west
    [InlineData(0, 179, 0, -179, 90)]  // east across the antimeridian
    [InlineData(0, -179, 0, 179, 270)] // west across the antimeridian
    public void InitialBearing_cardinal_directions(double la1, double lo1, double la2, double lo2, double expected)
        => Assert.Equal(expected, GreatCircle.InitialBearing(la1, lo1, la2, lo2), 6);

    [Fact]
    public void InitialBearing_known_value_new_york_to_london()
    {
        // JFK-ish to London: initial bearing about 51 degrees.
        var b = GreatCircle.InitialBearing(40.7128, -74.0060, 51.5074, -0.1278);
        Assert.InRange(b, 50.0, 52.5);
    }

    [Fact]
    public void InitialBearing_from_and_to_poles()
    {
        Assert.Equal(180, GreatCircle.InitialBearing(90, 0, 10, 77), 6);   // away from the north pole is south
        Assert.Equal(0, GreatCircle.InitialBearing(-90, 0, -10, 77), 6);   // away from the south pole is north
        Assert.Equal(0, GreatCircle.InitialBearing(10, 77, 90, 0), 6);     // toward the north pole
        Assert.Equal(180, GreatCircle.InitialBearing(10, 77, -90, 0), 6);  // toward the south pole
    }

    [Fact]
    public void InitialBearing_coincident_points_is_zero_not_NaN()
    {
        Assert.Equal(0, GreatCircle.InitialBearing(5, 5, 5, 5));
        Assert.Equal(0, GreatCircle.InitialBearing(90, 0, 90, 90));
    }

    [Fact]
    public void InitialBearing_always_in_range()
    {
        var rng = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var b = GreatCircle.InitialBearing(rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180,
                                               rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180);
            Assert.InRange(b, 0, 360);
            Assert.NotEqual(360, b);
        }
    }

    [Theory]
    [InlineData(90, 90, 0)]
    [InlineData(100, 90, 10)]
    [InlineData(80, 90, -10)]
    [InlineData(0, 180, 180)]       // directly behind reports +180, not -180
    [InlineData(350, 10, -20)]      // wraps through north
    [InlineData(10, 350, 20)]
    [InlineData(270, 90, 180)]
    [InlineData(0, 359, 1)]
    [InlineData(725, 5, 0)]         // out-of-range input still normalised
    public void RelativeBearing_wraps_into_minus180_to_180(double bearing, double heading, double expected)
        => Assert.Equal(expected, GreatCircle.RelativeBearing(bearing, heading), 9);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(720.5, 0.5)]
    public void NormalizeDegrees(double input, double expected)
        => Assert.Equal(expected, GreatCircle.NormalizeDegrees(input), 9);

    [Theory]
    [InlineData(180, -180)]
    [InlineData(-180, -180)]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(45, 45)]
    public void NormalizeLongitude(double input, double expected)
        => Assert.Equal(expected, GreatCircle.NormalizeLongitude(input), 9);

    [Fact]
    public void Destination_round_trips_with_distance_and_bearing()
    {
        const double r = 6_000_000;
        var (la, lo) = GreatCircle.Destination(10, 170, 75, 2_500_000, r);   // runs across the antimeridian
        Assert.Equal(2_500_000, GreatCircle.Distance(10, 170, la, lo, r), 3);
        Assert.Equal(75, GreatCircle.InitialBearing(10, 170, la, lo), 6);
        Assert.InRange(lo, -180, 180);
    }

    [Fact]
    public void Destination_over_the_pole_stays_valid()
    {
        var (la, lo) = GreatCircle.Destination(89, 0, 0, 200_000, 1_000_000);   // north past the pole
        Assert.InRange(la, -90, 90);
        Assert.InRange(lo, -180, 180);
        Assert.True(double.IsFinite(la) && double.IsFinite(lo));
    }
}
