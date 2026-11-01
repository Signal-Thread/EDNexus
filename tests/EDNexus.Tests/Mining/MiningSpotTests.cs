using System;
using System.Collections.Generic;
using System.Linq;
using EDNexus.Core.Colonisation;
using EDNexus.Core.Exobio;
using EDNexus.Core.Journal;
using EDNexus.Core.Mining;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Core.Voice;
using Xunit;

namespace EDNexus.Tests.Mining;

public class MiningSpotTests
{
    private const long SwoiphsAddress = 3756292819331;
    private const string Swoiphs = "Swoiphs AW-C d109";
    private const double MoonRadius = 1102587.5;   // Swoiphs AW-C d109 5 a, from its Scan event

    private static void Publish(JournalEventBus bus, string json, bool historical = false)
    {
        Assert.True(JournalEntry.TryParse(json, historical, out var entry), "sample JSON failed to parse");
        bus.Publish(entry);
    }

    private const string Arrive = """{ "timestamp":"2026-09-13T19:50:00Z", "event":"FSDJump", "StarSystem":"Swoiphs AW-C d109", "SystemAddress":3756292819331 }""";

    private static string SrvStatus(double lat, double lon) => $$"""
        { "timestamp":"2026-09-13T20:28:00Z", "event":"Status", "Flags":69225480, "Latitude":{{lat}}, "Longitude":{{lon}},
          "Heading":12, "Altitude":0, "BodyName":"Swoiphs AW-C d109 5 a", "PlanetRadius":{{MoonRadius}} }
        """;

    private const string Monazite = """{ "timestamp":"2026-09-13T20:28:28Z", "event":"MiningRefined", "Type":"$monazite_name;", "Type_Localised":"Monazite" }""";

    // --- MiningTracker position capture ---

    [Fact]
    public void A_unit_refined_in_an_srv_is_pinned_to_the_latest_status_position()
    {
        var bus = new JournalEventBus();
        var tracker = new MiningTracker(bus);

        Publish(bus, Arrive);
        Publish(bus, SrvStatus(-10.887676, 43.056656));
        Publish(bus, Monazite);

        var at = Assert.Single(tracker.Refined).Position!;
        Assert.Equal(SwoiphsAddress, at.SystemAddress);
        Assert.Equal(Swoiphs, at.StarSystem);
        Assert.Equal("Swoiphs AW-C d109 5 a", at.Body);
        Assert.Equal(-10.887676, at.Latitude);
        Assert.Equal(43.056656, at.Longitude);
        Assert.Equal(MoonRadius, at.PlanetRadius);
    }

    [Fact]
    public void Ship_refining_is_not_pinned_to_a_position()
    {
        var bus = new JournalEventBus();
        var tracker = new MiningTracker(bus);

        Publish(bus, Arrive);
        Publish(bus, SrvStatus(-10.88, 43.05));
        // Back aboard the ship, out in a ring: no SRV flag, no surface position.
        Publish(bus, """{ "timestamp":"2026-09-13T21:00:00Z", "event":"Status", "Flags":16777240 }""");
        Publish(bus, Monazite);

        Assert.Null(Assert.Single(tracker.Refined).Position);
    }

    // --- MiningSpotBook ---

    private static RefinedUnit Unit(string symbol, string name, double lat, double lon, string body = "Swoiphs AW-C d109 5 a", long address = SwoiphsAddress) =>
        new(DateTimeOffset.Parse("2026-09-13T20:28:28Z"), symbol, name,
            new MiningFix(address, Swoiphs, body, lat, lon, MoonRadius));

    [Fact]
    public void Units_mined_close_together_fold_into_one_spot()
    {
        var spots = new List<KnownMiningSpot>();
        spots = MiningSpotBook.Record(spots, Unit("monazite", "Monazite", -10.8876, 43.0566), 195_083);
        spots = MiningSpotBook.Record(spots, Unit("monazite", "Monazite", -10.8880, 43.0570), 195_083);   // ~10 m away

        var spot = Assert.Single(spots);
        Assert.Equal(2, spot.Tonnes);
        Assert.Equal(-10.8876, spot.Latitude);
    }

    [Fact]
    public void Distant_units_or_different_commodities_make_separate_spots()
    {
        var spots = new List<KnownMiningSpot>();
        spots = MiningSpotBook.Record(spots, Unit("monazite", "Monazite", -10.8876, 43.0566), 195_083);
        spots = MiningSpotBook.Record(spots, Unit("monazite", "Monazite", -11.0313, 43.1665), 195_083);   // ~3.3 km away
        spots = MiningSpotBook.Record(spots, Unit("thortveitite", "Thortveitite", -10.8876, 43.0566), 129_763);

        Assert.Equal(3, spots.Count);
    }

    [Fact]
    public void Recording_never_mutates_the_list_it_was_given()
    {
        var original = MiningSpotBook.Record(new List<KnownMiningSpot>(), Unit("monazite", "Monazite", -10.8876, 43.0566), 195_083);
        _ = MiningSpotBook.Record(original, Unit("monazite", "Monazite", -10.8876, 43.0566), 195_083);

        Assert.Equal(1, Assert.Single(original).Tonnes);
    }

    [Fact]
    public void Only_spots_in_the_system_and_above_the_threshold_are_worth_mining()
    {
        var spots = new List<KnownMiningSpot>();
        spots = MiningSpotBook.Record(spots, Unit("bastnasite", "Bastnasite", -10.0, 40.0), 66_401);
        spots = MiningSpotBook.Record(spots, Unit("monazite", "Monazite", -10.8876, 43.0566), 195_083);
        spots = MiningSpotBook.Record(spots, Unit("monazite", "Monazite", -20.0, 10.0, address: 42), 195_083);

        var worth = MiningSpotBook.WorthMiningIn(spots, SwoiphsAddress, Swoiphs, minValue: 100_000);

        Assert.Equal("monazite", Assert.Single(worth).Symbol);
    }

    [Fact]
    public void Callout_groups_spots_by_commodity_and_body_most_valuable_first()
    {
        var spots = new List<KnownMiningSpot>();
        spots = MiningSpotBook.Record(spots, Unit("thortveitite", "Thortveitite", 5.0, 5.0, body: "Swoiphs AW-C d109 5 c"), 129_763);
        spots = MiningSpotBook.Record(spots, Unit("monazite", "Monazite", -10.8876, 43.0566), 195_083);
        spots = MiningSpotBook.Record(spots, Unit("monazite", "Monazite", -11.0313, 43.1665), 195_083);

        Assert.Equal(
            "Known mining spots in Swoiphs AW-C d109: Monazite on 5 a, 2 spots; Thortveitite on 5 c.",
            MiningSpotBook.DescribeForCallout(Swoiphs, spots));
    }

    // --- Arrival callout ---

    private static (JournalEventBus Bus, List<VoiceCallout> Raised) NewVoice(IReadOnlyList<KnownMiningSpot> spots)
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        var voice = new VoiceCalloutTracker(bus, state, new ExobiologyTracker(bus, state), new ColonisationTracker(bus, state))
        {
            KnownMiningSpotsIn = (address, name) => MiningSpotBook.WorthMiningIn(spots, address, name, 30_000),
        };
        var raised = new List<VoiceCallout>();
        voice.CalloutRaised += raised.Add;
        return (bus, raised);
    }

    [Fact]
    public void Arriving_in_a_system_with_known_spots_calls_them_out_once()
    {
        var spots = MiningSpotBook.Record(new List<KnownMiningSpot>(), Unit("monazite", "Monazite", -10.8876, 43.0566), 195_083);
        var (bus, raised) = NewVoice(spots);

        Publish(bus, Arrive);
        Publish(bus, """{ "timestamp":"2026-09-13T20:00:00Z", "event":"Location", "StarSystem":"Swoiphs AW-C d109", "SystemAddress":3756292819331 }""");

        var callout = Assert.Single(raised);
        Assert.Equal(VoiceCalloutKind.KnownMiningSpots, callout.Kind);
        Assert.Equal("Known mining spots in Swoiphs AW-C d109: Monazite on 5 a.", callout.Text);

        // Leaving and coming back announces again.
        Publish(bus, """{ "timestamp":"2026-09-13T21:00:00Z", "event":"FSDJump", "StarSystem":"Elsewhere", "SystemAddress":42 }""");
        Publish(bus, Arrive);
        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void No_callout_for_systems_without_spots_or_during_replay()
    {
        var spots = MiningSpotBook.Record(new List<KnownMiningSpot>(), Unit("monazite", "Monazite", -10.8876, 43.0566), 195_083);
        var (bus, raised) = NewVoice(spots);

        Publish(bus, Arrive, historical: true);
        Publish(bus, """{ "timestamp":"2026-09-13T21:00:00Z", "event":"FSDJump", "StarSystem":"Elsewhere", "SystemAddress":42 }""");

        Assert.Empty(raised);
    }
}
