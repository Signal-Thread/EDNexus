using EDNexus.Core.Dev;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Core.Surface;
using Xunit;

namespace EDNexus.Tests.Surface;

public class SurfacePositionTrackerTests
{
    private const long InShip = 1L << 24;
    private const long LatLong = 1L << 21;
    private const long Supercruise = 1L << 4;

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

    private static string Status(long flags, string fields = "")
        => $$"""{ "timestamp":"2026-07-01T00:00:00Z", "event":"Status", "Flags":{{flags}}, "Flags2":0 {{(fields.Length > 0 ? "," + fields : "")}} }""";

    private const string Full = """
        "BodyName":"Alpha 1 a", "Latitude":-12.5, "Longitude":179.25, "Altitude":1500.0, "Heading":365, "PlanetRadius":3200000.0
        """;

    [Fact]
    public void Position_set_while_HasLatLong_with_all_fields()
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | LatLong, Full));

        var p = Assert.IsType<SurfacePosition>(state.Position);
        Assert.Equal(-12.5, p.Latitude);
        Assert.Equal(179.25, p.Longitude);
        Assert.Equal(1500.0, p.Altitude);
        Assert.Equal(5.0, p.Heading);            // 365 normalised
        Assert.Equal(3_200_000, p.PlanetRadius);
        Assert.Equal("Alpha 1 a", p.BodyName);
        Assert.Equal(FlightMode.NearSurface, state.Flight);
    }

    [Fact]
    public void Position_clears_when_flag_drops()
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | LatLong, Full));
        Assert.NotNull(state.Position);

        Publish(bus, Status(InShip | Supercruise));
        Assert.Null(state.Position);
    }

    [Fact]
    public void Flag_down_clears_even_if_stale_coordinates_are_still_in_the_file()
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | LatLong, Full));
        Publish(bus, Status(InShip, Full));
        Assert.Null(state.Position);
    }

    [Fact]
    public void Flag_up_without_coordinates_yields_no_position()
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | LatLong));
        Assert.Null(state.Position);
    }

    [Theory]
    [InlineData("\"Latitude\":91, \"Longitude\":0")]
    [InlineData("\"Latitude\":0, \"Longitude\":-181")]
    [InlineData("\"Latitude\":\"x\", \"Longitude\":0")]
    [InlineData("\"Latitude\":null, \"Longitude\":0")]
    [InlineData("\"Latitude\":10")]
    public void Malformed_or_out_of_range_coordinates_are_ignored(string fields)
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | LatLong, fields));
        Assert.Null(state.Position);
    }

    [Fact]
    public void Missing_optional_fields_stay_null()
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | LatLong, "\"Latitude\":1.5, \"Longitude\":2.5"));

        var p = Assert.IsType<SurfacePosition>(state.Position);
        Assert.Null(p.Altitude);
        Assert.Null(p.Heading);
        Assert.Null(p.PlanetRadius);
        Assert.Null(p.DistanceTo(0, 0));
        Assert.Null(p.RelativeBearingTo(0, 0));
    }

    [Fact]
    public void Status_without_Flags_leaves_position_alone()
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | LatLong, Full));
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:01Z", "event":"Status", "Balance":5 }""");
        Assert.NotNull(state.Position);
    }

    [Fact]
    public void Radius_falls_back_to_the_bodys_Scan_even_when_the_scan_came_first()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "BodyName":"Alpha 1 a", "Radius":2500000.0 }""");
        Publish(bus, Status(InShip | LatLong, "\"BodyName\":\"Alpha 1 a\", \"Latitude\":1, \"Longitude\":2"));
        Assert.Equal(2_500_000, state.Position!.PlanetRadius);
    }

    [Fact]
    public void Radius_is_backfilled_when_the_Scan_arrives_after_the_position()
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | LatLong, "\"BodyName\":\"Alpha 1 a\", \"Latitude\":1, \"Longitude\":2"));
        Assert.Null(state.Position!.PlanetRadius);
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "BodyName":"alpha 1 A", "Radius":2500000.0 }""");
        Assert.Equal(2_500_000, state.Position!.PlanetRadius);
    }

    [Fact]
    public void Status_radius_wins_over_Scan_and_other_bodys_scan_is_not_used()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "BodyName":"Alpha 1 a", "Radius":2500000.0 }""");
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "BodyName":"Other", "Radius":1.0 }""");
        Publish(bus, Status(InShip | LatLong, Full));
        Assert.Equal(3_200_000, state.Position!.PlanetRadius);
    }

    [Fact]
    public void Bad_scan_radius_is_ignored()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "BodyName":"A", "Radius":-4 }""");
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "BodyName":"A", "Radius":"big" }""");
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan" }""");
        Publish(bus, Status(InShip | LatLong, "\"BodyName\":\"A\", \"Latitude\":1, \"Longitude\":2"));
        Assert.Null(state.Position!.PlanetRadius);
    }

    [Fact]
    public void Jump_and_supercruise_entry_clear_position_and_body_id_follows_events()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Location", "StarSystem":"Alpha", "Body":"Alpha 1 a", "BodyID":7 }""");
        Assert.Equal(7, state.BodyId);

        Publish(bus, Status(InShip | LatLong, Full));
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:01Z", "event":"SupercruiseEntry", "StarSystem":"Alpha" }""");
        Assert.Null(state.Position);

        Publish(bus, Status(InShip | LatLong, Full));
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:02Z", "event":"FSDJump", "StarSystem":"Beta" }""");
        Assert.Null(state.Position);
        Assert.Null(state.BodyId);   // no BodyID in the event: do not keep the old system's
    }

    [Fact]
    public void Position_change_notifies_and_identical_snapshot_does_not()
    {
        var (bus, state) = NewTracker();
        var raised = 0;
        state.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(CommanderState.Position)) raised++; };

        Publish(bus, Status(InShip | LatLong, Full));
        Publish(bus, Status(InShip | LatLong, Full));
        Assert.Equal(1, raised);
        Publish(bus, Status(InShip));
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Never_visiting_a_planet_leaves_position_null()
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | Supercruise));
        Publish(bus, Status(0));
        Assert.Null(state.Position);
    }

    [Fact]
    public void Position_helpers_use_radius_and_heading()
    {
        var p = new SurfacePosition(0, 0, 100, 90, 1000, "x");
        Assert.Equal(Math.PI / 2 * 1000, p.DistanceTo(0, 90)!.Value, 6);
        Assert.Equal(90, p.BearingTo(0, 10), 6);
        Assert.Equal(0, p.RelativeBearingTo(0, 10)!.Value, 6);      // east, heading east: dead ahead
        Assert.Equal(-90, p.RelativeBearingTo(10, 0)!.Value, 6);    // north is to the left
    }

    [Fact]
    public void Sample_source_drives_a_moving_position_through_the_real_bus()
    {
        var (bus, state) = NewTracker();
        var source = Assert.Single(new DeveloperMode().Sources, s => s.CardKey == "surface");
        var rng = new Random(1);

        var seen = new HashSet<(double, double)>();
        foreach (var line in source.Sample(rng))
        {
            Assert.True(JournalEntry.TryParse(line, historical: false, out var entry));
            bus.Publish(entry);
            if (state.Position is { } p) seen.Add((p.Latitude, p.Longitude));
        }

        Assert.NotNull(state.Position);
        Assert.True(seen.Count > 1, "position should move between snapshots");
        Assert.NotNull(state.Position!.PlanetRadius);   // supplied by the Scan fallback
    }
}
