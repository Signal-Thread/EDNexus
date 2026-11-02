using System;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EDNexus.Tests.Reporting;   // shared RecordingHandler test double
using EliteDangerous.Spansh;
using Xunit;

namespace EDNexus.Tests.Trade;

/// <summary>Canned-response tests for the Spansh Road to Riches, exobiology, tourist, body and service endpoints.</summary>
public class SpanshExtraRoutersTests
{
    private static SpanshClientOptions Options(int attempts = 10) => new()
    {
        SoftwareName = "EDNexus.Tests",
        SoftwareVersion = "1.0.0",
        RoutePollInterval = TimeSpan.Zero,
        TransientRetryDelay = TimeSpan.Zero,
        RoutePollAttempts = attempts,
    };

    private const string Submit = """{ "job": "job-9", "status": "queued" }""";
    private const string Queued = """{ "status": "queued" }""";

    private const string Riches = """
    { "status": "ok", "result": [
        { "name": "Sol", "jumps": 0, "distance_jumped": 0, "bodies": [] },
        { "name": "Barnard's Star", "jumps": 2, "distance_jumped": 12.5, "bodies": [
            { "name": "Barnard's Star A 1", "subtype": "Earth-like world", "distance_to_arrival": 1200.5,
              "estimated_scan_value": 500000, "estimated_mapping_value": 3200000, "is_terraformable": false },
            { "name": "Barnard's Star A 2", "subtype": "Water world", "is_terraformable": true },
            "garbage"
        ] }
    ] }
    """;

    private static SpanshSurveyRouteQuery Survey => new() { From = "Sol", To = "Colonia", RangeLy = 48.5 };

    [Fact]
    public async Task Road_to_riches_submits_form_polls_and_parses_bodies()
    {
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n switch { 1 => Submit, 2 => Queued, _ => Riches }));
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.PlotRoadToRichesAsync(Survey);

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Systems.Count);
        var barnard = result.Systems[1];
        Assert.Equal("Barnard's Star", barnard.Name);
        Assert.Equal(2, barnard.Bodies.Count);   // the non-object entry is skipped
        Assert.Equal(3_200_000, barnard.Bodies[0].MappingValue);
        Assert.Equal(1200.5, barnard.Bodies[0].DistanceToArrivalLs);
        Assert.True(barnard.Bodies[1].IsTerraformable);
        Assert.Equal(0, barnard.Bodies[1].ScanValue);   // missing field degrades to zero

        Assert.Contains("/riches/route", handler.Uris[0]!.ToString());
        Assert.Contains("use_mapping_value=1", handler.Bodies[0]);
        Assert.Contains("range=48.5", handler.Bodies[0]);
        Assert.Contains("/results/job-9", handler.Uris[1]!.ToString());
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Exobiology_parses_landmarks_and_omits_mapping_flag()
    {
        const string exo = """
        { "status": "ok", "result": [
            { "name": "Eta Carinae", "jumps": 4, "bodies": [
                { "name": "Eta Carinae 3 a", "subtype": "Rocky body", "distance_to_arrival": 50,
                  "landmarks": [ { "type": "Bacterium", "subtype": "Bacterium Aurasus", "count": 2, "value": 1000000 } ] }
            ] }
        ] }
        """;
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : exo));
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.PlotExobiologyRouteAsync(Survey);

        Assert.True(result.IsOk);
        var landmark = Assert.Single(result.Systems[0].Bodies[0].Landmarks);
        Assert.Equal("Bacterium Aurasus", landmark.Subtype);
        Assert.Equal(2, landmark.Count);
        Assert.Equal(1_000_000, landmark.Value);
        Assert.Contains("/exobiology/route", handler.Uris[0]!.ToString());
        Assert.DoesNotContain("use_mapping_value", handler.Bodies[0]);
    }

    [Fact]
    public async Task Survey_job_error_and_failed_submit_are_failures_not_exceptions()
    {
        var errored = new RecordingHandler(n => (HttpStatusCode.OK,
            n == 1 ? Submit : """{ "status": "error", "error": "no systems" }"""));
        using (var client = new SpanshClient(Options(), new HttpClient(errored)))
        {
            var r = await client.PlotRoadToRichesAsync(Survey);
            Assert.False(r.IsOk);
            Assert.Equal("no systems", r.Error);
        }

        using (var client = new SpanshClient(Options(), new HttpClient(new RecordingHandler(HttpStatusCode.InternalServerError, "x"))))
        {
            var r = await client.PlotExobiologyRouteAsync(Survey);
            Assert.False(r.IsOk);
            Assert.Contains("500", r.Error);
        }
    }

    [Fact]
    public async Task Survey_poll_budget_is_bounded()
    {
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : Queued));
        using var client = new SpanshClient(Options(attempts: 3), new HttpClient(handler));

        var result = await client.PlotRoadToRichesAsync(Survey);

        Assert.False(result.IsOk);
        Assert.Equal(4, handler.CallCount);   // one submit + three polls, then give up
    }

    [Fact]
    public async Task Survey_unparseable_poll_is_a_failure()
    {
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : "<html>not json"));
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.PlotRoadToRichesAsync(Survey);

        Assert.False(result.IsOk);
        Assert.Contains("unparseable", result.Error);
    }

    [Theory]
    [InlineData("""{ "status": "ok" }""")]
    [InlineData("""{ "status": "ok", "result": { "stops": [] } }""")]
    public async Task Survey_unrecognised_result_shape_is_a_failure(string ready)
    {
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : ready));
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.PlotExobiologyRouteAsync(Survey);

        Assert.False(result.IsOk);
        Assert.Contains("unrecognised", result.Error);
    }

    [Fact]
    public async Task Survey_present_but_empty_result_is_an_empty_route()
    {
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : """{ "status": "ok", "result": [] }"""));
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.PlotRoadToRichesAsync(Survey);

        Assert.True(result.IsOk);
        Assert.Empty(result.Systems);
    }

    [Fact]
    public async Task Tourist_unrecognised_result_shape_is_a_failure()
    {
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : """{ "status": "ok", "result": "nope" }"""));
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.PlotTouristRouteAsync(new SpanshTouristRouteQuery { From = "A", Systems = new[] { "B" }, RangeLy = 30 });

        Assert.False(result.IsOk);
        Assert.Contains("unrecognised", result.Error);
    }

    [Fact]
    public async Task Tourist_falls_back_to_name_and_distance_fields()
    {
        const string ready = """{ "status": "ok", "result": [ { "name": "Named", "jumps": 1, "distance": 42.5 } ] }""";
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : ready));
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.PlotTouristRouteAsync(new SpanshTouristRouteQuery { From = "A", Systems = new[] { "B" }, RangeLy = 30 });

        var stop = Assert.Single(result.Waypoints);
        Assert.Equal("Named", stop.System);
        Assert.Equal(42.5, stop.DistanceJumpedLy);
    }

    [Fact]
    public async Task Survey_omits_to_when_blank_and_sends_it_when_set()
    {
        var blank = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : Riches));
        using (var client = new SpanshClient(Options(), new HttpClient(blank)))
            await client.PlotRoadToRichesAsync(new SpanshSurveyRouteQuery { From = "Sol", To = "  ", RangeLy = 40 });
        Assert.DoesNotContain("to=", blank.Bodies[0]);

        var set = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : Riches));
        using (var client = new SpanshClient(Options(), new HttpClient(set)))
            await client.PlotRoadToRichesAsync(Survey);
        Assert.Contains("to=Colonia", set.Bodies[0]);
    }

    [Fact]
    public async Task Invalid_job_input_fails_without_a_network_call()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        Assert.False((await client.PlotRoadToRichesAsync(null!)).IsOk);
        Assert.False((await client.PlotExobiologyRouteAsync(new SpanshSurveyRouteQuery { From = " ", RangeLy = 40 })).IsOk);
        Assert.False((await client.PlotRoadToRichesAsync(new SpanshSurveyRouteQuery { From = "Sol", RangeLy = double.NaN })).IsOk);
        Assert.False((await client.PlotRoadToRichesAsync(new SpanshSurveyRouteQuery { From = "Sol", RangeLy = 0 })).IsOk);
        Assert.False((await client.PlotTouristRouteAsync(null!)).IsOk);
        Assert.False((await client.PlotTouristRouteAsync(new SpanshTouristRouteQuery { From = "A", Systems = Array.Empty<string>(), RangeLy = 30 })).IsOk);
        Assert.False((await client.PlotTouristRouteAsync(new SpanshTouristRouteQuery { From = "A", Systems = null!, RangeLy = 30 })).IsOk);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Job_endpoints_rethrow_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var client = new SpanshClient(Options(), new HttpClient(new CancelAwareHandler(Submit)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PlotRoadToRichesAsync(Survey, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PlotExobiologyRouteAsync(Survey, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PlotTouristRouteAsync(
            new SpanshTouristRouteQuery { From = "A", Systems = new[] { "B" }, RangeLy = 30 }, cts.Token));
    }

    [Fact]
    public async Task Search_endpoints_rethrow_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var client = new SpanshClient(Options(), new HttpClient(new CancelAwareHandler("""{ "results": [] }""")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchBodiesAsync(
            new SpanshBodyQuery { ReferenceSystem = "Sol", SubTypes = new[] { "x" } }, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchStationsByServiceAsync(
            new SpanshServiceQuery { ReferenceSystem = "Sol", Services = new[] { "x" } }, cts.Token));
    }

    [Theory]
    [InlineData("""{ "status": "ok", "result": [ { "name": "A", "jumps": 0 }, { "name": "B", "jumps": 3, "distance_jumped": 90, "distance_left": 10 } ] }""")]
    [InlineData("""{ "status": "ok", "result": { "system_jumps": [ { "system": "A", "jumps": 0 }, { "system": "B", "jumps": 3, "distance_jumped": 90, "distance_left": 10 } ] } }""")]
    public async Task Tourist_route_accepts_either_result_shape(string ready)
    {
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : ready));
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.PlotTouristRouteAsync(new SpanshTouristRouteQuery
        {
            From = "A", Systems = new[] { "B", "C" }, RangeLy = 30,
        });

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Waypoints.Count);
        Assert.Equal("B", result.Waypoints[1].System);
        Assert.Equal(3, result.Waypoints[1].Jumps);
        Assert.Equal(90, result.Waypoints[1].DistanceJumpedLy);
        Assert.Contains("/tourist/route", handler.Uris[0]!.ToString());
        Assert.Contains("systems=B%2CC", handler.Bodies[0]);
    }

    [Fact]
    public async Task Body_search_sends_filters_and_parses_results()
    {
        const string reply = """
        { "results": [
            { "name": "Alpha 1", "system_name": "Alpha", "subtype": "Ammonia world", "distance": 12.3, "distance_to_arrival": 800, "is_landable": false },
            { "system_name": "Beta", "subtype": "ammonia WORLD" },
            { "name": "Mislabelled", "system_name": "Gamma", "subtype": "Water world" },
            42
        ] }
        """;
        var handler = new RecordingHandler(HttpStatusCode.OK, reply);
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.SearchBodiesAsync(new SpanshBodyQuery
        {
            ReferenceSystem = "Sol", SubTypes = new[] { SpanshBodyTypes.Ammonia }, Landable = false, MaxResults = 5,
        });

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Bodies.Count);   // the Water world row is filtered out client-side (case-insensitive match kept)
        Assert.Equal("Alpha", result.Bodies[0].SystemName);
        Assert.Equal(12.3, result.Bodies[0].DistanceLy);
        Assert.Equal("Unknown", result.Bodies[1].Name);
        Assert.Contains("/bodies/search", handler.Uris[0]!.ToString());

        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        var root = sent.RootElement;
        Assert.Equal("Ammonia world", root.GetProperty("filters").GetProperty("subtype").GetProperty("value")[0].GetString());
        Assert.False(root.GetProperty("filters").GetProperty("is_landable").GetProperty("value").GetBoolean());
        Assert.Equal("Sol", root.GetProperty("reference_system").GetString());
        Assert.Equal(5, root.GetProperty("size").GetInt32());
    }

    [Fact]
    public async Task Body_search_landable_true_filters_and_omits_filter_when_unset()
    {
        const string reply = """
        { "results": [
            { "name": "Land", "subtype": "Water world", "is_landable": true },
            { "name": "Sea", "subtype": "Water world", "is_landable": false }
        ] }
        """;
        var landable = new RecordingHandler(HttpStatusCode.OK, reply);
        using (var client = new SpanshClient(Options(), new HttpClient(landable)))
        {
            var r = await client.SearchBodiesAsync(new SpanshBodyQuery { ReferenceSystem = "Sol", SubTypes = new[] { "Water world" }, Landable = true });
            Assert.Equal("Land", Assert.Single(r.Bodies).Name);
        }
        using (var sent = JsonDocument.Parse(landable.Bodies[0]))
            Assert.True(sent.RootElement.GetProperty("filters").GetProperty("is_landable").GetProperty("value").GetBoolean());

        var any = new RecordingHandler(HttpStatusCode.OK, reply);
        using (var client = new SpanshClient(Options(), new HttpClient(any)))
        {
            var r = await client.SearchBodiesAsync(new SpanshBodyQuery { ReferenceSystem = "Sol", SubTypes = new[] { "Water world" } });
            Assert.Equal(2, r.Bodies.Count);
        }
        using (var sent = JsonDocument.Parse(any.Bodies[0]))
            Assert.False(sent.RootElement.GetProperty("filters").TryGetProperty("is_landable", out _));
    }

    [Fact]
    public async Task Body_search_fails_when_every_row_is_filtered_out()
    {
        const string reply = """{ "results": [ { "name": "X", "subtype": "Water world" } ] }""";
        using var client = new SpanshClient(Options(), new HttpClient(new RecordingHandler(HttpStatusCode.OK, reply)));

        var r = await client.SearchBodiesAsync(new SpanshBodyQuery { ReferenceSystem = "Sol", SubTypes = new[] { "Ammonia world" } });

        Assert.False(r.IsOk);
        Assert.Contains("none matched", r.Error);
    }

    [Theory]
    [InlineData("""{ "count": 0 }""")]
    [InlineData("""{ "results": {} }""")]
    [InlineData("""[]""")]
    public async Task Searches_with_unrecognised_shape_fail_but_empty_array_is_ok(string reply)
    {
        using var client = new SpanshClient(Options(), new HttpClient(new RecordingHandler(HttpStatusCode.OK, reply)));

        Assert.False((await client.SearchBodiesAsync(new SpanshBodyQuery { ReferenceSystem = "Sol", SubTypes = new[] { "x" } })).IsOk);
        Assert.False((await client.SearchStationsByServiceAsync(new SpanshServiceQuery { ReferenceSystem = "Sol", Services = new[] { "x" } })).IsOk);

        using var empty = new SpanshClient(Options(), new HttpClient(new RecordingHandler(HttpStatusCode.OK, """{ "results": [] }""")));
        Assert.True((await empty.SearchBodiesAsync(new SpanshBodyQuery { ReferenceSystem = "Sol", SubTypes = new[] { "x" } })).IsOk);
        Assert.True((await empty.SearchStationsByServiceAsync(new SpanshServiceQuery { ReferenceSystem = "Sol", Services = new[] { "x" } })).IsOk);
    }

    [Fact]
    public async Task Searches_with_invalid_input_return_failure_without_throwing()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "results": [] }""");
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        Assert.False((await client.SearchBodiesAsync(null!)).IsOk);
        Assert.False((await client.SearchBodiesAsync(new SpanshBodyQuery { ReferenceSystem = "Sol", SubTypes = null! })).IsOk);
        Assert.False((await client.SearchBodiesAsync(new SpanshBodyQuery { ReferenceSystem = "Sol", SubTypes = Array.Empty<string>() })).IsOk);
        Assert.False((await client.SearchBodiesAsync(new SpanshBodyQuery { ReferenceSystem = " ", SubTypes = new[] { "x" } })).IsOk);
        Assert.False((await client.SearchStationsByServiceAsync(null!)).IsOk);
        Assert.False((await client.SearchStationsByServiceAsync(new SpanshServiceQuery { ReferenceSystem = "Sol", Services = null! })).IsOk);
        Assert.False((await client.SearchStationsByServiceAsync(new SpanshServiceQuery { ReferenceSystem = "Sol", Services = Array.Empty<string>() })).IsOk);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1000, 100)]
    public async Task Search_size_is_clamped(int requested, int expected)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "results": [] }""");
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        await client.SearchBodiesAsync(new SpanshBodyQuery { ReferenceSystem = "Sol", SubTypes = new[] { "x" }, MaxResults = requested });

        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(expected, sent.RootElement.GetProperty("size").GetInt32());
        Assert.Equal(0, sent.RootElement.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Service_search_requires_services_and_parses_station_services()
    {
        const string reply = """
        { "results": [
            { "name": "Jameson Memorial", "system_name": "Shinrarta Dezhra", "type": "Orbis Starport",
              "distance": 0, "distance_to_arrival": 360, "has_large_pad": true,
              "services": [ { "name": "Shipyard" }, { "name": "Outfitting" }, "Refuel" ] }
        ] }
        """;
        var handler = new RecordingHandler(HttpStatusCode.OK, reply);
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.SearchStationsByServiceAsync(new SpanshServiceQuery
        {
            ReferenceSystem = "Sol", Services = new[] { SpanshServices.Shipyard }, RequireLargePad = true,
        });

        Assert.True(result.IsOk);
        var station = Assert.Single(result.Stations);
        Assert.Equal("Jameson Memorial", station.StationName);
        Assert.True(station.HasLargePad);
        Assert.Equal(new[] { "Shipyard", "Outfitting", "Refuel" }, station.Services);
        Assert.Contains("/stations/search", handler.Uris[0]!.ToString());

        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        var filters = sent.RootElement.GetProperty("filters");
        Assert.True(filters.GetProperty("has_large_pad").GetProperty("value").GetBoolean());
        Assert.Equal("Shipyard", filters.GetProperty("services")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Service_search_without_large_pad_requirement_omits_filter_and_keeps_small_pad_stations()
    {
        const string reply = """
        { "results": [ { "name": "Outpost", "system_name": "Sol", "has_large_pad": false, "large_pads": 0, "services": [ "Shipyard" ] } ] }
        """;
        var handler = new RecordingHandler(HttpStatusCode.OK, reply);
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.SearchStationsByServiceAsync(new SpanshServiceQuery
        {
            ReferenceSystem = "Sol", Services = new[] { SpanshServices.Shipyard }, RequireLargePad = false,
        });

        Assert.Equal("Outpost", Assert.Single(result.Stations).StationName);
        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        Assert.False(sent.RootElement.GetProperty("filters").TryGetProperty("has_large_pad", out _));
    }

    [Fact]
    public async Task Service_search_filters_client_side_on_services_and_large_pad()
    {
        const string reply = """
        { "results": [
            { "name": "Good", "has_large_pad": true, "services": [ { "name": "Shipyard" } ] },
            { "name": "NoShipyard", "has_large_pad": true, "services": [ { "name": "Refuel" } ] },
            { "name": "Small", "has_large_pad": false, "large_pads": 0, "services": [ { "name": "Shipyard" } ] },
            { "name": "FlagOnly", "has_large_pad": true, "has_shipyard": true }
        ] }
        """;
        using var client = new SpanshClient(Options(), new HttpClient(new RecordingHandler(HttpStatusCode.OK, reply)));

        var result = await client.SearchStationsByServiceAsync(new SpanshServiceQuery
        {
            ReferenceSystem = "Sol", Services = new[] { "shipyard" }, RequireLargePad = true,
        });

        Assert.Equal(new[] { "Good", "FlagOnly" }, result.Stations.Select(s => s.StationName).ToArray());
    }

    [Fact]
    public async Task Service_search_fails_when_every_row_is_filtered_out()
    {
        const string reply = """{ "results": [ { "name": "A", "services": [ "Refuel" ] } ] }""";
        using var client = new SpanshClient(Options(), new HttpClient(new RecordingHandler(HttpStatusCode.OK, reply)));

        var r = await client.SearchStationsByServiceAsync(new SpanshServiceQuery { ReferenceSystem = "Sol", Services = new[] { "Shipyard" } });

        Assert.False(r.IsOk);
        Assert.Contains("none matched", r.Error);
    }

    [Fact]
    public async Task Survey_huge_value_is_clamped_not_wrapped()
    {
        const string ready = """{ "status": "ok", "result": [ { "name": "S", "bodies": [ { "name": "B", "estimated_scan_value": 1e30, "estimated_mapping_value": -1e30 } ] } ] }""";
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : ready));
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.PlotRoadToRichesAsync(Survey);

        var body = result.Systems[0].Bodies[0];
        Assert.Equal(long.MaxValue, body.ScanValue);
        Assert.Equal(long.MinValue, body.MappingValue);
    }

    [Fact]
    public async Task Searches_never_throw_on_http_or_parse_failure()
    {
        using (var client = new SpanshClient(Options(), new HttpClient(new RecordingHandler(HttpStatusCode.BadGateway, "x"))))
        {
            var bodies = await client.SearchBodiesAsync(new SpanshBodyQuery { ReferenceSystem = "Sol", SubTypes = new[] { "x" } });
            Assert.False(bodies.IsOk);
            Assert.Contains("502", bodies.Error);
        }

        using (var client = new SpanshClient(Options(), new HttpClient(new RecordingHandler(HttpStatusCode.OK, "not json"))))
        {
            var stations = await client.SearchStationsByServiceAsync(new SpanshServiceQuery { ReferenceSystem = "Sol", Services = new[] { "x" } });
            Assert.False(stations.IsOk);
            Assert.Contains("unparseable", stations.Error);
        }
    }

    /// <summary>A handler that, unlike the shared recorder, honours an already-cancelled token.</summary>
    private sealed class CancelAwareHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
