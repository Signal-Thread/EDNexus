using EDNexus.Core.Colonisation;
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
                groups[id] = (g.Name, g.Needed + e.Needed, g.Sources);
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(e.Name) ? e.Key.Trim() : e.Name;
                groups[id] = (name, e.Needed, [e.Source]);
                order.Add(id);
            }
        }

        var rows = new List<ShoppingRow>(order.Count);
        foreach (var id in order)
        {
            var (name, needed, sources) = groups[id];
            rows.Add(new ShoppingRow(id.Item1, id.Item2, name, needed, Held(id.Item1, id.Item2, state), sources, string.Empty));
        }

        IEnumerable<ShoppingRow> result = rows;
        if (hideCompleted) result = result.Where(r => !r.Satisfied);

        return result
            .OrderBy(r => r.Satisfied)
            .ThenByDescending(r => r.StillNeeded)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Units of an item currently held; zero for unknown kinds or keys.</summary>
    internal static int Held(ShoppingItemKind kind, string key, CommanderState state)
    {
        if (kind == ShoppingItemKind.Commodity)
        {
            var want = Stem(CommodityName.Canonicalize(key));
            if (want.Length == 0) return 0;
            var total = 0;
            foreach (var (name, count) in state.Cargo)
                if (Stem(CommodityName.Canonicalize(name)) == want) total += count;
            return total;
        }

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
        if (bucket is null) return 0;
        var k = key?.Trim() ?? string.Empty;
        return k.Length > 0 && bucket.TryGetValue(k, out var n) ? n : 0;
    }

    /// <summary>
    /// The journal symbol is singular ("lowtemperaturediamond") while the localised label is plural
    /// ("Low Temperature Diamonds"); dropping one trailing 's' lets both forms meet in the hold.
    /// </summary>
    private static string Stem(string canonical) =>
        canonical.Length > 3 && canonical[^1] == 's' ? canonical[..^1] : canonical;

    private static string Normalise(ShoppingItemKind kind, string? key) =>
        kind == ShoppingItemKind.Commodity
            ? CommodityName.Canonicalize(key)
            : (key ?? string.Empty).Trim().ToLowerInvariant();
}
