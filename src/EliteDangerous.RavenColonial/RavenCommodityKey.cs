namespace EliteDangerous.RavenColonial;

/// <summary>
/// Raven Colonial's spelling of a commodity in a carrier's cargo map: lower case, with the journal's
/// <c>$</c> prefix and <c>_name;</c> suffix removed. Anything else in the name is left alone, which is
/// deliberately <em>not</em> the same as EDNexus's own canonical key (that one drops every
/// non-alphanumeric character); a name Raven would spell with punctuation must keep it.
/// </summary>
/// <remarks>
/// Mirrors <c>normalize_commodity_key</c> in the RavenColonial EDMC plugin, which is what the carriers'
/// cargo on the service was written with.
/// </remarks>
public static class RavenCommodityKey
{
    /// <summary>The key for a commodity name in any journal form; empty for null or blank.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        return name.Replace("$", "")
            .Replace("_name;", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_name", "", StringComparison.OrdinalIgnoreCase)
            .Trim().ToLowerInvariant();
    }
}
