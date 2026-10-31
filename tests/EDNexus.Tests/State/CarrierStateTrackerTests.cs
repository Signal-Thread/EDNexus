using EDNexus.Core.Journal;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.State;

public class CarrierStateTrackerTests
{
    private static (JournalEventBus bus, CommanderState state) NewTracker()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        return (bus, state);
    }

    private static void Publish(JournalEventBus bus, string json)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry), "sample JSON failed to parse");
        bus.Publish(entry);
    }

    [Fact]
    public void CarrierStats_updates_tritium_and_jump_range()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-12T02:55:06Z", "event":"CarrierStats", "FuelLevel":638, "JumpRangeCurr":500.0 }""");

        Assert.Equal(638, state.CarrierFuel);
        Assert.Equal(500.0, state.CarrierJumpRange);
    }

    [Fact]
    public void CarrierStats_derives_used_capacity_from_space_usage()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """
        { "timestamp":"2026-07-12T02:55:06Z", "event":"CarrierStats", "FuelLevel":638, "JumpRangeCurr":500.0,
          "SpaceUsage": { "TotalCapacity":25000, "Crew":1200, "Cargo":3500, "CargoSpaceReserved":0,
                          "ShipPacks":0, "ModulePacks":0, "FreeSpace":20300 } }
        """);

        Assert.Equal(4700, state.CarrierUsedCapacity);
    }

    [Fact]
    public void CarrierJumpRequest_records_pending_destination_and_departure()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """
        { "timestamp":"2026-07-12T02:55:01Z", "event":"CarrierJumpRequest",
          "SystemName":"Ga Gu", "Body":"Ga Gu 2 a", "DepartureTime":"2026-07-12T03:11:10Z" }
        """);

        Assert.Equal("Ga Gu", state.CarrierPendingSystem);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 3, 11, 10, TimeSpan.Zero), state.CarrierPendingDeparture);
    }

    [Fact]
    public void Completing_the_carrier_jump_clears_the_pending_request()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-12T02:55:01Z", "event":"CarrierJumpRequest", "SystemName":"Ga Gu", "DepartureTime":"2026-07-12T03:11:10Z" }""");
        Assert.Equal("Ga Gu", state.CarrierPendingSystem);

        Publish(bus, """{ "timestamp":"2026-07-12T03:11:10Z", "event":"CarrierJump", "StarSystem":"Ga Gu", "Body":"Ga Gu 2 a" }""");

        Assert.Equal("Ga Gu", state.StarSystem);
        Assert.Null(state.CarrierPendingSystem);
        Assert.Null(state.CarrierPendingDeparture);
    }

    [Fact]
    public void Cancelling_the_carrier_jump_clears_the_pending_request()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-12T02:55:01Z", "event":"CarrierJumpRequest", "SystemName":"Ga Gu", "DepartureTime":"2026-07-12T03:11:10Z" }""");
        Publish(bus, """{ "timestamp":"2026-07-12T02:57:00Z", "event":"CarrierJumpCancelled" }""");

        Assert.Null(state.CarrierPendingSystem);
        Assert.Null(state.CarrierPendingDeparture);
    }

    [Fact]
    public void A_ship_FSDJump_does_not_clear_a_pending_carrier_jump()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-12T02:55:01Z", "event":"CarrierJumpRequest", "SystemName":"Ga Gu", "DepartureTime":"2026-07-12T03:11:10Z" }""");
        Publish(bus, """{ "timestamp":"2026-07-12T02:58:00Z", "event":"FSDJump", "StarSystem":"Merope" }""");

        Assert.Equal("Merope", state.StarSystem);
        Assert.Equal("Ga Gu", state.CarrierPendingSystem);   // the carrier is still booked to move
    }

    [Fact]
    public void A_carrier_jump_taken_while_docked_on_the_carrier_stays_docked()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-10-31T01:31:45Z", "event":"Docked", "StationName":"X8J-LVZ", "StationType":"FleetCarrier", "StarSystem":"HD 232819" }""");
        Publish(bus, """
        { "timestamp":"2026-10-31T01:50:13Z", "event":"CarrierJump", "Docked":true, "StationName":"X8J-LVZ",
          "StationType":"FleetCarrier", "StarSystem":"Ga Gu", "Body":"Ga Gu 2 a" }
        """);

        Assert.Equal("Ga Gu", state.StarSystem);
        Assert.True(state.Docked);
        Assert.Equal("X8J-LVZ", state.StationName);
        Assert.Equal("FleetCarrier", state.StationType);
    }

    [Fact]
    public void A_ship_FSDJump_still_clears_the_docked_state()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-10-31T01:31:45Z", "event":"Docked", "StationName":"X8J-LVZ", "StationType":"FleetCarrier" }""");
        Publish(bus, """{ "timestamp":"2026-10-31T01:58:00Z", "event":"FSDJump", "StarSystem":"Merope" }""");

        Assert.False(state.Docked);
        Assert.Null(state.StationName);
    }

    // --- Carrier name vs. callsign (#90). ---

    private const string OwnCarrierStats = """
    { "timestamp":"2026-07-12T02:55:06Z", "event":"CarrierStats", "CarrierID":3700005632,
      "Callsign":"K7Q-B3L", "Name":"Nomad's Reach", "FuelLevel":638, "JumpRangeCurr":500.0 }
    """;

    private const string DockedAtOwnCarrier = """
    { "timestamp":"2026-07-12T03:20:00Z", "event":"Docked", "StarSystem":"HIP 23759",
      "StationName":"K7Q-B3L", "StationType":"FleetCarrier", "MarketID":3700005632 }
    """;

    [Fact]
    public void CarrierStats_records_the_carrier_name_and_callsign()
    {
        var (bus, state) = NewTracker();
        Publish(bus, OwnCarrierStats);

        Assert.Equal("Nomad's Reach", state.CarrierName);
        Assert.Equal("K7Q-B3L", state.CarrierCallsign);
    }

    [Fact]
    public void Docking_at_your_own_carrier_displays_its_name_not_its_callsign()
    {
        var (bus, state) = NewTracker();
        Publish(bus, OwnCarrierStats);
        Publish(bus, DockedAtOwnCarrier);

        Assert.Equal("K7Q-B3L", state.StationName);              // raw journal value is preserved
        Assert.Equal("FleetCarrier", state.StationType);
        Assert.Equal("Nomad's Reach", state.StationDisplayName); // what a commander is shown
    }

    [Fact]
    public void Another_commanders_carrier_keeps_its_callsign()
    {
        var (bus, state) = NewTracker();
        Publish(bus, OwnCarrierStats);
        Publish(bus, """
        { "timestamp":"2026-07-12T04:00:00Z", "event":"Docked", "StarSystem":"Deciat",
          "StationName":"V9T-B0X", "StationType":"FleetCarrier", "MarketID":3700009999 }
        """);

        // Only the callsign is guaranteed for someone else's carrier — labelling it with our
        // carrier's name would be worse than showing the callsign.
        Assert.Equal("V9T-B0X", state.StationDisplayName);
    }

    [Fact]
    public void The_callsign_stands_in_until_CarrierStats_supplies_the_name()
    {
        var (bus, state) = NewTracker();
        Publish(bus, DockedAtOwnCarrier);   // docked before any CarrierStats this session

        Assert.Equal("K7Q-B3L", state.StationDisplayName);

        Publish(bus, OwnCarrierStats);
        Assert.Equal("Nomad's Reach", state.StationDisplayName);
    }

    [Fact]
    public void A_normal_station_is_unaffected_by_carrier_naming()
    {
        var (bus, state) = NewTracker();
        Publish(bus, OwnCarrierStats);
        Publish(bus, """
        { "timestamp":"2026-07-12T05:00:00Z", "event":"Docked", "StarSystem":"Diaguandri",
          "StationName":"Ray Gateway", "StationType":"Coriolis", "MarketID":3228024320 }
        """);

        Assert.Equal("Coriolis", state.StationType);
        Assert.Equal("Ray Gateway", state.StationDisplayName);
    }

    [Fact]
    public void Undocking_clears_the_station_but_keeps_the_carrier_identity()
    {
        var (bus, state) = NewTracker();
        Publish(bus, OwnCarrierStats);
        Publish(bus, DockedAtOwnCarrier);
        Publish(bus, """{ "timestamp":"2026-07-12T03:40:00Z", "event":"Undocked", "StationName":"K7Q-B3L" }""");

        Assert.False(state.Docked);
        Assert.Null(state.StationName);
        Assert.Null(state.StationType);
        Assert.Equal("Nomad's Reach", state.CarrierName);   // the carrier still exists
    }
}
