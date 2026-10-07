using System.Text.Json;
using System.Text.Json.Nodes;
using EliteDangerous.Eddn;
using Xunit;

namespace EDNexus.Tests.Reporting;

public class EddnTransformerTests
{
    private static readonly EddnClientOptions Options = new()
    {
        SoftwareName = "EDNexus.Tests",
        SoftwareVersion = "1.0.0",
    };

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static (EddnJournalTransformer, EddnState) NewPair()
    {
        var state = new EddnState();
        return (new EddnJournalTransformer(Options), state);
    }

    [Fact]
    public void Journal_event_strips_localised_and_private_fields_and_keeps_location()
    {
        var (t, state) = NewPair();
        var raw = Json("""
        {
          "timestamp": "2020-01-01T00:00:00Z", "event": "FSDJump",
          "StarSystem": "Sol", "SystemAddress": 10477373803, "StarPos": [0.0, 0.0, 0.0],
          "JumpDist": 8.5, "FuelUsed": 1.2, "FuelLevel": 30.0,
          "SystemFaction": { "Name": "Foo", "Name_Localised": "Foo Loc" }
        }
        """);
        state.Observe(raw);

        var msg = t.Transform(raw, state);

        Assert.NotNull(msg);
        var message = msg!.Envelope["message"]!;
        Assert.Equal("Sol", (string?)message["StarSystem"]);
        Assert.NotNull(message["SystemAddress"]);
        Assert.NotNull(message["StarPos"]);
        // Private / financial / positional keys must be gone.
        Assert.Null(message["JumpDist"]);
        Assert.Null(message["FuelUsed"]);
        Assert.Null(message["FuelLevel"]);
        // No key anywhere may end in _Localised.
        Assert.DoesNotContain("_Localised", msg.ToString(), StringComparison.Ordinal);
    }

    // Factions[] as the game writes it for a populated system: the public BGS state EDDN wants, plus the
    // commander's own reputation and squadron flags, which the journal schema disallows inside Factions.
    private const string PopulatedFactions = """
        [
          { "Name": "Pilots' Federation Local Branch", "FactionState": "None", "Government": "Democracy",
            "Influence": 0.0, "Allegiance": "PilotsFederation", "Happiness": "", "MyReputation": 8.3 },
          { "Name": "Eravate School of Commerce", "FactionState": "Boom", "Government": "Corporate",
            "Influence": 0.472, "Allegiance": "Independent", "Happiness": "$Faction_HappinessBand2;",
            "Happiness_Localised": "Happy", "MyReputation": 42.5,
            "ActiveStates": [ { "State": "Boom" } ], "PendingStates": [ { "State": "Expansion", "Trend": 0 } ],
            "SquadronFaction": true, "HappiestSystem": true, "HomeSystem": true },
          { "Name": "Official Eravate Liberals", "FactionState": "War", "Government": "Democracy",
            "Influence": 0.311, "Allegiance": "Federation", "Happiness": "$Faction_HappinessBand2;",
            "Happiness_Localised": "Happy", "MyReputation": -15.25,
            "RecoveringStates": [ { "State": "Election", "Trend": 0 } ], "ActiveStates": [ { "State": "War" } ] },
          { "Name": "Eravate Network", "FactionState": "None", "Government": "Corporate",
            "Influence": 0.217, "Allegiance": "Independent", "Happiness": "$Faction_HappinessBand3;",
            "Happiness_Localised": "Discontented", "MyReputation": 0.0 }
        ]
        """;

    // PopulatedFactions as it must reach EDDN: per-commander fields and _Localised strings gone,
    // everything else exactly as the game wrote it.
    private const string UploadableFactions = """
        [
          { "Name": "Pilots' Federation Local Branch", "FactionState": "None", "Government": "Democracy",
            "Influence": 0.0, "Allegiance": "PilotsFederation", "Happiness": "" },
          { "Name": "Eravate School of Commerce", "FactionState": "Boom", "Government": "Corporate",
            "Influence": 0.472, "Allegiance": "Independent", "Happiness": "$Faction_HappinessBand2;",
            "ActiveStates": [ { "State": "Boom" } ], "PendingStates": [ { "State": "Expansion", "Trend": 0 } ] },
          { "Name": "Official Eravate Liberals", "FactionState": "War", "Government": "Democracy",
            "Influence": 0.311, "Allegiance": "Federation", "Happiness": "$Faction_HappinessBand2;",
            "RecoveringStates": [ { "State": "Election", "Trend": 0 } ], "ActiveStates": [ { "State": "War" } ] },
          { "Name": "Eravate Network", "FactionState": "None", "Government": "Corporate",
            "Influence": 0.217, "Allegiance": "Independent", "Happiness": "$Faction_HappinessBand3;" }
        ]
        """;

    // A realistic FSDJump, Location or CarrierJump in a populated system, carrying PopulatedFactions.
    private static string PopulatedSystemEvent(string ev)
    {
        var system = $$"""
            "StarSystem": "Eravate", "SystemAddress": 3932277478106, "StarPos": [-42.4375, -3.15625, 59.65625],
            "SystemAllegiance": "Independent", "SystemEconomy": "$economy_Industrial;",
            "SystemEconomy_Localised": "Industrial", "SystemGovernment": "$government_Corporate;",
            "SystemGovernment_Localised": "Corporate", "SystemSecurity": "$SYSTEM_SECURITY_high;",
            "SystemSecurity_Localised": "High Security", "Population": 740380179,
            "Factions": {{PopulatedFactions}},
            "SystemFaction": { "Name": "Eravate School of Commerce", "FactionState": "Boom" }
            """;

        return ev switch
        {
            "FSDJump" => $$"""
                { "timestamp": "2026-09-10T18:22:04Z", "event": "FSDJump", "Taxi": false, "Multicrew": false,
                  {{system}}, "Body": "Eravate", "BodyID": 0, "BodyType": "Star",
                  "JumpDist": 12.617, "FuelUsed": 1.302, "FuelLevel": 30.698 }
                """,
            "Location" => $$"""
                { "timestamp": "2026-09-10T18:40:12Z", "event": "Location", "DistFromStarLS": 1033.57, "Docked": true,
                  "StationName": "Cleve Hub", "StationType": "Orbis", "MarketID": 3223343616,
                  "StationFaction": { "Name": "Eravate School of Commerce", "FactionState": "Boom" },
                  "StationEconomies": [
                    { "Name": "$economy_Industrial;", "Name_Localised": "Industrial", "Proportion": 1.0 } ],
                  "Taxi": false, "Multicrew": false, {{system}},
                  "Body": "Cleve Hub", "BodyID": 64, "BodyType": "Station" }
                """,
            "CarrierJump" => $$"""
                { "timestamp": "2026-09-10T19:05:31Z", "event": "CarrierJump", "Docked": true, "OnFoot": false,
                  "StationName": "K7Q-BQL", "StationType": "FleetCarrier", "MarketID": 3700005632,
                  "StationFaction": { "Name": "FleetCarrier" },
                  "StationEconomies": [
                    { "Name": "$economy_Carrier;", "Name_Localised": "Private Enterprise", "Proportion": 1.0 } ],
                  "Taxi": false, "Multicrew": false, {{system}},
                  "Body": "Eravate", "BodyID": 0, "BodyType": "Star" }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(ev), ev, null),
        };
    }

    [Theory]
    [InlineData("FSDJump")]
    [InlineData("Location")]
    [InlineData("CarrierJump")]
    public void Per_commander_fields_are_stripped_from_every_faction(string ev)
    {
        var (t, state) = NewPair();
        var raw = Json(PopulatedSystemEvent(ev));
        state.Observe(raw);

        var msg = t.Transform(raw, state);

        Assert.NotNull(msg);
        // No faction's per-commander fields may reach EDDN...
        var json = msg!.ToString();
        foreach (var key in new[] { "MyReputation", "SquadronFaction", "HappiestSystem", "HomeSystem" })
            Assert.DoesNotContain($"\"{key}\"", json, StringComparison.Ordinal);
        // ...while every other faction field arrives exactly as the game wrote it.
        var factions = msg.Envelope["message"]!["Factions"]!;
        Assert.Equal(JsonNode.Parse(UploadableFactions)!.ToJsonString(), factions.ToJsonString());
    }

    [Theory]
    [InlineData("MyReputation", "8.3")]
    [InlineData("SquadronFaction", "\"Eravate Network\"")]
    [InlineData("HappiestSystem", "\"Eravate\"")]
    [InlineData("HomeSystem", "\"Sol\"")]
    public void Per_commander_fields_are_also_stripped_at_the_event_root(string key, string value)
    {
        var (t, state) = NewPair();
        var raw = Json($$"""
            { "timestamp": "2026-09-10T18:22:04Z", "event": "FSDJump", "Taxi": false, "Multicrew": false,
              "StarSystem": "Eravate", "SystemAddress": 3932277478106, "StarPos": [-42.4375, -3.15625, 59.65625],
              "{{key}}": {{value}} }
            """);
        state.Observe(raw);

        var msg = t.Transform(raw, state);

        Assert.NotNull(msg);
        Assert.DoesNotContain($"\"{key}\"", msg!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Event_without_factions_passes_through_unchanged()
    {
        var (t, state) = NewPair();
        // A jump into an unpopulated system carries no Factions at all. Nothing here is private or
        // localised either, so the message must be exactly what the game wrote.
        const string jump = """
            { "timestamp": "2026-09-10T18:30:00Z", "event": "FSDJump", "Taxi": false, "Multicrew": false,
              "StarSystem": "Col 285 Sector UH-K b8-3", "SystemAddress": 7230664632689,
              "StarPos": [-51.8125, 19.09375, 77.25],
              "SystemAllegiance": "", "SystemEconomy": "$economy_None;", "SystemSecondEconomy": "$economy_None;",
              "SystemGovernment": "$government_None;", "SystemSecurity": "$GAlAXY_MAP_INFO_state_anarchy;",
              "Population": 0, "Body": "Col 285 Sector UH-K b8-3 A", "BodyID": 1, "BodyType": "Star" }
            """;
        var raw = Json(jump);
        state.Observe(raw);

        var message = t.Transform(raw, state)!.Envelope["message"]!;

        Assert.Equal(JsonNode.Parse(jump)!.ToJsonString(), message.ToJsonString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"Eravate Network\"")]
    [InlineData("{ \"Name\": \"Eravate Network\" }")]
    [InlineData("[ null, 7, \"Eravate Network\", [], { \"Name\": \"Eravate Network\", \"MyReputation\": 12.5 } ]")]
    public void Malformed_factions_value_does_not_throw(string factions)
    {
        var (t, state) = NewPair();
        var raw = Json($$"""
            { "timestamp": "2026-09-10T18:22:04Z", "event": "FSDJump",
              "StarSystem": "Eravate", "SystemAddress": 3932277478106, "StarPos": [-42.4375, -3.15625, 59.65625],
              "Factions": {{factions}} }
            """);
        state.Observe(raw);

        var msg = t.Transform(raw, state);

        // The event still transforms, and a well-formed entry among the junk is still cleaned.
        Assert.NotNull(msg);
        Assert.DoesNotContain("MyReputation", msg!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Docked_gets_starpos_augmented_from_prior_jump()
    {
        var (t, state) = NewPair();
        // A jump establishes the current system's coordinates.
        state.Observe(Json("""
        { "timestamp": "2020-01-01T00:00:00Z", "event": "FSDJump",
          "StarSystem": "Sol", "SystemAddress": 10477373803, "StarPos": [1.0, 2.0, 3.0] }
        """));

        var docked = Json("""
        { "timestamp": "2020-01-01T00:05:00Z", "event": "Docked",
          "StarSystem": "Sol", "SystemAddress": 10477373803,
          "StationName": "Abraham Lincoln", "MarketID": 128666762, "ActiveFine": true }
        """);
        state.Observe(docked);

        var msg = t.Transform(docked, state);

        Assert.NotNull(msg);
        var message = msg!.Envelope["message"]!;
        var pos = message["StarPos"]!.AsArray();
        Assert.Equal(3, pos.Count);
        Assert.Equal(1.0, (double)pos[0]!);
        Assert.Null(message["ActiveFine"]);   // private field stripped
    }

    [Fact]
    public void Event_in_different_system_without_coordinates_is_dropped()
    {
        var (t, state) = NewPair();
        state.Observe(Json("""
        { "timestamp": "2020-01-01T00:00:00Z", "event": "FSDJump",
          "StarSystem": "Sol", "SystemAddress": 1, "StarPos": [0.0, 0.0, 0.0] }
        """));

        // A scan tagged with a different system but no coordinates: we can't know its StarPos, so
        // borrowing the previous system's would be wrong — the message must be dropped.
        var scan = Json("""
        { "timestamp": "2020-01-01T00:01:00Z", "event": "Scan",
          "StarSystem": "Alpha Centauri", "SystemAddress": 2, "BodyName": "AC 1" }
        """);
        state.Observe(scan);

        Assert.Null(t.Transform(scan, state));
    }

    [Fact]
    public void Non_whitelisted_event_produces_no_message()
    {
        var (t, state) = NewPair();
        var raw = Json("""{ "timestamp": "2020-01-01T00:00:00Z", "event": "Music", "MusicTrack": "MainMenu" }""");
        state.Observe(raw);
        Assert.Null(t.Transform(raw, state));
    }

    [Fact]
    public void Market_event_becomes_commodity_message_with_cleaned_names()
    {
        var (t, state) = NewPair();
        var raw = Json("""
        {
          "timestamp": "2020-01-01T00:00:00Z", "event": "Market",
          "MarketID": 128666762, "StationName": "Abraham Lincoln", "StarSystem": "Sol",
          "Items": [
            { "id": 1, "Name": "$gold_name;", "Name_Localised": "Gold",
              "BuyPrice": 100, "SellPrice": 90, "MeanPrice": 95,
              "StockBracket": 2, "DemandBracket": 0, "Stock": 10, "Demand": 0 }
          ]
        }
        """);
        state.Observe(raw);

        var msg = t.Transform(raw, state);

        Assert.NotNull(msg);
        Assert.Equal(EddnSchemas.Commodity, msg!.SchemaRef);
        var commodities = msg.Envelope["message"]!["commodities"]!.AsArray();
        Assert.Single(commodities);
        Assert.Equal("gold", (string?)commodities[0]!["name"]);
        Assert.Equal(100L, (long)commodities[0]!["buyPrice"]!);
        Assert.DoesNotContain("_Localised", msg.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Header_carries_software_identity_and_uploader()
    {
        var (t, state) = NewPair();
        state.Observe(Json("""{ "timestamp": "2020-01-01T00:00:00Z", "event": "Commander", "Name": "Jameson" }"""));
        var raw = Json("""
        { "timestamp": "2020-01-01T00:00:00Z", "event": "FSDJump",
          "StarSystem": "Sol", "SystemAddress": 1, "StarPos": [0.0, 0.0, 0.0] }
        """);
        state.Observe(raw);

        var header = t.Transform(raw, state)!.Envelope["header"]!;

        Assert.Equal("Jameson", (string?)header["uploaderID"]);
        Assert.Equal("EDNexus.Tests", (string?)header["softwareName"]);
        Assert.Equal("1.0.0", (string?)header["softwareVersion"]);
    }
}
