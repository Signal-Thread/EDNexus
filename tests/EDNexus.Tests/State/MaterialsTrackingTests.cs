using EDNexus.Core.Journal;
using EDNexus.Core.Materials;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.State;

/// <summary>
/// The ship materials inventory between full <c>Materials</c> snapshots. The game writes that snapshot
/// only at launch and relog; in between, the inventory has to follow the events that change it. Before,
/// only pickups were followed, so trading, engineering and synthesis left every later count wrong.
/// </summary>
public class MaterialsTrackingTests
{
    private static (JournalEventBus Bus, CommanderState State) Tracked()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        return (bus, state);
    }

    private static void Publish(JournalEventBus bus, string json)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry), json);
        bus.Publish(entry);
    }

    private static string Snapshot(string raw = "", string manufactured = "", string encoded = "")
        => $$"""{ "timestamp":"2026-10-21T23:47:18Z", "event":"Materials", "Raw":[{{raw}}], "Manufactured":[{{manufactured}}], "Encoded":[{{encoded}}] }""";

    private static string Item(string name, int count) => $$"""{ "Name":"{{name}}", "Count":{{count}} }""";

    private static int Held(CommanderState s, string category, string name)
        => MaterialInventory.CountsFor(s, category).GetValueOrDefault(name);

    // --- the case that was reported: a material trader ------------------------------------------------

    [Fact]
    public void A_material_trade_takes_what_was_paid_and_adds_what_was_received()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(manufactured: Item("imperialshielding", 74) + "," + Item("chemicalmanipulators", 0)));

        // Exactly as the game wrote it.
        Publish(bus, """
            { "timestamp":"2026-10-21T23:50:04Z", "event":"MaterialTrade", "MarketID":3230665216, "TraderType":"manufactured",
              "Paid":{ "Material":"imperialshielding", "Material_Localised":"Imperial Shielding", "Category":"Manufactured", "Quantity":74 },
              "Received":{ "Material":"chemicalmanipulators", "Material_Localised":"Chemical Manipulators", "Category":"Manufactured", "Quantity":37 } }
            """);

        Assert.Equal(0, Held(state, "Manufactured", "imperialshielding"));
        Assert.Equal(37, Held(state, "Manufactured", "chemicalmanipulators"));
    }

    [Fact]
    public void A_trade_for_a_material_not_held_yet_creates_it()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("iron", 100)));

        Publish(bus, """
            { "timestamp":"2026-10-21T23:50:04Z", "event":"MaterialTrade", "TraderType":"raw",
              "Paid":{ "Material":"iron", "Category":"Raw", "Quantity":30 },
              "Received":{ "Material":"vanadium", "Category":"Raw", "Quantity":6 } }
            """);

        Assert.Equal(70, Held(state, "Raw", "iron"));
        Assert.Equal(6, Held(state, "Raw", "vanadium"));
    }

    [Fact]
    public void A_trade_raises_one_materials_changed()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("iron", 100)));
        var raised = 0;
        state.MaterialsChanged += () => raised++;

        Publish(bus, """
            { "timestamp":"2026-10-21T23:50:04Z", "event":"MaterialTrade",
              "Paid":{ "Material":"iron", "Category":"Raw", "Quantity":30 },
              "Received":{ "Material":"vanadium", "Category":"Raw", "Quantity":6 } }
            """);

        Assert.Equal(1, raised);   // the card refreshes once, not once per half of the trade
    }

    // --- engineering ----------------------------------------------------------------------------------

    [Fact]
    public void An_engineer_craft_spends_its_ingredients_which_carry_no_category()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(
            raw: Item("sulphur", 295) + "," + Item("iron", 181),
            manufactured: Item("heatconductionwiring", 9)));

        Publish(bus, """
            { "timestamp":"2026-10-22T00:41:01Z", "event":"EngineerCraft", "Slot":"PowerPlant", "Module":"int_powerplant_size5_class5",
              "Ingredients":[ { "Name":"sulphur", "Count":1 }, { "Name":"heatconductionwiring", "Count":2 } ],
              "Engineer":"Hera Tani", "EngineerID":300090, "BlueprintName":"PowerPlant_Boosted", "Level":1, "Quality":0.3333 }
            """);

        Assert.Equal(294, Held(state, "Raw", "sulphur"));          // found in Raw
        Assert.Equal(7, Held(state, "Manufactured", "heatconductionwiring"));   // and in Manufactured
        Assert.Equal(181, Held(state, "Raw", "iron"));             // untouched
    }

    [Fact]
    public void A_craft_with_several_ingredients_raises_one_change()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("sulphur", 10) + "," + Item("iron", 10) + "," + Item("carbon", 10)));
        var raised = 0;
        state.MaterialsChanged += () => raised++;

        Publish(bus, """{ "timestamp":"2026-10-22T00:41:01Z", "event":"EngineerCraft", "Ingredients":[ { "Name":"sulphur", "Count":1 }, { "Name":"iron", "Count":1 }, { "Name":"carbon", "Count":1 } ] }""");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Material_names_match_whatever_the_case()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("Sulphur", 10)));

        Publish(bus, """{ "timestamp":"2026-10-22T00:41:01Z", "event":"EngineerCraft", "Ingredients":[ { "Name":"SULPHUR", "Count":4 } ] }""");

        Assert.Equal(6, Held(state, "Raw", "sulphur"));
    }

    // --- the other ways materials are spent ----------------------------------------------------------

    [Fact]
    public void Discarding_synthesis_and_research_each_spend_what_they_name()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(
            raw: Item("iron", 50) + "," + Item("nickel", 50) + "," + Item("zinc", 50),
            encoded: Item("shielddensityreports", 20)));

        Publish(bus, """{ "timestamp":"2026-10-22T00:50:00Z", "event":"MaterialDiscarded", "Category":"Raw", "Name":"iron", "Count":5 }""");
        Publish(bus, """{ "timestamp":"2026-10-22T00:51:00Z", "event":"Synthesis", "Name":"Repair Basic", "Materials":[ { "Name":"nickel", "Count":2 }, { "Name":"zinc", "Count":1 } ] }""");
        Publish(bus, """{ "timestamp":"2026-10-22T00:52:00Z", "event":"ScientificResearch", "Name":"shielddensityreports", "Category":"Encoded", "Count":3 }""");

        Assert.Equal(45, Held(state, "Raw", "iron"));
        Assert.Equal(48, Held(state, "Raw", "nickel"));
        Assert.Equal(49, Held(state, "Raw", "zinc"));
        Assert.Equal(17, Held(state, "Encoded", "shielddensityreports"));
    }

    [Fact]
    public void A_technology_broker_unlock_spends_its_materials_but_not_its_commodities()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(manufactured: Item("chemicalmanipulators", 10)));
        state.Cargo["Gold"] = 4;

        Publish(bus, """
            { "timestamp":"2026-10-22T01:00:00Z", "event":"TechnologyBroker", "BrokerType":"guardian", "MarketID":1, "ItemsUnlocked":[],
              "Commodities":[ { "Name":"gold", "Count":4 } ],
              "Materials":[ { "Name":"chemicalmanipulators", "Count":3, "Category":"Manufactured" } ] }
            """);

        Assert.Equal(7, Held(state, "Manufactured", "chemicalmanipulators"));
        Assert.Equal(4, state.Cargo["Gold"]);   // cargo is the cargo card's business
    }

    [Fact]
    public void An_engineer_contribution_spends_materials_only()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("iron", 40)));

        Publish(bus, """{ "timestamp":"2026-10-22T01:10:00Z", "event":"EngineerContribution", "Engineer":"Hera Tani", "Type":"Commodity", "Commodity":"kamitracigars", "Quantity":50, "TotalQuantity":50 }""");
        Assert.Equal(40, Held(state, "Raw", "iron"));   // a commodity contribution changes no material

        Publish(bus, """{ "timestamp":"2026-10-22T01:11:00Z", "event":"EngineerContribution", "Engineer":"Hera Tani", "Type":"Material", "Material":"iron", "Category":"Raw", "Quantity":15, "TotalQuantity":15 }""");
        Assert.Equal(25, Held(state, "Raw", "iron"));
    }

    // --- the cap: the game logs a pickup but only holds so much --------------------------------------

    [Fact]
    public void A_pickup_beyond_the_cap_is_not_counted()
    {
        var (bus, state) = Tracked();
        // Imperial Shielding is grade 5: the ship holds 100.
        Publish(bus, Snapshot(manufactured: Item("imperialshielding", 98)));

        Publish(bus, """{ "timestamp":"2026-10-21T23:58:25Z", "event":"MaterialCollected", "Category":"Manufactured", "Name":"imperialshielding", "Count":4 }""");

        Assert.Equal(100, Held(state, "Manufactured", "imperialshielding"));
    }

    [Fact]
    public void A_capped_pickup_does_not_make_a_later_spend_come_out_wrong()
    {
        // The real case from a journal: 102 counted, 100 held. Trading 26 away left the card on 76 while the
        // game had 74, and every count after that was off by two.
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(manufactured: Item("imperialshielding", 98)));
        Publish(bus, """{ "timestamp":"2026-10-21T23:58:25Z", "event":"MaterialCollected", "Category":"Manufactured", "Name":"imperialshielding", "Count":4 }""");

        Publish(bus, """
            { "timestamp":"2026-10-22T00:09:17Z", "event":"MaterialTrade", "TraderType":"manufactured",
              "Paid":{ "Material":"imperialshielding", "Category":"Manufactured", "Quantity":26 },
              "Received":{ "Material":"chemicalmanipulators", "Category":"Manufactured", "Quantity":13 } }
            """);

        Assert.Equal(74, Held(state, "Manufactured", "imperialshielding"));
    }

    [Fact]
    public void Items_received_in_a_trade_are_capped_too()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("iron", 100), manufactured: Item("imperialshielding", 90)));

        Publish(bus, """
            { "timestamp":"2026-10-22T00:09:17Z", "event":"MaterialTrade", "TraderType":"manufactured",
              "Paid":{ "Material":"iron", "Category":"Raw", "Quantity":10 },
              "Received":{ "Material":"imperialshielding", "Category":"Manufactured", "Quantity":40 } }
            """);

        Assert.Equal(100, Held(state, "Manufactured", "imperialshielding"));
    }

    [Fact]
    public void A_material_the_catalogue_does_not_know_is_never_capped_or_lowered()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("some_future_material", 400)));

        Publish(bus, """{ "timestamp":"2026-10-22T00:00:00Z", "event":"MaterialCollected", "Category":"Raw", "Name":"some_future_material", "Count":50 }""");

        Assert.Equal(450, Held(state, "Raw", "some_future_material"));
    }

    // --- defensive parsing ---------------------------------------------------------------------------

    [Fact]
    public void Spending_never_goes_below_zero_and_never_invents_stock()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("iron", 2)));

        Publish(bus, """{ "timestamp":"2026-10-22T00:41:01Z", "event":"EngineerCraft", "Ingredients":[ { "Name":"iron", "Count":9 }, { "Name":"neverheld", "Count":3 } ] }""");

        Assert.Equal(0, Held(state, "Raw", "iron"));
        Assert.False(MaterialInventory.CountsFor(state, "Raw").ContainsKey("neverheld"));
    }

    [Fact]
    public void Malformed_material_events_never_throw_and_change_nothing()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("iron", 10)));

        foreach (var json in new[]
        {
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"MaterialTrade" }""",
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"MaterialTrade", "Paid":5, "Received":"x" }""",
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"MaterialTrade", "Paid":{ "Material":"iron" }, "Received":{ "Material":"carbon" } }""",
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"EngineerCraft" }""",
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"EngineerCraft", "Ingredients":"lots" }""",
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"EngineerCraft", "Ingredients":[ null, 5, { "Count":2 }, { "Name":7, "Count":"x" } ] }""",
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"MaterialDiscarded", "Name":"iron", "Count":-4 }""",
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"Synthesis", "Materials":null }""",
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"TechnologyBroker" }""",
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"EngineerContribution", "Type":"Material" }""",
            """{ "timestamp":"2026-10-22T00:00:00Z", "event":"ScientificResearch" }""",
        })
        {
            Publish(bus, json);   // the bus isolates handler errors, but a handler must not rely on that
        }

        Assert.Equal(10, Held(state, "Raw", "iron"));
        Assert.Equal(0, Held(state, "Raw", "carbon"));
    }

    [Fact]
    public void Nothing_to_change_raises_nothing()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("iron", 10)));
        var raised = 0;
        state.MaterialsChanged += () => raised++;

        Publish(bus, """{ "timestamp":"2026-10-22T00:41:01Z", "event":"EngineerCraft", "Ingredients":[ { "Name":"neverheld", "Count":3 } ] }""");

        Assert.Equal(0, raised);
    }

    // --- the property that ties it together ----------------------------------------------------------

    [Fact]
    public void A_snapshot_plus_the_events_after_it_equals_the_next_snapshot()
    {
        // The game writes a full Materials snapshot at every launch and nothing changes while it is
        // closed, so the tracked inventory at the end of a session has to equal the next snapshot. This is
        // the same check that was run against a real commander's journals (15 consecutive snapshots,
        // 179 items out before, none after), reduced to one session with a bit of everything.
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(
            raw: Item("iron", 181) + "," + Item("sulphur", 295) + "," + Item("carbon", 278),
            manufactured: Item("imperialshielding", 98) + "," + Item("fsdtelemetry", 26) + "," + Item("chemicalmanipulators", 0),
            encoded: Item("hyperspacetrajectories", 16)));

        Publish(bus, """{ "timestamp":"2026-10-21T23:58:25Z", "event":"MaterialCollected", "Category":"Manufactured", "Name":"imperialshielding", "Count":4 }""");   // capped at 100
        Publish(bus, """{ "timestamp":"2026-10-21T23:59:00Z", "event":"MaterialCollected", "Category":"Raw", "Name":"carbon", "Count":30 }""");                        // capped at 300
        Publish(bus, """{ "timestamp":"2026-10-22T00:09:17Z", "event":"MaterialTrade", "Paid":{ "Material":"imperialshielding", "Category":"Manufactured", "Quantity":26 }, "Received":{ "Material":"chemicalmanipulators", "Category":"Manufactured", "Quantity":13 } }""");
        Publish(bus, """{ "timestamp":"2026-10-22T00:41:01Z", "event":"EngineerCraft", "Ingredients":[ { "Name":"sulphur", "Count":1 }, { "Name":"fsdtelemetry", "Count":12 } ] }""");
        Publish(bus, """{ "timestamp":"2026-10-22T00:42:00Z", "event":"EngineerCraft", "Ingredients":[ { "Name":"hyperspacetrajectories", "Count":3 } ] }""");

        // What the game would write at the next launch.
        var expected = new Dictionary<string, int>
        {
            ["iron"] = 181, ["sulphur"] = 294, ["carbon"] = 300,
            ["imperialshielding"] = 74, ["fsdtelemetry"] = 14, ["chemicalmanipulators"] = 13,
            ["hyperspacetrajectories"] = 13,
        };

        foreach (var (name, count) in expected)
        {
            var category = MaterialInventory.Categories.First(c => MaterialInventory.CountsFor(state, c).ContainsKey(name));
            Assert.True(count == Held(state, category, name), $"{name}: expected {count}, tracked {Held(state, category, name)}");
        }
    }

    [Fact]
    public void A_new_snapshot_replaces_whatever_the_events_had_made_of_the_inventory()
    {
        var (bus, state) = Tracked();
        Publish(bus, Snapshot(raw: Item("iron", 50)));
        Publish(bus, """{ "timestamp":"2026-10-22T00:41:01Z", "event":"EngineerCraft", "Ingredients":[ { "Name":"iron", "Count":5 } ] }""");

        Publish(bus, Snapshot(raw: Item("iron", 60)));

        Assert.Equal(60, Held(state, "Raw", "iron"));
    }
}
