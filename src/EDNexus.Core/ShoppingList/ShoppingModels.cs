using System.Text.Json.Serialization;

namespace EDNexus.Core.ShoppingList;

/// <summary>What sort of inventory item a shopping-list line refers to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShoppingItemKind
{
    Commodity,
    Raw,
    Manufactured,
    Encoded,
    Component,
    Data,
    Item,
    Consumable,
}

/// <summary>Where a shopping-list entry came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShoppingItemSource
{
    Manual,
    Colonisation,
    Engineering,
}

/// <summary>One requested line: <paramref name="Needed"/> units of an item, from a given source.</summary>
/// <param name="Key">Journal or display name; normalised when rows are built.</param>
/// <param name="Name">Display name.</param>
public sealed record ShoppingEntry(
    ShoppingItemKind Kind,
    string Key,
    string Name,
    int Needed,
    ShoppingItemSource Source = ShoppingItemSource.Manual);

/// <summary>A merged shopping-list row, projected against the commander's live inventory.</summary>
public sealed record ShoppingRow(
    ShoppingItemKind Kind,
    string Key,
    string Name,
    int Needed,
    int Held,
    IReadOnlyList<ShoppingItemSource> Sources,
    string Hint)
{
    /// <summary>Units still to acquire (never negative).</summary>
    public int StillNeeded => Math.Max(0, Needed - Held);

    /// <summary>True when the inventory already covers the requirement.</summary>
    public bool Satisfied => Held >= Needed;

    /// <summary>Progress toward the requirement, clamped to 0..1.</summary>
    public double Fraction => Needed > 0 ? Math.Clamp((double)Held / Needed, 0, 1) : 1;

    /// <summary>True when a market search is meaningful: an unsatisfied commodity.</summary>
    public bool CanFind => Kind == ShoppingItemKind.Commodity && !Satisfied;
}
