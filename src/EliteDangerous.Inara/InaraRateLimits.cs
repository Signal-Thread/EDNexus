using System.Text.RegularExpressions;

namespace EliteDangerous.Inara;

/// <summary>Rate-limit facts shared by the write (<see cref="InaraClient"/>) and read (<see cref="InaraReadClient"/>) paths.</summary>
public static class InaraRateLimits
{
    /// <summary>
    /// Minimum gap between live requests for one API key. Inara allows about two requests per minute,
    /// so callers (the write-side bridge and the read client) share this one number.
    /// </summary>
    public static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(30);

    // Inara's exact wording for a rate-limit refusal in a JSON body isn't documented; match the usual phrasings.
    private static readonly Regex Wording =
        new(@"too many|rate[\s-]?limit|limit.{0,20}exceed|exceed.{0,20}limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="statusText"/> reads like a rate-limit refusal.</summary>
    public static bool IsRateLimitWording([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? statusText) => statusText is not null && Wording.IsMatch(statusText);
}
