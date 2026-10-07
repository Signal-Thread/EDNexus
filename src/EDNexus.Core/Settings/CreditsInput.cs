using System.Globalization;

namespace EDNexus.Core.Settings;

/// <summary>Parses a credit amount typed into a settings box.</summary>
public static class CreditsInput
{
    /// <summary>
    /// Reads a whole number of credits, accepting thousands separators ("1,500,000") in the current
    /// culture or the invariant one. Blank is valid and means zero (the setting is switched off).
    /// Anything else that is not a non-negative whole number that fits is rejected, so a typo is
    /// reported instead of silently becoming zero and wiping the saved value.
    /// </summary>
    public static bool TryParse(string? text, out int credits)
    {
        credits = 0;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return true;

        const NumberStyles styles = NumberStyles.Integer | NumberStyles.AllowThousands;
        if (!int.TryParse(trimmed, styles, CultureInfo.CurrentCulture, out var value)
            && !int.TryParse(trimmed, styles, CultureInfo.InvariantCulture, out value))
            return false;

        if (value < 0) return false;
        credits = value;
        return true;
    }
}
