using System.Linq;
using EDNexus.Core.ShoppingList;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.ShoppingList;

public class ShoppingListServiceTests
{
    [Fact]
    public void Held_reads_each_inventory()
    {
        var s = new CommanderState();
        s.Cargo["steel"] = 5;
        s.Materials.Raw["iron"] = 7;
        s.Materials.Manufactured["shieldemitters"] = 2;
        s.Materials.Encoded["shielddensityreports"] = 3;
        s.OnFoot.Components["circuitboard"] = 4;
        s.OnFoot.Data["ident"] = 6;
        s.OnFoot.Items["medkit"] = 8;
        s.OnFoot.Consumables["healthpack"] = 9;

        Assert.Equal(5, ShoppingListService.Held(ShoppingItemKind.Commodity, "steel", s));
        Assert.Equal(7, ShoppingListService.Held(ShoppingItemKind.Raw, "iron", s));
        Assert.Equal(2, ShoppingListService.Held(ShoppingItemKind.Manufactured, "shieldemitters", s));
        Assert.Equal(3, ShoppingListService.Held(ShoppingItemKind.Encoded, "shielddensityreports", s));
        Assert.Equal(4, ShoppingListService.Held(ShoppingItemKind.Component, "circuitboard", s));
        Assert.Equal(6, ShoppingListService.Held(ShoppingItemKind.Data, "ident", s));
        Assert.Equal(8, ShoppingListService.Held(ShoppingItemKind.Item, "medkit", s));
        Assert.Equal(9, ShoppingListService.Held(ShoppingItemKind.Consumable, "healthpack", s));
    }

    [Fact]
    public void Commodity_key_matches_journal_and_display_forms()
    {
        var s = new CommanderState();
        s.Cargo["lowtemperaturediamond"] = 3;

        var rows = ShoppingListService.Build(
            [new ShoppingEntry(ShoppingItemKind.Commodity, "Low Temperature Diamonds", "Low Temperature Diamonds", 10)], s);

        Assert.Single(rows);
        Assert.Equal(3, rows[0].Held);
    }

    [Fact]
    public void Duplicate_entries_merge_and_sum()
    {
        var rows = ShoppingListService.Build(
        [
            new ShoppingEntry(ShoppingItemKind.Commodity, "Steel", "Steel", 100, ShoppingItemSource.Colonisation),
            new ShoppingEntry(ShoppingItemKind.Commodity, "steel", "steel", 10, ShoppingItemSource.Manual),
        ], new CommanderState());

        var row = Assert.Single(rows);
        Assert.Equal(110, row.Needed);
        Assert.Contains(ShoppingItemSource.Colonisation, row.Sources);
        Assert.Contains(ShoppingItemSource.Manual, row.Sources);
    }

    [Fact]
    public void Rows_sort_unsatisfied_first_by_shortfall()
    {
        var s = new CommanderState();
        s.Cargo["done"] = 50;
        var rows = ShoppingListService.Build(
        [
            new ShoppingEntry(ShoppingItemKind.Commodity, "done", "Done", 10),
            new ShoppingEntry(ShoppingItemKind.Commodity, "small", "Small", 5),
            new ShoppingEntry(ShoppingItemKind.Commodity, "big", "Big", 90),
        ], s);

        Assert.Equal(["Big", "Small", "Done"], rows.Select(r => r.Name).ToArray());
        Assert.True(rows[2].Satisfied);
        Assert.Equal(0, rows[2].StillNeeded);
        Assert.Equal(1.0, rows[2].Fraction);
        Assert.False(rows[2].CanFind);
        Assert.True(rows[0].CanFind);
    }

    [Fact]
    public void HideCompleted_drops_satisfied_rows()
    {
        var s = new CommanderState();
        s.Cargo["done"] = 50;
        var rows = ShoppingListService.Build(
        [
            new ShoppingEntry(ShoppingItemKind.Commodity, "done", "Done", 10),
            new ShoppingEntry(ShoppingItemKind.Commodity, "big", "Big", 90),
        ], s, hideCompleted: true);

        var row = Assert.Single(rows);
        Assert.Equal("Big", row.Name);
    }

    [Fact]
    public void Unknown_kind_key_yields_zero_held_without_throwing()
    {
        var s = new CommanderState();
        Assert.Equal(0, ShoppingListService.Held(ShoppingItemKind.Raw, "nope", s));
        Assert.Equal(0, ShoppingListService.Held((ShoppingItemKind)999, "x", s));
        Assert.Equal(0, ShoppingListService.Held(ShoppingItemKind.Commodity, "", s));

        var rows = ShoppingListService.Build(
            [new ShoppingEntry(ShoppingItemKind.Encoded, "nope", "Nope", 2)], s);
        Assert.Equal(0, Assert.Single(rows).Held);
    }
}
