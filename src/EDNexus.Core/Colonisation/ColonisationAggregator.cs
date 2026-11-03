namespace EDNexus.Core.Colonisation;

/// <summary>One project's share of a combined commodity line.</summary>
public sealed record ProjectShare(long MarketId, string ProjectName, int Remaining);

/// <summary>
/// One commodity summed across every open project: how much is wanted in total, how much has been
/// delivered, how much is still owed, and which projects owe it.
/// </summary>
/// <param name="Projects">Projects still owing this commodity, largest share first.</param>
public sealed record CombinedCommodity(
    string Name, string Symbol, int Required, int Provided, int Remaining, IReadOnlyList<ProjectShare> Projects)
{
    /// <summary>Delivery progress across the open projects, clamped to 0..1.</summary>
    public double Fraction => Required > 0 ? Math.Clamp((double)Provided / Required, 0, 1) : 1;
}

/// <summary>
/// Pure projection of several colonisation sites onto one list of what is still needed to finish
/// all of them. Completed, failed and fully supplied projects are left out.
/// </summary>
public static class ColonisationAggregator
{
    /// <summary>
    /// Sums the outstanding commodities of every open site, keyed by canonical symbol, biggest
    /// shortfall first (ties broken by name). A line appears only if some project still owes it.
    /// </summary>
    public static IReadOnlyList<CombinedCommodity> Combine(IEnumerable<ColonisationSite> sites)
    {
        var lines = new Dictionary<string, Builder>(StringComparer.Ordinal);

        foreach (var site in sites)
        {
            if (!site.IsOpen) continue;
            foreach (var r in site.Resources)
            {
                var key = r.Symbol.Length > 0 ? r.Symbol : CommodityName.Canonicalize(r.Name);
                if (key.Length == 0) continue;
                if (!lines.TryGetValue(key, out var line)) lines[key] = line = new Builder(r.Name, key);

                line.Required += r.Required;
                line.Provided += Math.Min(r.Provided, r.Required);
                if (r.Remaining > 0)
                {
                    line.Remaining += r.Remaining;
                    line.Shares.Add(new ProjectShare(site.MarketId, site.DisplayName, r.Remaining));
                }
            }
        }

        return lines.Values
            .Where(l => l.Remaining > 0)
            .Select(l => new CombinedCommodity(
                l.Name, l.Symbol, Clamp(l.Required), Clamp(l.Provided), Clamp(l.Remaining),
                l.Shares.OrderByDescending(s => s.Remaining)
                    .ThenBy(s => s.ProjectName, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(l => l.Remaining)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int Clamp(long v) => (int)Math.Min(v, int.MaxValue);

    private sealed class Builder(string name, string symbol)
    {
        public string Name { get; } = name;
        public string Symbol { get; } = symbol;
        public long Required;
        public long Provided;
        public long Remaining;
        public List<ProjectShare> Shares { get; } = new();
    }
}
