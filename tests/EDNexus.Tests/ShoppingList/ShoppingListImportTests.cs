using System.Linq;
using EDNexus.Core.Colonisation;
using EDNexus.Core.Engineering;
using EDNexus.Core.Journal;
using EDNexus.Core.Odyssey;
using EDNexus.Core.Settings;
using EDNexus.Core.ShoppingList;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.ShoppingList;

public class ShoppingListImportTests
{
    private const string Depot = """
    { "timestamp":"2026-06-03T00:47:02Z", "event":"ColonisationConstructionDepot", "MarketID":3956023042,
      "ConstructionProgress":0.5, "ConstructionComplete":false, "ConstructionFailed":false,
      "ResourcesRequired":[
        { "Name":"$aluminium_name;", "Name_Localised":"Aluminium", "RequiredAmount":100, "ProvidedAmount":40, "Payment":3239 },
        { "Name":"$steel_name;", "Name_Localised":"Steel", "RequiredAmount":200, "ProvidedAmount":200, "Payment":5057 },
        { "Name":"$cmmcomposite_name;", "Name_Localised":"CMM Composite", "RequiredAmount":500, "ProvidedAmount":0, "Payment":6788 }
      ] }
    """;

    private static ColonisationSite BuildSite()
    {
        var bus = new JournalEventBus();
        var tracker = new ColonisationTracker(bus, new CommanderState());
        Assert.True(JournalEntry.TryParse(Depot, historical: false, out var entry));
        bus.Publish(entry);
        return tracker.ActiveSite!;
    }

    [Fact]
    public void Colonisation_yields_outstanding_commodities_only()
    {
        var entries = ShoppingListService.FromColonisation(BuildSite());

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(ShoppingItemKind.Commodity, e.Kind);
            Assert.Equal(ShoppingItemSource.Colonisation, e.Source);
        });
        Assert.Equal(60, entries.Single(e => e.Name == "Aluminium").Needed);
        Assert.Equal(500, entries.Single(e => e.Name == "CMM Composite").Needed);
        Assert.DoesNotContain(entries, e => e.Name == "Steel");
    }

    [Fact]
    public void Colonisation_null_site_is_empty()
    {
        Assert.Empty(ShoppingListService.FromColonisation(null));
    }

    [Fact]
    public void Colonisation_entries_merge_with_cargo_in_build()
    {
        var state = new CommanderState();
        state.Cargo["aluminium"] = 25;

        var rows = ShoppingListService.Build(ShoppingListService.FromColonisation(BuildSite()), state);

        var al = rows.Single(r => r.Name == "Aluminium");
        Assert.Equal(25, al.Held);
        Assert.Equal(35, al.StillNeeded);
    }

    [Fact]
    public void Engineering_pinned_ship_blueprint_is_tagged_with_correct_kinds()
    {
        var engineering = new EngineeringTracker(new JournalEventBus());
        var pin = new EngineeringSettings { PinnedBlueprintId = "fsd_increased_range", PinnedGrade = 5 };

        var entries = ShoppingListService.FromEngineering(pin, engineering, new CommanderState());

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.Equal(ShoppingItemSource.Engineering, e.Source);
            Assert.Contains(e.Kind, new[] { ShoppingItemKind.Raw, ShoppingItemKind.Manufactured, ShoppingItemKind.Encoded });
            Assert.True(e.Needed > 0);
            Assert.False(e.Name.StartsWith('$'));
        });

        var plan = engineering.BuildPlan("fsd_increased_range", 5, new CommanderState())!;
        foreach (var m in plan.Materials)
        {
            var expected = m.Category.ToLowerInvariant() switch
            {
                "raw" => ShoppingItemKind.Raw,
                "manufactured" => ShoppingItemKind.Manufactured,
                _ => ShoppingItemKind.Encoded,
            };
            Assert.Contains(entries, e => e.Kind == expected && e.Key == m.Symbol && e.Needed == m.Needed);
        }
    }

    [Fact]
    public void Engineering_empty_pin_is_empty()
    {
        var engineering = new EngineeringTracker(new JournalEventBus());

        Assert.Empty(ShoppingListService.FromEngineering(new EngineeringSettings(), engineering, new CommanderState()));
    }

    [Fact]
    public void Engineering_on_foot_pin_maps_catalog_categories()
    {
        var engineering = new EngineeringTracker(new JournalEventBus());
        var suit = OdysseyCatalog.Default.Suits.First(x => x.IsUpgradeable);
        var pin = new EngineeringSettings { PinnedOnFootKind = "suit", PinnedOnFootId = suit.Id, PinnedOnFootGrade = 3 };

        var entries = ShoppingListService.FromEngineering(pin, engineering, new CommanderState());

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.Equal(ShoppingItemSource.Engineering, e.Source);
            Assert.Contains(e.Kind, new[]
            {
                ShoppingItemKind.Component, ShoppingItemKind.Data, ShoppingItemKind.Item, ShoppingItemKind.Consumable,
            });
        });
    }

    [Fact]
    public void Resolve_raw_returns_catalog_symbol_and_name()
    {
        var (key, name) = ShoppingListService.Resolve(ShoppingItemKind.Raw, "Iron");

        Assert.Equal("iron", key);
        Assert.Equal("Iron", name);
    }

    [Fact]
    public void Resolve_ignores_case_and_spaces()
    {
        var iron = EngineeringCatalog.Default.Materials.First(m => m.Symbol == "iron");
        var (key, _) = ShoppingListService.Resolve(ShoppingItemKind.Raw, "  IRON ");
        Assert.Equal(iron.Symbol, key);
    }

    [Fact]
    public void Resolve_matches_on_foot_catalog_by_name()
    {
        var mat = OdysseyCatalog.Default.Materials.First(m => m.Category.Equals("Component", System.StringComparison.OrdinalIgnoreCase));

        var (key, name) = ShoppingListService.Resolve(ShoppingItemKind.Component, mat.Name.ToUpperInvariant());

        Assert.Equal(mat.Symbol, key);
        Assert.Equal(mat.Name, name);
    }

    [Fact]
    public void Resolve_unknown_returns_free_text_without_throwing()
    {
        var (key, name) = ShoppingListService.Resolve(ShoppingItemKind.Component, "Nonexistent Thing");

        Assert.Equal("nonexistent thing", key);
        Assert.Equal("Nonexistent Thing", name);
    }

    [Fact]
    public void Resolve_commodity_canonicalises_and_keeps_text_as_name()
    {
        var (key, name) = ShoppingListService.Resolve(ShoppingItemKind.Commodity, "CMM Composite");

        Assert.Equal(CommodityName.Canonicalize("CMM Composite"), key);
        Assert.Equal("CMM Composite", name);
    }

    [Fact]
    public void Build_hint_is_catalog_source_for_unsatisfied_non_commodity_only()
    {
        var iron = EngineeringCatalog.Default.Materials.First(m => m.Symbol == "iron");
        var state = new CommanderState();
        state.Materials.Raw["iron"] = 1;
        state.Materials.Raw["carbon"] = 3;

        var rows = ShoppingListService.Build(
        [
            new ShoppingEntry(ShoppingItemKind.Raw, "iron", "Iron", 5),
            new ShoppingEntry(ShoppingItemKind.Raw, "carbon", "Carbon", 1),
            new ShoppingEntry(ShoppingItemKind.Commodity, "steel", "Steel", 2),
        ], state);

        Assert.Equal(iron.Source, rows.Single(r => r.Key == "iron").Hint);
        Assert.Equal(string.Empty, rows.Single(r => r.Key == "carbon").Hint);
        Assert.Equal(string.Empty, rows.Single(r => r.Kind == ShoppingItemKind.Commodity).Hint);
    }
}
