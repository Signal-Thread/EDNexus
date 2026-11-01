using System.Globalization;
using System.Text.RegularExpressions;

namespace EDNexus.Core.Surface;

/// <summary>
/// Parse and format planetary coordinates. Accepts signed decimals (<c>-12.5</c>) and hemisphere
/// forms (<c>12.5 S</c>, <c>S 12.5</c>, <c>12.5°S</c>), always in invariant culture.
/// </summary>
public static partial class SurfaceCoordinates
{
    // Caller-supplied text: cap its length and bound the match time so a pathological string cannot stall a caller.
    private const int MaxInputLength = 64;
    private const int RegexTimeoutMs = 100;

    [GeneratedRegex(@"^\s*(?<h1>[NSEWnsew])?\s*(?<v>[+-]?(?:\d+(?:\.\d+)?|\.\d+))\s*°?\s*(?<h2>[NSEWnsew])?\s*$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex Single();

    [GeneratedRegex(@"^\d+,\d+$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex BareIntegerCommaPair();

    [GeneratedRegex(@"^(?<a>\S.*?[NSns]|[NSns]\s*[+-]?[\d.]+|[+-]?[\d.]+°?)\s+(?<b>\S.*)$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex Pair();

    /// <summary>Parse a latitude in [-90, 90]; N/S suffixes or prefixes apply a sign.</summary>
    public static bool TryParseLatitude(string? text, out double latitude) => TryParse(text, "NS", 90, out latitude);

    /// <summary>Parse a longitude in [-180, 180]; E/W suffixes or prefixes apply a sign.</summary>
    public static bool TryParseLongitude(string? text, out double longitude) => TryParse(text, "EW", 180, out longitude);

    /// <summary>
    /// Parse a "lat, lon" pair (comma or semicolon separated), or a whitespace-separated one such as
    /// <c>12.5 S 40 W</c>. Each part may use any accepted form. A bare <c>12,5</c> (integers around a comma
    /// with no space) is rejected as ambiguous: it is a decimal comma as often as it is a pair.
    /// </summary>
    public static bool TryParsePair(string? text, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxInputLength) return false;
        if (BareIntegerCommaPair().IsMatch(text.Trim())) return false;

        var parts = text.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            try
            {
                var m = Pair().Match(text.Trim());
                if (!m.Success) return false;
                parts = new[] { m.Groups["a"].Value, m.Groups["b"].Value };
            }
            catch (RegexMatchTimeoutException) { return false; }
        }
        return parts.Length == 2 && TryParseLatitude(parts[0], out latitude) && TryParseLongitude(parts[1], out longitude);
    }

    private static bool TryParse(string? text, string axis, double limit, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxInputLength) return false;
        Match m;
        try { m = Single().Match(text); }
        catch (RegexMatchTimeoutException) { return false; }
        if (!m.Success) return false;
        var raw = m.Groups["v"].Value;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)) return false;

        var h1 = m.Groups["h1"].Success ? m.Groups["h1"].Value : null;
        var h2 = m.Groups["h2"].Success ? m.Groups["h2"].Value : null;
        if (h1 is not null && h2 is not null) return false;                 // "N 12 S"
        var hemi = (h1 ?? h2)?.ToUpperInvariant();
        if (hemi is not null)
        {
            if (!axis.Contains(hemi[0])) return false;                        // "12 E" as a latitude
            if (raw.StartsWith('-') || raw.StartsWith('+')) return false;     // a sign and a hemisphere together
            if (hemi[0] is 'S' or 'W') v = -v;
        }
        if (Math.Abs(v) > limit) return false;
        value = v == 0 ? 0 : v;   // normalise -0
        return true;
    }

    /// <summary>Format a latitude as e.g. <c>12.3457° S</c>.</summary>
    public static string FormatLatitude(double latitude, int decimals = 4) => Format(latitude, decimals, 'N', 'S');

    /// <summary>Format a longitude as e.g. <c>40.0000° W</c>.</summary>
    public static string FormatLongitude(double longitude, int decimals = 4) => Format(longitude, decimals, 'E', 'W');

    /// <summary>Format a position as <c>"12.3457° S, 40.0000° W"</c>.</summary>
    public static string Format(double latitude, double longitude, int decimals = 4)
        => $"{FormatLatitude(latitude, decimals)}, {FormatLongitude(longitude, decimals)}";

    private static string Format(double v, int decimals, char pos, char neg)
    {
        if (!double.IsFinite(v)) return "n/a";
        var s = Math.Abs(v).ToString("F" + Math.Clamp(decimals, 0, 8), CultureInfo.InvariantCulture);
        var isZero = double.Parse(s, CultureInfo.InvariantCulture) == 0;
        return $"{s}° {(v < 0 && !isZero ? neg : pos)}";
    }
}
