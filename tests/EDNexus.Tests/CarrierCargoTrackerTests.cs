using System.Collections.Generic;
using EDNexus.Core.Colonisation;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests;

public class CarrierCargoTrackerTests
{
    private static void Publish(JournalEventBus bus, string json)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry), "sample JSON failed to parse");
        bus.Publish(entry);
    }

    private const string Depot = """
    { "timestamp":"2026-06-03T00:47:02Z", "event":"ColonisationConstructionDepot", "MarketID":3956023042,
      "ConstructionProgress":0.5, "ConstructionComplete":false, "ConstructionFailed":false,
      "ResourcesRequired":[
        { "Name":"$steel_name;", "Name_Localised":"Steel", "RequiredAmount":1500, "ProvidedAmount":0, "Payment":5057 },
        { "Name":"$cmmcomposite_name;", "Name_Localised":"CMM Composite", "RequiredAmount":500, "ProvidedAmount":0, "Payment":6788 }
      ] }
    """;

    private static string Transfer(string type, int count, string direction) => $$"""
    { "timestamp":"2026-06-03T01:00:00Z", "event":"CargoTransfer",
      "Transfers":[ { "Type":"{{type}}", "Count":{{count}}, "Direction":"{{direction}}" } ] }
    """;

    [Fact]
    public void Transfers_to_and_from_the_carrier_adjust_its_stock()
    {
        var bus = new JournalEventBus();
        var carrier = new CarrierCargoTracker(bus);

        Publish(bus, Transfer("steel", 1276, "tocarrier"));
        Publish(bus, Transfer("steel", 276, "toship"));

        Assert.Equal(1000, carrier.Stock["steel"]);
    }

    [Fact]
    public void Stock_never_goes_negative_when_history_is_incomplete()
    {
        var bus = new JournalEventBus();
        var carrier = new CarrierCargoTracker(bus);

        // Only part of the carrier's history was replayed: a withdrawal with no recorded deposit.
        Publish(bus, Transfer("steel", 50, "toship"));

        Assert.False(carrier.Stock.TryGetValue("steel", out var tons) && tons != 0);
    }

    [Fact]
    public void Malformed_transfer_entries_are_skipped_without_losing_later_ones()
    {
        var bus = new JournalEventBus();
        var carrier = new CarrierCargoTracker(bus);

        Publish(bus, """
        { "timestamp":"2026-06-03T01:00:00Z", "event":"CargoTransfer",
          "Transfers":[ null, 7, { "Type":"steel", "Count":10, "Direction":"tocarrier" } ] }
        """);

        Assert.Equal(10, carrier.Stock["steel"]);
    }

    [Fact]
    public void Shopping_list_counts_carrier_stock_toward_what_is_still_needed()
    {
        var bus = new JournalEventBus();
        var colonisation = new ColonisationTracker(bus);
        var carrier = new CarrierCargoTracker(bus);

        Publish(bus, Depot);
        Publish(bus, Transfer("steel", 1276, "tocarrier"));

        var list = colonisation.ActiveSite!.BuildShoppingList(new Dictionary<string, int>(), carrier.Stock);

        var steel = Assert.Single(list, i => i.Name == "Steel");
        Assert.Equal(1276, steel.OnCarrier);
        Assert.Equal(224, steel.StillNeeded);          // 1500 required - 1276 on the carrier

        var cmm = Assert.Single(list, i => i.Name == "CMM Composite");
        Assert.Equal(0, cmm.OnCarrier);
        Assert.Equal(500, cmm.StillNeeded);
    }

    [Fact]
    public void Hold_and_carrier_stack_but_never_exceed_the_shortfall()
    {
        var bus = new JournalEventBus();
        var colonisation = new ColonisationTracker(bus);
        var carrier = new CarrierCargoTracker(bus);

        Publish(bus, Depot);
        Publish(bus, Transfer("steel", 1400, "tocarrier"));

        var hold = new Dictionary<string, int> { ["steel"] = 400 };
        var steel = Assert.Single(colonisation.ActiveSite!.BuildShoppingList(hold, carrier.Stock), i => i.Name == "Steel");

        Assert.Equal(0, steel.StillNeeded);
        Assert.True(steel.CoveredByStock);
    }

    // --- Trades at the carrier's own market (the journal has 100+ of these per active carrier owner). ---

    private const long MyCarrier = 3711552256;

    private static (JournalEventBus bus, CommanderState state, CarrierCargoTracker carrier) NewWithState()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        var carrier = new CarrierCargoTracker(bus, state);
        Publish(bus, $$"""{ "timestamp":"2026-11-02T04:00:00Z", "event":"CarrierStats", "CarrierID":{{MyCarrier}}, "CarrierType":"FleetCarrier", "Callsign":"X8J-LVZ" }""");
        return (bus, state, carrier);
    }

    private static string Sell(long marketId, string type, int count) => $$"""
    { "timestamp":"2026-11-02T04:10:00Z", "event":"MarketSell", "MarketID":{{marketId}}, "Type":"{{type}}", "Count":{{count}}, "SellPrice":1, "TotalSale":1, "AvgPricePaid":1 }
    """;

    private static string Buy(long marketId, string type, int count) => $$"""
    { "timestamp":"2026-11-02T04:10:00Z", "event":"MarketBuy", "MarketID":{{marketId}}, "Type":"{{type}}", "Count":{{count}}, "BuyPrice":1, "TotalCost":1 }
    """;

    [Fact]
    public void Selling_to_and_buying_from_your_own_carriers_market_moves_its_stock()
    {
        var (bus, _, carrier) = NewWithState();

        Publish(bus, Sell(MyCarrier, "superconductors", 1276));
        Publish(bus, Buy(MyCarrier, "superconductors", 276));

        Assert.Equal(1000, carrier.Stock["superconductors"]);
    }

    [Fact]
    public void Trades_at_other_stations_do_not_touch_the_carrier_stock()
    {
        var (bus, _, carrier) = NewWithState();

        Publish(bus, Sell(128666762, "superconductors", 500));   // some other market

        Assert.Empty(carrier.Stock);
    }

    [Fact]
    public void Market_trades_are_ignored_until_the_own_carrier_is_known()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        var carrier = new CarrierCargoTracker(bus, state);

        Publish(bus, Sell(MyCarrier, "superconductors", 500));   // no CarrierStats yet: cannot tell it from any station

        Assert.Empty(carrier.Stock);
    }

    [Fact]
    public void A_transfer_at_a_different_carrier_is_not_my_carriers_cargo()
    {
        var (bus, _, carrier) = NewWithState();
        Publish(bus, """{ "timestamp":"2026-11-02T04:05:00Z", "event":"Docked", "StationName":"SQD-1AA", "StationType":"FleetCarrier", "MarketID":3700009999 }""");

        Publish(bus, Transfer("steel", 100, "tocarrier"));

        Assert.Empty(carrier.Stock);
    }

    [Fact]
    public void A_transfer_at_my_own_carrier_counts()
    {
        var (bus, _, carrier) = NewWithState();
        Publish(bus, $$"""{ "timestamp":"2026-11-02T04:05:00Z", "event":"Docked", "StationName":"X8J-LVZ", "StationType":"FleetCarrier", "MarketID":{{MyCarrier}} }""");

        Publish(bus, Transfer("steel", 100, "tocarrier"));

        Assert.Equal(100, carrier.Stock["steel"]);
    }

    [Fact]
    public void Transfers_still_count_when_the_dock_or_carrier_is_unknown()
    {
        // The existing behaviour, kept for a journal replayed without its CarrierStats or Docked.
        var bus = new JournalEventBus();
        var carrier = new CarrierCargoTracker(bus, new CommanderState());

        Publish(bus, Transfer("steel", 100, "tocarrier"));

        Assert.Equal(100, carrier.Stock["steel"]);
    }

    [Fact]
    public void The_shopping_list_sees_a_commodity_sold_to_the_carrier_market()
    {
        var (bus, _, carrier) = NewWithState();
        var colonisation = new ColonisationTracker(bus);
        Publish(bus, Depot);

        Publish(bus, Sell(MyCarrier, "steel", 1276));

        var steel = Assert.Single(colonisation.ActiveSite!.BuildShoppingList(new Dictionary<string, int>(), carrier.Stock), i => i.Name == "Steel");
        Assert.Equal(1276, steel.OnCarrier);
    }
}
