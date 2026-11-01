namespace EDNexus.Core.Colonisation;

/// <summary>
/// One commodity line on a construction depot: how much is <see cref="Required"/>, how much has
/// been <see cref="Provided"/> so far, and the per-unit <see cref="Payment"/> the depot pays.
/// </summary>
/// <param name="Name">Localised display name, e.g. "Fruit and Vegetables".</param>
/// <param name="Symbol">Canonical key (see <see cref="CommodityName"/>) for cross-referencing.</param>
public sealed record ColonisationResource(string Name, string Symbol, int Required, int Provided, int Payment)
{
    /// <summary>Tons still outstanding for this commodity (never negative).</summary>
    public int Remaining => Math.Max(0, Required - Provided);

    /// <summary>Delivery progress for this commodity, clamped to 0..1.</summary>
    public double Fraction => Required > 0 ? Math.Clamp((double)Provided / Required, 0, 1) : 1;

    public bool IsComplete => Remaining == 0;
}

/// <summary>
/// A construction site keyed by its market id, holding the latest depot snapshot: overall
/// <see cref="Progress"/> and the per-commodity <see cref="Resources"/>. Instances are immutable;
/// the tracker replaces them wholesale as new events arrive.
/// </summary>
public sealed class ColonisationSite
{
    public required long MarketId { get; init; }

    /// <summary>Where the construction ship is, captured from live state when the depot was seen.</summary>
    public string? StationName { get; init; }
    public string? StarSystem { get; init; }

    /// <summary>Overall build progress reported by the game (0..1).</summary>
    public double Progress { get; init; }
    public bool Complete { get; init; }
    public bool Failed { get; init; }
    public DateTimeOffset Updated { get; init; }

    public IReadOnlyList<ColonisationResource> Resources { get; init; } = Array.Empty<ColonisationResource>();

    /// <summary>Commodities still owing at least one ton, worst shortfall first.</summary>
    public IEnumerable<ColonisationResource> Outstanding =>
        Resources.Where(r => !r.IsComplete).OrderByDescending(r => r.Remaining);

    public int TotalRequired => Resources.Sum(r => r.Required);
    public int TotalProvided => Resources.Sum(r => Math.Min(r.Provided, r.Required));
    public int TotalRemaining => Resources.Sum(r => r.Remaining);
    public int CompletedCount => Resources.Count(r => r.IsComplete);

    /// <summary>
    /// The auto shopping list: every outstanding commodity, sorted by shortfall, cross-referenced
    /// against the current cargo hold so the caller can see what is already aboard.
    /// </summary>
    /// <param name="cargo">Cargo hold as commodity-name → tons, in any journal name form.</param>
    /// <param name="carrier">Fleet-carrier stock, same shape; counted toward what is already sourced.</param>
    public IReadOnlyList<ShoppingListItem> BuildShoppingList(
        IReadOnlyDictionary<string, int>? cargo = null, IReadOnlyDictionary<string, int>? carrier = null)
    {
        var hold = BuildHoldLookup(cargo);
        var stored = BuildHoldLookup(carrier);
        var list = new List<ShoppingListItem>();
        foreach (var r in Outstanding)
        {
            hold.TryGetValue(r.Symbol, out var inHold);
            stored.TryGetValue(r.Symbol, out var onCarrier);
            list.Add(new ShoppingListItem(r.Name, r.Required, r.Provided, r.Remaining, inHold, onCarrier));
        }
        return list;
    }

    private static Dictionary<string, int> BuildHoldLookup(IReadOnlyDictionary<string, int>? cargo)
    {
        var hold = new Dictionary<string, int>();
        if (cargo is null) return hold;
        foreach (var (name, count) in cargo)
        {
            var key = CommodityName.Canonicalize(name);
            if (key.Length == 0) continue;
            hold[key] = hold.TryGetValue(key, out var existing) ? existing + count : count;
        }
        return hold;
    }
}

/// <summary>
/// A shopping-list row: an outstanding commodity plus how much of it is already in the hold, so the
/// caller can distinguish what still needs buying (<see cref="StillNeeded"/>) from what is being
/// carried toward the requirement (<see cref="Carrying"/>).
/// </summary>
/// <param name="OnCarrier">Tons of this commodity stored in the fleet carrier.</param>
public sealed record ShoppingListItem(
    string Name, int Required, int Provided, int Remaining, int InHold, int OnCarrier = 0)
{
    /// <summary>Tons still to acquire after accounting for what is aboard and on the carrier.</summary>
    public int StillNeeded => Math.Max(0, Remaining - InHold - OnCarrier);

    /// <summary>Tons in the hold that count toward this requirement (capped at the shortfall).</summary>
    public int Carrying => Math.Min(InHold, Remaining);

    /// <summary>True when the hold already covers the entire remaining requirement.</summary>
    public bool CoveredByHold => Remaining > 0 && InHold >= Remaining;

    /// <summary>True when the hold and the carrier together already cover the remaining requirement.</summary>
    public bool CoveredByStock => Remaining > 0 && InHold + OnCarrier >= Remaining;

    /// <summary>Delivery progress for this commodity, clamped to 0..1.</summary>
    public double Fraction => Required > 0 ? Math.Clamp((double)Provided / Required, 0, 1) : 1;
}
