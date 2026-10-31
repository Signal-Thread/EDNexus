using EDNexus.Core.Colonisation;
using EDNexus.Core.Engineering;
using EDNexus.Core.Odyssey;
using EDNexus.Core.Settings;
using EDNexus.Core.State;

namespace EDNexus.Core.ShoppingList;

/// <summary>
/// Pure projection of shopping-list entries against a <see cref="CommanderState"/>: merges
/// duplicates, reads what is held, and sorts the biggest shortfall first. Reads state only.
/// </summary>
public static class ShoppingListService
{
    /// <summary>Builds the merged, sorted rows for <paramref name="entries"/>.</summary>
    /// <param name="hideCompleted">Drop rows whose requirement is already met.</param>
    public static IReadOnlyList<ShoppingRow> Build(
        IEnumerable<ShoppingEntry> entries, CommanderState state, bool hideCompleted = false)
    {
        var groups = new Dictionary<(ShoppingItemKind, string), (string Name, int Needed, List<ShoppingItemSource> Sources)>();
        var order = new List<(ShoppingItemKind, string)>();

        foreach (var e in entries)
        {
            var key = Normalise(e.Kind, e.Key);
            if (key.Length == 0) continue;
            var id = (e.Kind, key);
            if (groups.TryGetValue(id, out var g))
            {
                if (!g.Sources.Contains(e.Source)) g.Sources.Add(e.Source);
                var name = IsRawSymbol(g.Name) && !IsRawSymbol(e.Name) && !string.IsNullOrWhiteSpace(e.Name) ? e.Name : g.Name;
                groups[id] = (name, g.Needed + e.Needed, g.Sources);
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(e.Name) ? e.Key.Trim() : e.Name;
                groups[id] = (name, e.Needed, [e.Source]);
                order.Add(id);
            }
        }

        var hold = BuildHoldLookup(state);
        var rows = new List<ShoppingRow>(order.Count);
        foreach (var id in order)
        {
            var (name, needed, sources) = groups[id];
            rows.Add(new ShoppingRow(id.Item1, id.Item2, name, needed, HeldNormalised(id.Item1, id.Item2, hold, state), sources, string.Empty));
            var row = rows[^1];
            if (!row.Satisfied && row.Kind != ShoppingItemKind.Commodity)
                rows[^1] = row with { Hint = CatalogSource(row.Kind, row.Key) };
        }

        IEnumerable<ShoppingRow> result = rows;
        if (hideCompleted) result = result.Where(r => !r.Satisfied);

        return result
            .OrderBy(r => r.Satisfied)
            .ThenByDescending(r => r.StillNeeded)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Commodity entries for every outstanding line of a colonisation depot; empty for no site.</summary>
    public static IReadOnlyList<ShoppingEntry> FromColonisation(ColonisationSite? site)
    {
        if (site is null) return [];
        return site.Outstanding
            .Select(r => new ShoppingEntry(ShoppingItemKind.Commodity, r.Symbol, r.Name, r.Remaining, ShoppingItemSource.Colonisation))
            .ToList();
    }

    /// <summary>
    /// Entries for the pinned ship blueprint (costed at one roll) and the pinned on-foot suit or weapon
    /// upgrade. Materials in an unrecognised category are skipped. Empty when nothing is pinned.
    /// </summary>
    public static IReadOnlyList<ShoppingEntry> FromEngineering(
        EngineeringSettings pin, EngineeringTracker engineering, CommanderState state)
    {
        var entries = new List<ShoppingEntry>();

        if (!string.IsNullOrWhiteSpace(pin.PinnedBlueprintId))
        {
            var plan = engineering.BuildPlan(pin.PinnedBlueprintId, pin.PinnedGrade, state, 1);
            if (plan is not null)
                foreach (var m in plan.Materials)
                    AddEngineering(entries, m.Category, m.Symbol, m.Name, m.Needed);
        }

        if (!string.IsNullOrWhiteSpace(pin.PinnedOnFootId))
        {
            IReadOnlyList<UpgradeRequirement>? materials = string.Equals(pin.PinnedOnFootKind, "weapon", StringComparison.OrdinalIgnoreCase)
                ? engineering.BuildWeaponUpgradePlan(pin.PinnedOnFootId, pin.PinnedOnFootGrade, state)?.Materials
                : engineering.BuildSuitUpgradePlan(pin.PinnedOnFootId, pin.PinnedOnFootGrade, state)?.Materials;
            if (materials is not null)
                foreach (var m in materials)
                    AddEngineering(entries, m.Category, m.Symbol, m.Name, m.Needed);
        }

        return entries;
    }

    private static void AddEngineering(List<ShoppingEntry> entries, string? category, string symbol, string name, int needed)
    {
        if (needed <= 0 || !TryKind(category, out var kind)) return;
        entries.Add(new ShoppingEntry(kind, symbol, name, needed, ShoppingItemSource.Engineering));
    }

    private static bool TryKind(string? category, out ShoppingItemKind kind)
    {
        switch (category?.Trim().ToLowerInvariant())
        {
            case "raw": kind = ShoppingItemKind.Raw; return true;
            case "manufactured": kind = ShoppingItemKind.Manufactured; return true;
            case "encoded": kind = ShoppingItemKind.Encoded; return true;
            case "component": kind = ShoppingItemKind.Component; return true;
            case "data": kind = ShoppingItemKind.Data; return true;
            case "item": kind = ShoppingItemKind.Item; return true;
            case "consumable": kind = ShoppingItemKind.Consumable; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>
    /// Resolves free text to a catalog item of <paramref name="kind"/> by symbol or name (case- and
    /// space-insensitive). Commodities canonicalise the text and keep it as the name. Unmatched text
    /// comes back as itself (lower-cased key, trimmed name) rather than throwing.
    /// </summary>
    public static (string Key, string Name) Resolve(ShoppingItemKind kind, string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (kind == ShoppingItemKind.Commodity)
            return (CommodityName.Canonicalize(trimmed), trimmed);

        var needle = Squash(trimmed);
        if (needle.Length > 0)
        {
            foreach (var (symbol, name, category, _) in CatalogItems())
            {
                if (!TryKind(category, out var k) || k != kind) continue;
                if (Squash(symbol) == needle || Squash(name) == needle) return (symbol.ToLowerInvariant(), name);
            }
        }
        return (trimmed.ToLowerInvariant(), trimmed);
    }

    private static string CatalogSource(ShoppingItemKind kind, string key)
    {
        foreach (var (symbol, _, category, source) in CatalogItems())
            if (TryKind(category, out var k) && k == kind && string.Equals(symbol, key, StringComparison.OrdinalIgnoreCase))
                return source ?? string.Empty;
        return string.Empty;
    }

    private static IEnumerable<(string Symbol, string Name, string Category, string? Source)> CatalogItems()
    {
        foreach (var m in EngineeringCatalog.Default.Materials) yield return (m.Symbol, m.Name, m.Category, m.Source);
        foreach (var m in OdysseyCatalog.Default.Materials) yield return (m.Symbol, m.Name, m.Category, m.Source);
    }

    private static string Squash(string? s) =>
        new string((s ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    /// <summary>Units of an item currently held; zero for unknown kinds or keys.</summary>
    internal static int Held(ShoppingItemKind kind, string key, CommanderState state) =>
        HeldNormalised(kind, Normalise(kind, key), BuildHoldLookup(state), state);

    /// <summary>Stemmed, canonicalised copy of the cargo hold (journal and display forms merged).</summary>
    private static Dictionary<string, int> BuildHoldLookup(CommanderState state)
    {
        var hold = new Dictionary<string, int>();
        foreach (var (name, count) in state.Cargo)
        {
            var key = Normalise(ShoppingItemKind.Commodity, name);
            if (key.Length == 0) continue;
            hold[key] = hold.TryGetValue(key, out var existing) ? existing + count : count;
        }
        return hold;
    }

    private static int HeldNormalised(ShoppingItemKind kind, string key, Dictionary<string, int> hold, CommanderState state)
    {
        if (key.Length == 0) return 0;
        if (kind == ShoppingItemKind.Commodity) return hold.TryGetValue(key, out var c) ? c : 0;

        var bucket = kind switch
        {
            ShoppingItemKind.Raw => state.Materials.Raw,
            ShoppingItemKind.Manufactured => state.Materials.Manufactured,
            ShoppingItemKind.Encoded => state.Materials.Encoded,
            ShoppingItemKind.Component => state.OnFoot.Components,
            ShoppingItemKind.Data => state.OnFoot.Data,
            ShoppingItemKind.Item => state.OnFoot.Items,
            ShoppingItemKind.Consumable => state.OnFoot.Consumables,
            _ => null,
        };
        return bucket is not null && bucket.TryGetValue(key, out var n) ? n : 0;
    }

    private static bool IsRawSymbol(string? name) => name is not null && name.TrimStart().StartsWith('$');

    /// <summary>
    /// The journal symbol is singular ("lowtemperaturediamond") while the localised label is plural
    /// ("Low Temperature Diamonds"); dropping one trailing 's' lets both forms meet in the hold.
    /// Trade-off: two distinct commodities differing only by a trailing 's' would collide (none known).
    /// Not idempotent, so apply it exactly once, inside <see cref="Normalise"/>.
    /// </summary>
    private static string Stem(string canonical) =>
        canonical.Length > 3 && canonical[^1] == 's' ? canonical[..^1] : canonical;

    private static string Normalise(ShoppingItemKind kind, string? key) =>
        kind == ShoppingItemKind.Commodity
            ? Stem(CommodityName.Canonicalize(key))
            : (key ?? string.Empty).Trim().ToLowerInvariant();
}
