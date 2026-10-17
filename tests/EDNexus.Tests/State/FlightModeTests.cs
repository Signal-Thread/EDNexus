using System.Text.Json;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.State;

public class FlightModeTests
{
    private static long Bit(int n) => 1L << n;

    // --- the flag decoder ---------------------------------------------------------------------------

    [Fact]
    public void A_real_docked_Status_decodes_as_docked()
    {
        // Flags from a live Status.json while docked at a Coriolis, gear down, shields up, mass locked.
        const long flags = 151060493;

        Assert.Equal(FlightMode.Docked, ShipStatusFlags.Derive(flags, 0));
    }

    [Theory]
    [InlineData(1 << 1 | 1 << 24, FlightMode.Landed)]
    [InlineData(1 << 4 | 1 << 24, FlightMode.Supercruise)]
    [InlineData(1 << 30 | 1 << 24, FlightMode.Hyperspace)]
    [InlineData(1 << 24, FlightMode.InSpace)]
    [InlineData(1 << 25, FlightMode.InSpace)]                  // in a fighter
    [InlineData(1 << 21 | 1 << 24, FlightMode.NearSurface)]    // lat/long: close to a planet
    [InlineData(1 << 26 | 1 << 21, FlightMode.Srv)]
    public void Each_mode_is_read_from_its_bits(long flags, FlightMode expected)
        => Assert.Equal(expected, ShipStatusFlags.Derive(flags, 0));

    [Fact]
    public void On_foot_wins_over_everything_including_a_docked_ship()
    {
        // The ship's Docked bit stays set while the commander walks around the station.
        Assert.Equal(FlightMode.OnFoot, ShipStatusFlags.Derive(Bit(0) | Bit(24), flags2: Bit(0)));
    }

    [Fact]
    public void The_more_specific_state_wins_when_several_bits_are_set()
    {
        Assert.Equal(FlightMode.Srv, ShipStatusFlags.Derive(Bit(26) | Bit(0), 0));
        Assert.Equal(FlightMode.Docked, ShipStatusFlags.Derive(Bit(0) | Bit(1) | Bit(24), 0));
        Assert.Equal(FlightMode.Landed, ShipStatusFlags.Derive(Bit(1) | Bit(4) | Bit(24), 0));
        Assert.Equal(FlightMode.Hyperspace, ShipStatusFlags.Derive(Bit(30) | Bit(4) | Bit(24), 0));
    }

    [Theory]
    [InlineData(0L)]                 // main menu, loading
    [InlineData(1L << 3)]            // shields only: not a ship, SRV or suit
    [InlineData(1L << 8 | 1L << 9)]  // lights and scoop, still no ship bit
    public void Flags_that_describe_no_ship_give_no_mode(long flags)
        => Assert.Null(ShipStatusFlags.Derive(flags, 0));

    // --- the tracker --------------------------------------------------------------------------------

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

    private static string Status(long flags, long flags2 = 0)
        => $$"""{"timestamp":"2026-10-17T01:20:00Z","event":"Status","Flags":{{flags}},"Flags2":{{flags2}}}""";

    [Fact]
    public void Status_updates_the_flight_mode_and_raises_a_change()
    {
        var (bus, state) = Tracked();
        var raised = new List<string?>();
        state.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Publish(bus, Status(Bit(0) | Bit(24)));
        Assert.Equal(FlightMode.Docked, state.Flight);
        Assert.Contains(nameof(CommanderState.Flight), raised);

        // The undock the commander asked about: the next Status says space, and that is a change.
        raised.Clear();
        Publish(bus, Status(Bit(24)));
        Assert.Equal(FlightMode.InSpace, state.Flight);
        Assert.Contains(nameof(CommanderState.Flight), raised);

        Publish(bus, Status(Bit(4) | Bit(24)));
        Assert.Equal(FlightMode.Supercruise, state.Flight);
    }

    [Fact]
    public void A_repeated_Status_with_the_same_mode_raises_nothing()
    {
        var (bus, state) = Tracked();
        Publish(bus, Status(Bit(24)));
        var raised = new List<string?>();
        state.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Status.json changes every second for fuel and pips: none of that is a reason to republish.
        Publish(bus, Status(Bit(24) | Bit(3)));

        Assert.DoesNotContain(nameof(CommanderState.Flight), raised);
    }

    [Fact]
    public void A_Status_with_no_ship_bit_keeps_the_previous_mode()
    {
        var (bus, state) = Tracked();
        Publish(bus, Status(Bit(4) | Bit(24)));

        Publish(bus, Status(0));

        Assert.Equal(FlightMode.Supercruise, state.Flight);
    }

    [Fact]
    public void A_Status_without_Flags_leaves_the_mode_alone()
    {
        var (bus, state) = Tracked();
        Publish(bus, Status(Bit(24)));

        Publish(bus, """{"timestamp":"2026-10-17T01:20:01Z","event":"Status","Fuel":{"FuelMain":12.5}}""");

        Assert.Equal(FlightMode.InSpace, state.Flight);
        Assert.Equal(12.5, state.FuelMain);
    }

    [Fact]
    public void A_hyperspace_jump_remembers_its_destination_until_arrival()
    {
        var (bus, state) = Tracked();

        Publish(bus, """{"timestamp":"2026-10-17T01:20:20Z","event":"StartJump","JumpType":"Hyperspace","StarSystem":"Colonia"}""");
        Assert.Equal("Colonia", state.JumpDestination);

        Publish(bus, """{"timestamp":"2026-10-17T01:20:40Z","event":"FSDJump","StarSystem":"Colonia","Body":"Colonia","StarPos":[0,0,0]}""");
        Assert.Null(state.JumpDestination);
    }

    [Fact]
    public void A_supercruise_jump_has_no_destination()
    {
        var (bus, state) = Tracked();

        Publish(bus, """{"timestamp":"2026-10-17T01:20:24Z","event":"StartJump","JumpType":"Supercruise"}""");

        Assert.Null(state.JumpDestination);
    }

    [Fact]
    public void A_malformed_Flags_never_throws()
    {
        var (bus, state) = Tracked();

        // The journal changes between game updates; a field of the wrong type must be ignored, not thrown on.
        Publish(bus, """{"timestamp":"2026-10-17T01:20:00Z","event":"Status","Flags":"lots","Flags2":null}""");
        Publish(bus, """{"timestamp":"2026-10-17T01:20:00Z","event":"Status","Flags":true}""");

        Assert.Equal(FlightMode.Unknown, state.Flight);
    }
}
