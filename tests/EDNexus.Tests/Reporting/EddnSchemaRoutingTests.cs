using System.Text.Json;
using System.Text.Json.Nodes;
using EliteDangerous.Eddn;
using Xunit;

namespace EDNexus.Tests.Reporting;

/// <summary>
/// Event routing and per-schema shaping: each uploadable event must land on the schema that accepts it
/// (the journal schema's <c>event</c> enum rejects everything else) with only the keys that schema allows.
/// </summary>
public class EddnSchemaRoutingTests
{
    private static readonly EddnClientOptions Options = new() { SoftwareName = "EDNexus.Tests", SoftwareVersion = "1.0.0" };

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private const string InSol = """
        { "timestamp": "2026-01-01T00:00:00Z", "event": "FSDJump", "StarSystem": "Sol",
          "SystemAddress": 10477373803, "StarPos": [0.0, 0.0, 0.0] }
        """;

    private static (EddnJournalTransformer T, EddnState S) InSolPair(bool odyssey = true)
    {
        var state = new EddnState { Horizons = true, Odyssey = odyssey };
        state.Observe(Json(InSol));
        return (new EddnJournalTransformer(Options), state);
    }

    private static JsonObject Body(EddnMessage msg) => msg.Envelope["message"]!.AsObject();

    [Fact]
    public void Journal_whitelist_only_contains_events_the_journal_schema_accepts()
    {
        // journal/1's "event" enum: Docked, FSDJump, Scan, Location, SAASignalsFound, CarrierJump, CodexEntry.
        Assert.Equal(
            new[] { "CarrierJump", "Docked", "FSDJump", "Location", "SAASignalsFound", "Scan" },
            EddnSchemas.JournalWhitelist.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void CodexEntry_uses_its_own_schema_and_drops_the_disallowed_keys()
    {
        var (t, state) = InSolPair();
        var raw = Json("""
            { "timestamp": "2026-01-01T00:01:00Z", "event": "CodexEntry", "EntryID": 1100001,
              "Name": "$Codex_Ent_Cactoid_01_Name;", "Name_Localised": "Cactoid", "SubCategory": "$Codex_SubCategory_Organic_Structures;",
              "SubCategory_Localised": "Organic structures", "Category": "$Codex_Category_Biology;", "Region": "$Codex_RegionName_18;",
              "System": "Sol", "SystemAddress": 10477373803, "VoucherAmount": 2500,
              "IsNewEntry": true, "NewTraitsDiscovered": true, "Traits": ["a"], "Latitude": 1.5, "Longitude": 2.5 }
            """);

        var msg = t.Transform(raw, state);

        Assert.NotNull(msg);
        Assert.Equal(EddnSchemas.CodexEntry, msg!.SchemaRef);
        var body = Body(msg);
        Assert.Equal("Sol", (string?)body["System"]);
        Assert.Null(body["StarSystem"]);
        Assert.NotNull(body["StarPos"]);
        Assert.Equal(1100001L, (long)body["EntryID"]!);
        Assert.Equal(2500L, (long)body["VoucherAmount"]!);
        foreach (var gone in new[] { "IsNewEntry", "NewTraitsDiscovered", "Latitude", "Longitude" })
            Assert.Null(body[gone]);
        Assert.DoesNotContain("_Localised", msg.ToString(), StringComparison.Ordinal);
        Assert.True((bool)body["odyssey"]!);
    }

    [Fact]
    public void CodexEntry_without_system_name_is_filled_in_from_the_tracked_location()
    {
        var (t, state) = InSolPair();
        var raw = Json("""{ "timestamp": "2026-01-01T00:01:00Z", "event": "CodexEntry", "EntryID": 7, "System": "", "SystemAddress": 10477373803 }""");

        var msg = t.Transform(raw, state);

        Assert.Equal("Sol", (string?)Body(msg!)["System"]);
    }

    [Fact]
    public void ApproachSettlement_keeps_latitude_and_longitude_on_its_own_schema()
    {
        var (t, state) = InSolPair();
        var raw = Json("""
            { "timestamp": "2026-01-01T00:02:00Z", "event": "ApproachSettlement", "Name": "Hutton Orbital", "Name_Localised": "x",
              "MarketID": 128016384, "SystemAddress": 10477373803, "BodyID": 8, "BodyName": "Earth", "Latitude": 59.97, "Longitude": -84.5,
              "StationFaction": { "Name": "Foo", "FactionState": "Boom" }, "StationEconomies": [ { "Name": "$economy_Industrial;", "Name_Localised": "Industrial", "Proportion": 1.0 } ] }
            """);

        var msg = t.Transform(raw, state);

        Assert.NotNull(msg);
        Assert.Equal(EddnSchemas.ApproachSettlement, msg!.SchemaRef);
        var body = Body(msg);
        Assert.Equal(59.97, (double)body["Latitude"]!, 3);
        Assert.Equal(-84.5, (double)body["Longitude"]!, 3);
        Assert.Equal("Sol", (string?)body["StarSystem"]);
        Assert.Equal(128016384L, (long)body["MarketID"]!);
        Assert.DoesNotContain("_Localised", msg.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ApproachSettlement_without_coordinates_is_dropped()
    {
        var (t, state) = InSolPair();
        var raw = Json("""{ "timestamp": "2026-01-01T00:02:00Z", "event": "ApproachSettlement", "Name": "Beacon", "SystemAddress": 10477373803, "BodyID": 8, "BodyName": "Earth" }""");

        Assert.Null(t.Transform(raw, state));
    }

    [Fact]
    public void FssDiscoveryScan_drops_progress_and_gets_starpos()
    {
        var (t, state) = InSolPair();
        var raw = Json("""{ "timestamp": "2026-01-01T00:03:00Z", "event": "FSSDiscoveryScan", "Progress": 0.4, "BodyCount": 12, "NonBodyCount": 3, "SystemName": "Sol", "SystemAddress": 10477373803 }""");

        var msg = t.Transform(raw, state);

        Assert.Equal(EddnSchemas.FssDiscoveryScan, msg!.SchemaRef);
        var body = Body(msg);
        Assert.Null(body["Progress"]);
        Assert.Equal("Sol", (string?)body["SystemName"]);
        Assert.Equal(3, body["StarPos"]!.AsArray().Count);
        Assert.Equal(12L, (long)body["BodyCount"]!);
    }

    [Fact]
    public void Other_dedicated_schema_events_are_routed_with_location_augmentation()
    {
        var (t, state) = InSolPair();

        var all = t.Transform(Json("""{ "timestamp": "2026-01-01T00:04:00Z", "event": "FSSAllBodiesFound", "SystemName": "Sol", "SystemAddress": 10477373803, "Count": 9 }"""), state);
        var sig = t.Transform(Json("""{ "timestamp": "2026-01-01T00:04:01Z", "event": "FSSBodySignals", "BodyName": "Earth", "BodyID": 3, "SystemAddress": 10477373803, "Signals": [ { "Type": "$SAA_SignalType_Biological;", "Type_Localised": "Bio", "Count": 2 } ] }"""), state);
        var beacon = t.Transform(Json("""{ "timestamp": "2026-01-01T00:04:02Z", "event": "NavBeaconScan", "SystemAddress": 10477373803, "NumBodies": 5 }"""), state);
        var bary = t.Transform(Json("""{ "timestamp": "2026-01-01T00:04:03Z", "event": "ScanBaryCentre", "StarSystem": "Sol", "SystemAddress": 10477373803, "BodyID": 1, "SemiMajorAxis": 1.5, "Eccentricity": 0.1, "Junk": 1 }"""), state);

        Assert.Equal(EddnSchemas.FssAllBodiesFound, all!.SchemaRef);
        Assert.Equal(9L, (long)Body(all)["Count"]!);
        Assert.Equal(EddnSchemas.FssBodySignals, sig!.SchemaRef);
        Assert.Equal("Sol", (string?)Body(sig)["StarSystem"]);
        Assert.DoesNotContain("_Localised", sig.ToString(), StringComparison.Ordinal);
        Assert.Equal(EddnSchemas.NavBeaconScan, beacon!.SchemaRef);
        Assert.Equal(5L, (long)Body(beacon)["NumBodies"]!);
        Assert.Equal(EddnSchemas.ScanBaryCentre, bary!.SchemaRef);
        Assert.Null(Body(bary)["Junk"]);   // these schemas reject unknown keys
    }

    [Fact]
    public void Dedicated_schema_event_in_a_different_system_than_tracked_is_dropped()
    {
        var (t, state) = InSolPair();
        var raw = Json("""{ "timestamp": "2026-01-01T00:04:02Z", "event": "NavBeaconScan", "SystemAddress": 999, "NumBodies": 5 }""");

        Assert.Null(t.Transform(raw, state));
    }

    // --- FSSSignalDiscovered batching ---

    private static JsonElement Signal(string extra, string ts = "2026-01-01T00:05:00Z", long address = 10477373803)
        => Json($$"""{ "timestamp": "{{ts}}", "event": "FSSSignalDiscovered", "SystemAddress": {{address}}, {{extra}} }""");

    [Fact]
    public void Fss_signals_are_coalesced_filtered_and_stripped()
    {
        var (t, state) = InSolPair();
        var events = new[]
        {
            Signal("""
                "SignalName": "$USS_NonHumanSignalSource;", "SignalName_Localised": "x", "USSType": "$USS_Type_NonHuman;", "SpawningState": "$FactionState_None;",
                "SpawningFaction": "$faction_none;", "ThreatLevel": 5, "TimeRemaining": 600.0
                """, "2026-01-01T00:05:00Z"),
            Signal(""" "SignalName": "$USS_MissionTarget", "USSType": "$USS_Type_MissionTarget;", "TimeRemaining": 100.0 """, "2026-01-01T00:05:01Z"),
            Signal(""" "SignalName": "WRONG SYSTEM" """, "2026-01-01T00:05:02Z", address: 42),
            Signal(""" "SignalName": "EXPLORER-CLASS X2X-74M", "IsStation": true """, "2026-01-01T00:05:03Z"),
        };

        var msg = t.TransformFssSignals(events, state);

        Assert.NotNull(msg);
        Assert.Equal(EddnSchemas.FssSignalDiscovered, msg!.SchemaRef);
        var body = Body(msg);
        Assert.Equal("2026-01-01T00:05:00Z", (string?)body["timestamp"]);
        Assert.Equal("Sol", (string?)body["StarSystem"]);
        Assert.Equal(10477373803L, (long)body["SystemAddress"]!);
        var signals = body["signals"]!.AsArray();
        Assert.Equal(2, signals.Count);   // mission target and the wrong-system signal are gone
        var first = signals[0]!.AsObject();
        Assert.Equal("$USS_NonHumanSignalSource;", (string?)first["SignalName"]);
        Assert.Equal("$FactionState_None;", (string?)first["SpawningState"]);   // allowed by the schema
        Assert.Equal(5L, (long)first["ThreatLevel"]!);
        Assert.Null(first["TimeRemaining"]);
        Assert.Null(first["event"]);
        Assert.Null(first["SystemAddress"]);
        Assert.NotNull(first["timestamp"]);
        Assert.True((bool)signals[1]!["IsStation"]!);
        Assert.DoesNotContain("_Localised", msg.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fss_signals_that_all_get_filtered_produce_no_message()
    {
        var (t, state) = InSolPair();
        var events = new[] { Signal(""" "SignalName": "x", "USSType": "$USS_Type_MissionTarget;" """) };

        Assert.Null(t.TransformFssSignals(events, state));
    }

    [Fact]
    public void A_lone_fss_signal_via_Transform_becomes_a_one_signal_message()
    {
        var (t, state) = InSolPair();

        var msg = t.Transform(Signal(""" "SignalName": "EXPLORER-CLASS X2X-74M", "IsStation": true """), state);

        Assert.Equal(EddnSchemas.FssSignalDiscovered, msg!.SchemaRef);
        Assert.Single(Body(msg)["signals"]!.AsArray());
    }

    // --- NavRoute / FCMaterials sidecars ---

    [Fact]
    public void Bare_navroute_journal_line_is_not_uploaded()
    {
        var (t, state) = InSolPair();

        Assert.Null(t.Transform(Json("""{ "timestamp": "2026-01-01T00:06:00Z", "event": "NavRoute" }"""), state));
    }

    [Fact]
    public void Empty_navroute_is_not_uploaded()
    {
        var (t, state) = InSolPair();

        Assert.Null(t.Transform(Json("""{ "timestamp": "2026-01-01T00:06:00Z", "event": "NavRoute", "Route": [] }"""), state));
    }

    [Fact]
    public void Navroute_sidecar_is_uploaded_with_only_the_schema_keys_per_hop()
    {
        var (t, state) = InSolPair();
        var raw = Json("""
            { "timestamp": "2026-01-01T00:06:00Z", "event": "NavRoute", "Route": [
                { "StarSystem": "Sol", "SystemAddress": 10477373803, "StarPos": [0.0, 0.0, 0.0], "StarClass": "G", "Extra": 1 },
                { "StarSystem": "Alpha Centauri", "SystemAddress": 2, "StarPos": [3.03, -0.09, 3.16], "StarClass": "K" },
                { "StarSystem": "Broken" } ] }
            """);

        var msg = t.Transform(raw, state);

        Assert.Equal(EddnSchemas.NavRoute, msg!.SchemaRef);
        var route = Body(msg)["Route"]!.AsArray();
        Assert.Equal(2, route.Count);   // the incomplete hop is dropped
        Assert.Null(route[0]!["Extra"]);
        Assert.Equal("K", (string?)route[1]!["StarClass"]);
    }

    [Fact]
    public void Bare_fcmaterials_journal_line_is_not_uploaded()
    {
        var (t, state) = InSolPair();

        Assert.Null(t.Transform(Json("""{ "timestamp": "2026-01-01T00:07:00Z", "event": "FCMaterials", "MarketID": 1, "CarrierName": "X", "CarrierID": "ABC" }"""), state));
    }

    [Fact]
    public void Fcmaterials_sidecar_is_uploaded_without_localised_strings()
    {
        var (t, state) = InSolPair();
        var raw = Json("""
            { "timestamp": "2026-01-01T00:07:00Z", "event": "FCMaterials", "MarketID": 3700005632, "CarrierName": "Test Carrier", "CarrierID": "K7X-12Q",
              "Items": [ { "id": 128961532, "Name": "$icrpolymers_name;", "Name_Localised": "Polymers", "Price": 100, "Stock": 5, "Demand": 0 },
                         { "id": 1, "Name": "bad" } ] }
            """);

        var msg = t.Transform(raw, state);

        Assert.Equal(EddnSchemas.FcMaterialsJournal, msg!.SchemaRef);
        Assert.Single(Body(msg)["Items"]!.AsArray());
        Assert.DoesNotContain("_Localised", msg.ToString(), StringComparison.Ordinal);
    }

    // --- Station schemas ---

    [Fact]
    public void Market_skips_nonmarketable_items_and_station_schemas_carry_game_flags()
    {
        var (t, state) = InSolPair(odyssey: false);

        var market = t.Transform(Json("""
            { "timestamp": "2026-01-01T00:08:00Z", "event": "Market", "MarketID": 1, "StationName": "Lincoln", "StarSystem": "Sol",
              "Items": [
                { "Name": "$gold_name;", "Category": "$MARKET_category_metals;", "BuyPrice": 1, "SellPrice": 1, "MeanPrice": 1, "StockBracket": 1, "DemandBracket": 1, "Stock": 1, "Demand": 1 },
                { "Name": "$drones_name;", "Category": "$MARKET_category_NonMarketable;", "BuyPrice": 1, "SellPrice": 1, "MeanPrice": 1, "StockBracket": 1, "DemandBracket": 1, "Stock": 1, "Demand": 1 } ] }
            """), state);
        var outfitting = t.Transform(Json("""{ "timestamp": "2026-01-01T00:08:00Z", "event": "Outfitting", "MarketID": 1, "StationName": "Lincoln", "StarSystem": "Sol", "Items": [ { "Name": "hpt_pulselaser_fixed_small" } ] }"""), state);
        var shipyard = t.Transform(Json("""{ "timestamp": "2026-01-01T00:08:00Z", "event": "Shipyard", "MarketID": 1, "StationName": "Lincoln", "StarSystem": "Sol", "PriceList": [ { "ShipType": "Sidewinder" } ] }"""), state);

        Assert.Single(Body(market!)["commodities"]!.AsArray());
        foreach (var msg in new[] { market!, outfitting!, shipyard! })
        {
            Assert.True((bool)Body(msg)["horizons"]!);
            Assert.False((bool)Body(msg)["odyssey"]!);
        }
    }

    // --- Defensive parsing ---

    [Theory]
    [InlineData("\"StarSystem\": 5")]
    [InlineData("\"SystemAddress\": \"abc\"")]
    [InlineData("\"StarPos\": \"x\"")]
    [InlineData("\"StarPos\": [1, \"a\", 3]")]
    public void Wrong_typed_location_fields_drop_the_event_instead_of_throwing(string badField)
    {
        var (t, state) = InSolPair();
        var raw = Json($$"""{ "timestamp": "2026-01-01T00:09:00Z", "event": "Scan", "BodyName": "x", {{badField}} }""");

        Assert.Null(t.Transform(raw, state));
    }
}
