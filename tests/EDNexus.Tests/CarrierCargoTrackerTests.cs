using System.Collections.Generic;
using EDNexus.Core.Colonisation;
using EDNexus.Core.Journal;
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
}
