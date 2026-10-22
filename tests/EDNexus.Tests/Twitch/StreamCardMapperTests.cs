using System.Text;
using System.Text.Json;
using EDNexus.Core.Exobio;
using EDNexus.Core.Journal;
using EDNexus.Core.Missions;
using EDNexus.Core.State;
using EDNexus.Core.Twitch;
using Xunit;

namespace EDNexus.Tests.Twitch;

public class StreamCardMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Docked_headline_shows_the_station_display_name()
    {
        var state = new CommanderState
        {
            Docked = true,
            StationName = "Jameson Memorial",
            StarSystem = "Shinrarta Dezhra",
        };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.Equal("Docked at Jameson Memorial", card.Headline);
    }

    [Fact]
    public void Docked_at_own_carrier_headlines_the_carrier_name_not_the_callsign()
    {
        var state = new CommanderState
        {
            Docked = true,
            StationName = "K7Q-B3L",
            CarrierName = "Gone Sampling",
            CarrierCallsign = "K7Q-B3L",
        };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.Equal("Docked at Gone Sampling", card.Headline);
    }

    [Fact]
    public void Undocked_headline_pairs_the_system_with_a_distinct_body()
    {
        var state = new CommanderState { StarSystem = "Nervi", Body = "Nervi 2 a" };

        Assert.Equal("Nervi / Nervi 2 a", StreamCardMapper.Map(state, now: Now).Headline);
    }

    [Fact]
    public void Undocked_headline_omits_a_body_that_repeats_the_system()
    {
        var state = new CommanderState { StarSystem = "Sol", Body = "Sol" };

        Assert.Equal("Sol", StreamCardMapper.Map(state, now: Now).Headline);
    }

    [Fact]
    public void No_known_location_falls_back_to_a_generic_headline()
    {
        Assert.Equal("In the black", StreamCardMapper.Map(new CommanderState(), now: Now).Headline);
    }

    [Fact]
    public void Credits_are_withheld_by_default()
    {
        var state = new CommanderState { Name = "Hadfield", Balance = 1_842_900_311 };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.Equal("Hadfield", card.Commander!.Name);
        Assert.Null(card.Commander.Credits);
    }

    [Fact]
    public void Credits_are_published_once_the_broadcaster_opts_in()
    {
        var state = new CommanderState { Balance = 1_842_900_311 };
        var visibility = StreamCardVisibility.Default with { Credits = true };

        var card = StreamCardMapper.Map(state, visibility: visibility, now: Now);

        Assert.Equal(1_842_900_311, card.Commander!.Credits);
    }

    [Fact]
    public void A_hidden_section_is_absent_from_the_payload_entirely()
    {
        var state = new CommanderState
        {
            StarSystem = "Nervi",
            Ship = "Krait Phantom",
            Docked = true,
            StationName = "Jameson Memorial",
        };
        var visibility = StreamCardVisibility.Default with { Location = false };

        var card = StreamCardMapper.Map(state, visibility: visibility, now: Now);
        var json = JsonSerializer.Serialize(card, StreamCardSnapshot.SerializerOptions);

        Assert.Null(card.Location);
        // Not merely null on the wire — the property (and the station name inside it) never leaves
        // the commander's machine.
        Assert.DoesNotContain("\"loc\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Jameson Memorial", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Hiding_the_location_also_drops_it_from_the_headline()
    {
        var state = new CommanderState { Docked = true, StationName = "Jameson Memorial", StarSystem = "Sol" };
        var visibility = StreamCardVisibility.Default with { Location = false };

        var card = StreamCardMapper.Map(state, visibility: visibility, now: Now);

        Assert.Equal("Elite Dangerous", card.Headline);
    }

    [Fact]
    public void Station_details_are_dropped_once_the_commander_undocks()
    {
        var state = new CommanderState { StarSystem = "Sol", StationName = "Abraham Lincoln", StationType = "Orbis" };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.False(card.Location!.Docked);
        Assert.Null(card.Location.Station);
        Assert.Null(card.Location.StationType);
    }

    [Fact]
    public void Carrier_is_omitted_until_one_has_been_seen()
    {
        Assert.Null(StreamCardMapper.Map(new CommanderState(), now: Now).Carrier);
    }

    [Fact]
    public void Carrier_reports_its_pending_jump()
    {
        var departure = Now.AddMinutes(15);
        var state = new CommanderState
        {
            CarrierName = "Gone Sampling",
            CarrierCallsign = "K7Q-B3L",
            CarrierFuel = 812,
            CarrierJumpRange = 500,
            CarrierPendingSystem = "Colonia",
            CarrierPendingDeparture = departure,
        };

        var carrier = StreamCardMapper.Map(state, now: Now).Carrier!;

        Assert.Equal("Gone Sampling", carrier.Name);
        Assert.Equal("Colonia", carrier.PendingSystem);
        Assert.Equal(departure, carrier.DepartsAt);
    }

    [Fact]
    public void Hiding_the_location_also_withholds_the_carrier_jump_destination()
    {
        var state = new CommanderState
        {
            CarrierName = "Gone Sampling",
            CarrierCallsign = "K7Q-B3L",
            CarrierPendingSystem = "Colonia",
            CarrierPendingDeparture = Now.AddMinutes(15),
        };

        var card = StreamCardMapper.Map(state, visibility: StreamCardVisibility.Default with { Location = false }, now: Now);
        var json = JsonSerializer.Serialize(card, StreamCardSnapshot.SerializerOptions);

        // Where the carrier is jumping is where the commander will be in fifteen minutes.
        Assert.Equal("Gone Sampling", card.Carrier!.Name);
        Assert.Null(card.Carrier.PendingSystem);
        Assert.DoesNotContain("Colonia", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Hiding_the_location_also_withholds_the_mission_target_faction()
    {
        var bus = new JournalEventBus();
        var missions = new MissionTracker(bus);
        Assert.True(JournalEntry.TryParse(
            """
            { "timestamp": "2026-09-15T17:00:00Z", "event": "MissionAccepted", "Faction": "Union of Kremata Front",
              "Name": "Mission_MassacreWing", "LocalisedName": "Kill 42 Pirates", "TargetType": "$MissionUtil_FactionTag_Pirate;",
              "TargetType_Localised": "Pirates", "TargetFaction": "Kremata Blue Society", "KillCount": 42,
              "DestinationSystem": "Kremata", "Expiry": "2026-09-22T12:00:00Z", "Reward": 1000000, "MissionID": 1 }
            """, historical: false, out var accepted));
        bus.Publish(accepted);
        var sources = new StreamCardSources(Missions: missions);

        var shown = StreamCardMapper.Map(new CommanderState(), sources, now: Now);
        var hidden = StreamCardMapper.Map(new CommanderState(), sources, StreamCardVisibility.Default with { Location = false }, Now);
        var json = JsonSerializer.Serialize(hidden, StreamCardSnapshot.SerializerOptions);

        // Minor factions live in a handful of systems, so the target faction narrows down where the
        // commander is. The rest of the stack is still useful without it.
        Assert.Equal("Kremata Blue Society", shown.Missions!.Stacks![0].Faction);
        Assert.Null(hidden.Missions!.Stacks![0].Faction);
        Assert.Equal(42, hidden.Missions.Stacks[0].Kills);
        Assert.DoesNotContain("Kremata", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Cargo_lists_the_biggest_lots_first_and_is_capped()
    {
        var state = new CommanderState();
        for (var i = 0; i < StreamCardMapper.MaxCargoItems + 5; i++)
            state.Cargo[$"Commodity{i}"] = i + 1;

        var cargo = StreamCardMapper.Map(state, now: Now).Cargo!;

        Assert.Equal(StreamCardMapper.MaxCargoItems, cargo.Count);
        Assert.Equal(StreamCardMapper.MaxCargoItems + 5, cargo[0].Tons);
        Assert.True(cargo[0].Tons > cargo[^1].Tons);
    }

    [Fact]
    public void A_truncated_hold_reports_how_many_lots_were_left_out()
    {
        var state = new CommanderState();
        for (var i = 0; i < StreamCardMapper.MaxCargoItems + 5; i++)
            state.Cargo[$"Commodity{i}"] = i + 1;

        var card = StreamCardMapper.Map(state, now: Now);

        // Without this the card would present 8 lots as the entire hold.
        Assert.Equal(StreamCardMapper.MaxCargoItems, card.Cargo!.Count);
        Assert.Equal(5, card.CargoMore);
    }

    [Fact]
    public void A_hold_that_fits_reports_no_overflow()
    {
        var state = new CommanderState();
        state.Cargo["Painite"] = 96;

        Assert.Equal(0, StreamCardMapper.Map(state, now: Now).CargoMore);
    }

    [Fact]
    public void Overflow_is_not_reported_when_the_hold_is_hidden()
    {
        var state = new CommanderState();
        for (var i = 0; i < StreamCardMapper.MaxCargoItems + 5; i++)
            state.Cargo[$"Commodity{i}"] = i + 1;

        var card = StreamCardMapper.Map(
            state, visibility: StreamCardVisibility.Default with { Cargo = false }, now: Now);

        // A count of what is in the hold is still information about the hold.
        Assert.Null(card.Cargo);
        Assert.Equal(0, card.CargoMore);
    }

    [Fact]
    public void Empty_sections_are_omitted_rather_than_sent_blank()
    {
        var card = StreamCardMapper.Map(new CommanderState(), now: Now);

        Assert.Null(card.Cargo);
        Assert.Null(card.Exobiology);
        Assert.Null(card.Mining);
        Assert.Null(card.Missions);
    }

    [Fact]
    public void A_full_card_stays_inside_the_five_kibibyte_pubsub_cap()
    {
        var state = new CommanderState
        {
            Name = "Hadfield",
            Balance = 1_842_900_311,
            Ship = "Krait Phantom",
            ShipName = "Wandering Albatross",
            ShipIdent = "PH-01",
            StarSystem = "Hypiae Aescs FB-W c1-1046",
            Body = "Hypiae Aescs FB-W c1-1046 A 3 f",
            FuelMain = 9.4,
            FuelCapacity = 32,
            CargoTons = 96,
            CarrierName = "Gone Sampling",
            CarrierCallsign = "K7Q-B3L",
            CarrierFuel = 812,
            CarrierJumpRange = 500,
            CarrierPendingSystem = "Colonia",
            CarrierPendingDeparture = Now.AddMinutes(15),
        };
        for (var i = 0; i < StreamCardMapper.MaxCargoItems; i++)
            state.Cargo[$"Low Temperature Diamonds {i}"] = 64;

        var visibility = StreamCardVisibility.Default with { Credits = true };
        var json = JsonSerializer.Serialize(
            StreamCardMapper.Map(state, visibility: visibility, now: Now), StreamCardSnapshot.SerializerOptions);

        // Twitch rejects a PubSub message over 5 KiB outright, and the EBS refuses one before it
        // ever gets there — so the shape the mapper emits has to fit with room to spare.
        Assert.True(Encoding.UTF8.GetByteCount(json) < 5000, $"Payload was {Encoding.UTF8.GetByteCount(json)} bytes.");
    }

    [Fact]
    public void Jump_range_is_the_figure_the_game_reports_not_a_derived_one()
    {
        // The card shows what the Loadout reported (25.05 ly here), not a figure re-derived from the
        // drive model — which leaves out a Guardian booster. A viewer comparing the card to the
        // commander's ship panel must see 25.05.
        var state = new CommanderState
        {
            Ship = "anaconda",
            FuelMain = 0,
            Fsd = new EDNexus.Core.Ship.ShipFsdProfile(
                OptimalMass: 1050, BaseMass: 1379.9, TankSize: 32, ReserveSize: 1.07,
                FuelMultiplier: 0.012, FuelPower: 2.45, MaxFuelPerJump: 5, RangeBoost: 0,
                CargoCapacity: 0, MaxJumpRange: 25.052088),
        };

        Assert.Equal(25.05, StreamCardMapper.Map(state, now: Now).Ship!.JumpRange);
    }

    [Fact]
    public void Jump_range_is_omitted_when_the_loadout_never_reported_one()
    {
        var state = new CommanderState { Ship = "anaconda" };

        Assert.Null(StreamCardMapper.Map(state, now: Now).Ship!.JumpRange);
    }

    /// <summary>
    /// An exobiology tracker that has seen one body's bio signals, for the location-leak tests.
    /// </summary>
    private static StreamCardSources ExobiologySources()
    {
        var bus = new JournalEventBus();
        var tracker = new ExobiologyTracker(bus, new CommanderState());
        const string dss = """
        { "timestamp":"2026-08-01T10:00:00Z", "event":"SAASignalsFound", "BodyName":"Nervi 2 a",
          "SystemAddress":2871051298217, "BodyID":12,
          "Signals":[{"Type":"$SAA_SignalType_Biological;","Type_Localised":"Biological","Count":3}],
          "Genuses":[{"Genus":"$Codex_Ent_Stratum_Genus_Name;","Genus_Localised":"Stratum"}] }
        """;
        Assert.True(JournalEntry.TryParse(dss, historical: false, out var entry));
        bus.Publish(entry);

        // CurrentBody — the "bio signals here" summary the card shows — is only set once the
        // commander has actually arrived at the body.
        const string approach = """
        { "timestamp":"2026-08-01T10:05:00Z", "event":"ApproachBody", "StarSystem":"Nervi",
          "SystemAddress":2871051298217, "Body":"Nervi 2 a", "BodyID":12 }
        """;
        Assert.True(JournalEntry.TryParse(approach, historical: false, out var arrival));
        bus.Publish(arrival);

        return new StreamCardSources(Exobiology: tracker);
    }

    [Fact]
    public void Exobiology_names_the_body_while_location_is_shown()
    {
        var card = StreamCardMapper.Map(new CommanderState(), ExobiologySources(), now: Now);

        Assert.Equal("Nervi 2 a", card.Exobiology!.BodyName);
    }

    [Fact]
    public void Exobiology_withholds_the_body_once_location_is_hidden()
    {
        var visibility = StreamCardVisibility.Default with { Location = false };

        var card = StreamCardMapper.Map(new CommanderState(), ExobiologySources(), visibility, Now);
        var json = JsonSerializer.Serialize(card, StreamCardSnapshot.SerializerOptions);

        // An Elite body name carries its system name, so publishing it here would hand viewers the
        // exact thing hiding Location exists to withhold — the settings UI promises anything
        // unchecked is never sent.
        Assert.NotNull(card.Exobiology);
        Assert.Null(card.Exobiology!.BodyName);
        Assert.DoesNotContain("Nervi", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_ignores_the_timestamp_but_not_the_content()
    {
        var state = new CommanderState { StarSystem = "Sol" };

        var first = StreamCardMapper.Map(state, now: Now);
        var later = StreamCardMapper.Map(state, now: Now.AddMinutes(5));

        Assert.Equal(first.ContentFingerprint(), later.ContentFingerprint());

        state.StarSystem = "Nervi";
        Assert.NotEqual(first.ContentFingerprint(), StreamCardMapper.Map(state, now: Now).ContentFingerprint());
    }

    // --- flight mode: what the commander is doing, not only whether they are docked ----------------

    [Fact]
    public void Supercruise_is_in_the_headline_and_the_location_section()
    {
        var state = new CommanderState { StarSystem = "Nervi", Flight = FlightMode.Supercruise };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.Equal("Supercruise · Nervi", card.Headline);
        Assert.Equal("supercruise", card.Location!.Mode);
        Assert.Equal("In supercruise", card.Location.Status);
    }

    [Fact]
    public void Landed_names_the_body()
    {
        var state = new CommanderState { StarSystem = "Nervi", Body = "Nervi 2 a", Flight = FlightMode.Landed };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.Equal("Landed · Nervi / Nervi 2 a", card.Headline);
        Assert.Equal("landed", card.Location!.Mode);
        Assert.Equal("Landed", card.Location.Status);
    }

    [Theory]
    [InlineData(FlightMode.InSpace, "Flying · Nervi", "space")]
    [InlineData(FlightMode.NearSurface, "Flying low · Nervi", "surface")]
    [InlineData(FlightMode.Srv, "In the SRV · Nervi", "srv")]
    [InlineData(FlightMode.OnFoot, "On foot · Nervi", "foot")]
    public void Each_other_mode_has_its_own_label_and_token(FlightMode mode, string headline, string token)
    {
        var card = StreamCardMapper.Map(new CommanderState { StarSystem = "Nervi", Flight = mode }, now: Now);

        Assert.Equal(headline, card.Headline);
        Assert.Equal(token, card.Location!.Mode);
    }

    [Fact]
    public void A_hyperspace_jump_headlines_the_destination_not_the_system_left_behind()
    {
        var state = new CommanderState
        {
            StarSystem = "Nervi",
            Flight = FlightMode.Hyperspace,
            JumpDestination = "Colonia",
        };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.Equal("Jumping to Colonia", card.Headline);
        Assert.Equal("Jumping to Colonia", card.Location!.Status);
        Assert.Equal("hyperspace", card.Location.Mode);
    }

    [Fact]
    public void A_hyperspace_jump_with_no_known_destination_still_says_so()
    {
        var state = new CommanderState { StarSystem = "Nervi", Flight = FlightMode.Hyperspace };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.Equal("Hyperspace jump", card.Headline);
        Assert.Equal("In hyperspace", card.Location!.Status);
    }

    [Fact]
    public void Docked_keeps_its_headline_whatever_the_flight_mode_says()
    {
        var state = new CommanderState
        {
            Docked = true,
            StationName = "Jameson Memorial",
            Flight = FlightMode.Docked,
        };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.Equal("Docked at Jameson Memorial", card.Headline);
        Assert.Equal("docked", card.Location!.Mode);
    }

    [Fact]
    public void With_no_flight_mode_yet_the_card_reads_exactly_as_it_always_did()
    {
        // Before the first Status.json: the headline and location are the old ones, and the new
        // fields are absent rather than guessed.
        var state = new CommanderState { StarSystem = "Nervi", Body = "Nervi 2 a" };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.Equal("Nervi / Nervi 2 a", card.Headline);
        Assert.Null(card.Location!.Mode);
        Assert.Null(card.Location.Status);
        var json = JsonSerializer.Serialize(card, StreamCardSnapshot.SerializerOptions);
        Assert.DoesNotContain("\"mode\"", json);
        Assert.DoesNotContain("\"status\"", json);
    }

    [Fact]
    public void Hiding_the_location_hides_the_flight_state_and_the_jump_destination_too()
    {
        // Where the commander is heading is where they will be: exactly what hiding Location withholds.
        var state = new CommanderState
        {
            StarSystem = "Nervi",
            Body = "Nervi 2 a",
            Flight = FlightMode.Hyperspace,
            JumpDestination = "Colonia",
        };

        var card = StreamCardMapper.Map(state, visibility: StreamCardVisibility.Default with { Location = false }, now: Now);
        var json = JsonSerializer.Serialize(card, StreamCardSnapshot.SerializerOptions);

        Assert.Equal("Elite Dangerous", card.Headline);
        Assert.Null(card.Location);
        Assert.DoesNotContain("Colonia", json);
        Assert.DoesNotContain("hyperspace", json);
    }

    [Fact]
    public void A_change_of_flight_mode_changes_the_content_fingerprint_so_it_is_published()
    {
        var state = new CommanderState { StarSystem = "Nervi", Flight = FlightMode.InSpace };
        var before = StreamCardMapper.Map(state, now: Now).ContentFingerprint();

        state.Flight = FlightMode.Supercruise;

        Assert.NotEqual(before, StreamCardMapper.Map(state, now: Now).ContentFingerprint());
    }

    [Fact]
    public void Walking_around_a_station_is_on_foot_not_docked_even_though_the_ship_stays_docked()
    {
        // Status.json keeps the ship's Docked bit while the commander is out of it.
        var state = new CommanderState
        {
            Docked = true,
            StationName = "Jameson Memorial",
            Flight = FlightMode.OnFoot,
        };

        var card = StreamCardMapper.Map(state, now: Now);

        Assert.Equal("On foot at Jameson Memorial", card.Headline);
        Assert.Equal("foot", card.Location!.Mode);
        Assert.Equal("On foot", card.Location.Status);
    }
}
