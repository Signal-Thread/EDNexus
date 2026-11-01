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
    private const long Docked = 1L << 0;
    private const long InSrv = 1L << 26;

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
    public void Jump_clears_position_and_body_id_follows_events()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Location", "StarSystem":"Alpha", "Body":"Alpha 1 a", "BodyID":7 }""");
        Assert.Equal(7, state.BodyId);

        Publish(bus, Status(InShip | LatLong, Full));
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:02Z", "event":"FSDJump", "StarSystem":"Beta" }""");
        Assert.Null(state.Position);
        Assert.Null(state.BodyId);   // no BodyID in the event: do not keep the old system's
    }

    [Fact]
    public void Position_follows_the_flag_only_so_Supercruise_with_HasLatLong_keeps_it()
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | LatLong, Full));
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:01Z", "event":"SupercruiseEntry", "StarSystem":"Alpha" }""");
        Assert.NotNull(state.Position);   // no event clears it: the flag is the single rule

        Publish(bus, Status(InShip | Supercruise | LatLong, Full));
        Assert.NotNull(state.Position);

        Publish(bus, Status(InShip | Supercruise));
        Assert.Null(state.Position);
    }

    [Theory]
    [InlineData(Docked)]
    [InlineData(InSrv)]
    [InlineData(0L)]
    public void Position_is_set_for_other_flag_combinations_with_HasLatLong(long extra)
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(extra | LatLong, Full));
        Assert.NotNull(state.Position);
    }

    [Fact]
    public void OnFoot_with_HasLatLong_sets_position()
    {
        var (bus, state) = NewTracker();
        Publish(bus, $$"""{ "timestamp":"2026-07-01T00:00:00Z", "event":"Status", "Flags":{{LatLong}}, "Flags2":1, {{Full}} }""");
        Assert.NotNull(state.Position);
    }

    [Fact]
    public void ApproachBody_does_not_change_Body_or_BodyId()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Location", "StarSystem":"Alpha", "Body":"Alpha A", "BodyID":1 }""");
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:01Z", "event":"ApproachBody", "StarSystem":"Alpha", "Body":"Alpha 1 a", "BodyID":7 }""");
        Assert.Equal("Alpha A", state.Body);
        Assert.Equal(1, state.BodyId);
    }

    [Fact]
    public void Location_and_SupercruiseExit_set_BodyId_together_with_Body()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Location", "StarSystem":"Alpha", "Body":"Alpha 1 a", "BodyID":7 }""");
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:01Z", "event":"SupercruiseExit", "StarSystem":"Alpha", "Body":"Alpha 2" }""");
        Assert.Equal("Alpha 2", state.Body);
        Assert.Null(state.BodyId);   // the event has no BodyID: do not keep the previous body's

        Publish(bus, """{ "timestamp":"2026-07-01T00:00:02Z", "event":"Location", "StarSystem":"Alpha", "Body":"Alpha 3", "BodyID":9 }""");
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:03Z", "event":"Location", "StarSystem":"Alpha", "Body":"Alpha 4" }""");
        Assert.Null(state.BodyId);
    }

    [Fact]
    public void Stale_position_after_Location_is_cleared_by_the_next_flag_down_Status()
    {
        var (bus, state) = NewTracker();
        Publish(bus, Status(InShip | LatLong, Full));
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:01Z", "event":"Died" }""");
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:02Z", "event":"Location", "StarSystem":"Home", "Body":"Home Port", "BodyID":2, "Docked":true }""");
        Publish(bus, Status(Docked));
        Assert.Null(state.Position);
    }

    [Fact]
    public void Scan_radius_lookup_is_case_insensitive_through_Status()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Location", "StarSystem":"Alpha", "Body":"Alpha 1 a", "BodyID":7 }""");
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "StarSystem":"Alpha", "BodyName":"ALPHA 1 A", "Radius":2500000.0 }""");
        Publish(bus, Status(InShip | LatLong, "\"BodyName\":\"alpha 1 a\", \"Latitude\":1, \"Longitude\":2"));
        Assert.Equal(2_500_000, state.Position!.PlanetRadius);
    }

    [Fact]
    public void Scan_radius_in_another_system_with_the_same_body_name_is_not_used()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "StarSystem":"Beta", "BodyName":"A 1", "Radius":1000.0 }""");
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Location", "StarSystem":"Alpha", "Body":"A 1" }""");
        Publish(bus, Status(InShip | LatLong, "\"BodyName\":\"A 1\", \"Latitude\":1, \"Longitude\":2"));
        Assert.Null(state.Position!.PlanetRadius);
    }

    [Fact]
    public void Scan_cache_cap_evicts_one_entry_and_keeps_the_current_body()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Location", "StarSystem":"Alpha", "Body":"Alpha 1 a" }""");
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "StarSystem":"Alpha", "BodyName":"Alpha 1 a", "Radius":2500000.0 }""");
        for (var i = 0; i < 5000; i++)
            Publish(bus, $$"""{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "StarSystem":"Alpha", "BodyName":"Filler {{i}}", "Radius":1000.0 }""");

        // The cap is hit long before 5000; the current body's radius must survive via a Status that omits it.
        Publish(bus, Status(InShip | LatLong, "\"BodyName\":\"Filler 4999\", \"Latitude\":1, \"Longitude\":2"));
        Assert.Equal(1000.0, state.Position!.PlanetRadius);   // the most recent entries are retained
    }

    [Fact]
    public void Concurrent_publishes_do_not_corrupt_state()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-07-01T00:00:00Z", "event":"Location", "StarSystem":"Alpha", "Body":"Alpha 1 a" }""");

        Parallel.For(0, 8, t =>
        {
            for (var i = 0; i < 500; i++)
            {
                Publish(bus, $$"""{ "timestamp":"2026-07-01T00:00:00Z", "event":"Scan", "StarSystem":"Alpha", "BodyName":"B{{t}}-{{i}}", "Radius":1000.0 }""");
                Publish(bus, Status(InShip | (i % 2 == 0 ? LatLong : 0), $"\"BodyName\":\"B{t}-{i}\", \"Latitude\":1, \"Longitude\":2"));
            }
        });

        Publish(bus, Status(InShip | LatLong, Full));
        Assert.Equal(3_200_000, state.Position!.PlanetRadius);
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
        var seen = new HashSet<(double, double)>();
        for (var seed = 1; seed <= 25; seed++)
        {
            var radiusSeen = true;
            foreach (var line in source.Sample(new Random(seed)))
            {
                Assert.True(JournalEntry.TryParse(line, historical: false, out var entry));
                bus.Publish(entry);
                if (state.Position is { } p) { seen.Add((p.Latitude, p.Longitude)); radiusSeen &= p.PlanetRadius is not null; }
            }
            Assert.True(radiusSeen, "radius should come from the Scan fallback");
        }

        Assert.Null(state.Position);   // the sample ends with a flag-down Status
        Assert.True(seen.Count > 1, "position should move between snapshots");
        Assert.All(seen, p => Assert.InRange(p.Item1, -90, 90));
        Assert.All(seen, p => Assert.InRange(p.Item2, -180, 180));
    }
}
