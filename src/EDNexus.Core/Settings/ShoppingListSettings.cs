using EDNexus.Core.ShoppingList;

namespace EDNexus.Core.Settings;

/// <summary>Persisted state of the unified shopping list card.</summary>
public sealed class ShoppingListSettings
{
    /// <summary>The commander's hand-entered lines. Linked sources are derived live and never stored here.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(LenientShoppingEntryListConverter))]
    public List<ShoppingEntry> Manual { get; set; } = new();

    /// <summary>When true, the active colonisation depot's remaining needs are merged into the list.</summary>
    public bool LinkColonisation { get; set; }

    /// <summary>When true, the pinned engineering goal's material needs are merged into the list.</summary>
    public bool LinkEngineering { get; set; }

    /// <summary>When true, rows whose requirement is already covered by the inventory are hidden.</summary>
    public bool HideCompleted { get; set; }
}
