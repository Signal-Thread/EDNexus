using System.Linq;
using EDNexus.Core.Colonisation;
using EDNexus.Core.Journal;
using Xunit;

namespace EDNexus.Tests.Colonisation;

/// <summary>
/// The one definition of "this event moved cargo on a carrier", shared by the local tally and the
/// Raven Colonial sharing so the two can never disagree about what a movement is.
/// </summary>
public class CarrierCargoMovementsTests
{
    private static CarrierCargoMovement? Read(string json, long? docked = 3700005632)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry), "sample JSON failed to parse");
        return CarrierCargoMovements.Read(entry, docked);
    }

    [Fact]
    public void Transfers_to_the_carrier_add_and_transfers_to_the_ship_remove()
    {
        var m = Read("""
        { "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer",
          "Transfers":[ { "Type":"steel", "Count":841, "Direction":"tocarrier" },
                        { "Type":"titanium", "Count":32, "Direction":"toship" } ] }
        """);

        Assert.NotNull(m);
        Assert.False(m!.IsMarketTrade);
        Assert.Equal(new[] { ("steel", 841), ("titanium", -32) }, m.Deltas.Select(d => (d.Commodity, d.Units)));
    }

    [Fact]
    public void A_transfer_belongs_to_the_carrier_the_commander_is_docked_at()
    {
        // CargoTransfer does not name the carrier; the docked market id is the only clue.
        var m = Read("""{ "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer", "Transfers":[ { "Type":"steel", "Count":1, "Direction":"tocarrier" } ] }""", docked: 42);

        Assert.Equal(42, m!.MarketId);
    }

    [Fact]
    public void A_transfer_with_no_known_dock_has_no_market_id()
    {
        var m = Read("""{ "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer", "Transfers":[ { "Type":"steel", "Count":1, "Direction":"tocarrier" } ] }""", docked: null);

        Assert.Null(m!.MarketId);
    }

    [Fact]
    public void Selling_to_a_carriers_market_adds_to_it_and_carries_the_market_id_from_the_event()
    {
        var m = Read("""
        { "timestamp":"2026-10-29T00:04:17Z", "event":"MarketSell", "MarketID":3711552256,
          "Type":"superconductors", "Count":1276, "SellPrice":6941, "TotalSale":8856716, "AvgPricePaid":5581 }
        """, docked: null);

        Assert.True(m!.IsMarketTrade);
        Assert.Equal(3711552256, m.MarketId);
        var d = Assert.Single(m.Deltas);
        Assert.Equal(("superconductors", 1276), (d.Commodity, d.Units));
    }

    [Fact]
    public void Buying_from_a_carriers_market_removes_from_it()
    {
        var m = Read("""
        { "timestamp":"2026-10-29T00:04:17Z", "event":"MarketBuy", "MarketID":3711552256,
          "Type":"steel", "Count":300, "BuyPrice":4000, "TotalCost":1200000 }
        """);

        var d = Assert.Single(m!.Deltas);
        Assert.Equal(("steel", -300), (d.Commodity, d.Units));
    }

    [Fact]
    public void A_market_trade_without_a_market_id_is_not_a_movement()
    {
        // Without the id there is no way to tell a carrier's market from any other station's.
        Assert.Null(Read("""{ "timestamp":"2026-10-29T00:04:17Z", "event":"MarketSell", "Type":"steel", "Count":5 }"""));
    }

    [Theory]
    [InlineData("""{ "timestamp":"2026-11-02T04:05:52Z", "event":"Docked", "MarketID":1 }""")]
    [InlineData("""{ "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer" }""")]
    [InlineData("""{ "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer", "Transfers":"nope" }""")]
    [InlineData("""{ "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer", "Transfers":[ { "Type":"steel", "Count":5, "Direction":"tosrv" } ] }""")]
    [InlineData("""{ "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer", "Transfers":[ { "Type":"steel", "Count":0, "Direction":"tocarrier" } ] }""")]
    [InlineData("""{ "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer", "Transfers":[ { "Type":"steel", "Count":-5, "Direction":"tocarrier" } ] }""")]
    [InlineData("""{ "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer", "Transfers":[ { "Type":"", "Count":5, "Direction":"tocarrier" } ] }""")]
    [InlineData("""{ "timestamp":"2026-11-02T04:05:52Z", "event":"MarketSell", "MarketID":7, "Type":"steel", "Count":0 }""")]
    public void Anything_that_is_not_a_positive_movement_is_ignored(string json)
        => Assert.Null(Read(json));

    [Fact]
    public void Bad_entries_are_skipped_without_losing_good_ones()
    {
        var m = Read("""
        { "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer",
          "Transfers":[ null, 7, { "Type":"steel", "Count":10, "Direction":"tocarrier" } ] }
        """);

        var d = Assert.Single(m!.Deltas);
        Assert.Equal(("steel", 10), (d.Commodity, d.Units));
    }
}
