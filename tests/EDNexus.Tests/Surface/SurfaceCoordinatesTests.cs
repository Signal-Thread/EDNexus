using EDNexus.Core.Surface;
using Xunit;

namespace EDNexus.Tests.Surface;

public class SurfaceCoordinatesTests
{
    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("-12.5", -12.5)]
    [InlineData("+12.5", 12.5)]
    [InlineData("12.5 N", 12.5)]
    [InlineData("12.5N", 12.5)]
    [InlineData("12.5° N", 12.5)]
    [InlineData("12.5 S", -12.5)]
    [InlineData("12.5°s", -12.5)]
    [InlineData("S 12.5", -12.5)]
    [InlineData("n12.5", 12.5)]
    [InlineData("  90 ", 90)]
    [InlineData("-90", -90)]
    [InlineData("90 S", -90)]
    [InlineData(".5", 0.5)]
    [InlineData("0 S", 0)]
    public void Latitude_accepts_signed_and_hemisphere_forms(string text, double expected)
    {
        Assert.True(SurfaceCoordinates.TryParseLatitude(text, out var v));
        Assert.Equal(expected, v, 9);
    }

    [Theory]
    [InlineData("40 E", 40)]
    [InlineData("40 W", -40)]
    [InlineData("W 40.25", -40.25)]
    [InlineData("-40", -40)]
    [InlineData("180", 180)]
    [InlineData("-180", -180)]
    [InlineData("180°W", -180)]
    public void Longitude_accepts_signed_and_hemisphere_forms(string text, double expected)
    {
        Assert.True(SurfaceCoordinates.TryParseLongitude(text, out var v));
        Assert.Equal(expected, v, 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("91")]
    [InlineData("-90.0001")]
    [InlineData("12 E")]        // wrong hemisphere for a latitude
    [InlineData("-12 S")]       // sign and hemisphere together
    [InlineData("N 12 S")]
    [InlineData("1,5")]
    [InlineData("NaN")]
    [InlineData("1e2")]
    public void Latitude_rejects_bad_input(string? text)
        => Assert.False(SurfaceCoordinates.TryParseLatitude(text, out _));

    [Theory]
    [InlineData("181")]
    [InlineData("-181")]
    [InlineData("40 N")]
    [InlineData("-40 W")]
    public void Longitude_rejects_bad_input(string text)
        => Assert.False(SurfaceCoordinates.TryParseLongitude(text, out _));

    [Theory]
    [InlineData("12.5, -40", 12.5, -40)]
    [InlineData("12.5 S, 40 W", -12.5, -40)]
    [InlineData("12.5 S 40 W", -12.5, -40)]
    [InlineData("-12.5 -40", -12.5, -40)]
    [InlineData("-12.5;40", -12.5, 40)]
    [InlineData("S12.5 W40", -12.5, -40)]
    [InlineData("N 12.5 E 40", 12.5, 40)]
    [InlineData("12.5° N, 40.25° E", 12.5, 40.25)]
    public void Pair_parses(string text, double lat, double lon)
    {
        Assert.True(SurfaceCoordinates.TryParsePair(text, out var la, out var lo));
        Assert.Equal(lat, la, 9);
        Assert.Equal(lon, lo, 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12.5")]
    [InlineData("12.5, 40, 3")]
    [InlineData("100, 40")]
    [InlineData("12, 200")]
    [InlineData("12 E, 40 N")]   // swapped axes
    public void Pair_rejects_bad_input(string? text)
        => Assert.False(SurfaceCoordinates.TryParsePair(text, out _, out _));

    [Fact]
    public void Parsing_is_culture_invariant()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.True(SurfaceCoordinates.TryParseLatitude("12.5", out var v));
            Assert.Equal(12.5, v);
            Assert.Equal("12.5000° N", SurfaceCoordinates.FormatLatitude(12.5));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Theory]
    [InlineData(12.34567, "12.3457° N")]
    [InlineData(-12.34567, "12.3457° S")]
    [InlineData(0, "0.0000° N")]
    [InlineData(-0.00001, "0.0000° N")]   // rounds to zero: no misleading "S"
    [InlineData(-90, "90.0000° S")]
    public void FormatLatitude(double v, string expected) => Assert.Equal(expected, SurfaceCoordinates.FormatLatitude(v));

    [Fact]
    public void FormatLongitude_and_pair_and_decimals()
    {
        Assert.Equal("40.0000° W", SurfaceCoordinates.FormatLongitude(-40));
        Assert.Equal("179.5° E", SurfaceCoordinates.FormatLongitude(179.5, 1));
        Assert.Equal("12.3457° S, 40.0000° W", SurfaceCoordinates.Format(-12.34567, -40));
    }

    [Fact]
    public void Format_then_parse_round_trips()
    {
        foreach (var (la, lo) in new[] { (12.3457, -40.0), (-89.9999, 179.9999), (0.0, 0.0), (-45.5, -180.0) })
        {
            Assert.True(SurfaceCoordinates.TryParsePair(SurfaceCoordinates.Format(la, lo), out var pla, out var plo));
            Assert.Equal(la, pla, 4);
            Assert.Equal(lo, plo, 4);
        }
    }
}
