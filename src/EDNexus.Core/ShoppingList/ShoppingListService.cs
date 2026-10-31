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
