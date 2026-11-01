using System.Linq;
using EDNexus.Core.Settings;
using EDNexus.Core.ShoppingList;
using Xunit;

namespace EDNexus.Tests.ShoppingList;

public class ShoppingListManualTests
{
    [Fact]
    public void Add_creates_a_manual_entry_and_keeps_flags()
    {
        var start = new ShoppingListSettings { LinkColonisation = true, HideCompleted = true };

        var next = ShoppingListService.AddManual(start, ShoppingItemKind.Commodity, "  Titanium ", 10);

        var e = Assert.Single(next.Manual);
        Assert.Equal(ShoppingItemKind.Commodity, e.Kind);
        Assert.Equal("Titanium", e.Name);
        Assert.Equal(10, e.Needed);
        Assert.Equal(ShoppingItemSource.Manual, e.Source);
        Assert.True(next.LinkColonisation);
        Assert.False(next.LinkEngineering);
        Assert.True(next.HideCompleted);
    }

    [Fact]
    public void Add_merges_quantity_into_same_normalised_item()
    {
        var s = ShoppingListService.AddManual(new ShoppingListSettings(), ShoppingItemKind.Commodity, "Titanium", 10);
        s = ShoppingListService.AddManual(s, ShoppingItemKind.Commodity, "titanium", 5);

        var e = Assert.Single(s.Manual);
        Assert.Equal(15, e.Needed);
    }

    [Fact]
    public void Add_same_text_under_a_different_kind_is_a_separate_entry()
    {
        var s = ShoppingListService.AddManual(new ShoppingListSettings(), ShoppingItemKind.Commodity, "Iron", 1);
        s = ShoppingListService.AddManual(s, ShoppingItemKind.Raw, "Iron", 1);

        Assert.Equal(2, s.Manual.Count);
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("   ", 5)]
    [InlineData("Titanium", 0)]
    [InlineData("Titanium", -3)]
    public void Add_ignores_blank_text_or_non_positive_quantity(string text, int qty)
    {
        var start = new ShoppingListSettings();

        var next = ShoppingListService.AddManual(start, ShoppingItemKind.Commodity, text, qty);

        Assert.Empty(next.Manual);
    }

    [Fact]
    public void Add_never_mutates_the_passed_instance()
    {
        var start = new ShoppingListSettings();
        start.Manual.Add(new ShoppingEntry(ShoppingItemKind.Commodity, "gold", "Gold", 2));

        var next = ShoppingListService.AddManual(start, ShoppingItemKind.Commodity, "Gold", 3);

        Assert.NotSame(start, next);
        Assert.NotSame(start.Manual, next.Manual);
        Assert.Equal(2, Assert.Single(start.Manual).Needed);
        Assert.Equal(5, Assert.Single(next.Manual).Needed);
    }

    [Fact]
    public void Remove_drops_the_matching_entry_only()
    {
        var s = ShoppingListService.AddManual(new ShoppingListSettings(), ShoppingItemKind.Commodity, "Gold", 2);
        s = ShoppingListService.AddManual(s, ShoppingItemKind.Commodity, "Silver", 2);
        var key = ShoppingListService.Resolve(ShoppingItemKind.Commodity, "Gold").Key;

        var next = ShoppingListService.RemoveManual(s, ShoppingItemKind.Commodity, key);

        Assert.Equal("Silver", Assert.Single(next.Manual).Name);
        Assert.Equal(2, s.Manual.Count);   // original untouched
    }

    [Fact]
    public void Remove_matches_the_stemmed_row_key()
    {
        // Row keys for commodities are stemmed ("lowtemperaturediamond"); the stored entry holds the
        // canonical, un-stemmed form ("lowtemperaturediamonds").
        var s = ShoppingListService.AddManual(new ShoppingListSettings(), ShoppingItemKind.Commodity, "Low Temperature Diamonds", 4);
        var rowKey = ShoppingListService.Build(s.Manual, new EDNexus.Core.State.CommanderState()).Single().Key;

        var next = ShoppingListService.RemoveManual(s, ShoppingItemKind.Commodity, rowKey);

        Assert.Empty(next.Manual);
    }

    [Fact]
    public void Remove_of_unknown_key_is_a_no_op_copy()
    {
        var s = ShoppingListService.AddManual(new ShoppingListSettings(), ShoppingItemKind.Commodity, "Gold", 2);

        var next = ShoppingListService.RemoveManual(s, ShoppingItemKind.Commodity, "nothing");

        Assert.Single(next.Manual);
        Assert.NotSame(s, next);
    }
}

public class ShoppingListNameMergeTests
{
    [Fact]
    public void Non_manual_name_wins_over_manual_regardless_of_order()
    {
        var state = new EDNexus.Core.State.CommanderState();
        var manual = new ShoppingEntry(ShoppingItemKind.Commodity, "steel", "steel", 5, ShoppingItemSource.Manual);
        var depot = new ShoppingEntry(ShoppingItemKind.Commodity, "$steel_name;", "Steel", 10, ShoppingItemSource.Colonisation);

        var a = Assert.Single(ShoppingListService.Build([manual, depot], state));
        var b = Assert.Single(ShoppingListService.Build([depot, manual], state));

        Assert.Equal("Steel", a.Name);
        Assert.Equal("Steel", b.Name);
        Assert.Equal(15, a.Needed);
    }

    [Fact]
    public void Raw_symbol_name_never_overrides_a_real_name()
    {
        var state = new EDNexus.Core.State.CommanderState();
        var manual = new ShoppingEntry(ShoppingItemKind.Commodity, "steel", "Steel", 5, ShoppingItemSource.Manual);
        var depot = new ShoppingEntry(ShoppingItemKind.Commodity, "steel", "$steel_name;", 10, ShoppingItemSource.Colonisation);

        Assert.Equal("Steel", Assert.Single(ShoppingListService.Build([manual, depot], state)).Name);
    }
}
