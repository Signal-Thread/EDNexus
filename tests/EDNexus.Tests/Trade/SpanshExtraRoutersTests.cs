using System;
using System.Net;
using System.Net.Http;
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

    [Fact]
    public async Task Survey_missing_result_is_an_empty_route()
    {
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : """{ "status": "ok" }"""));
        using var client = new SpanshClient(Options(), new HttpClient(handler));

        var result = await client.PlotExobiologyRouteAsync(Survey);

        Assert.True(result.IsOk);
        Assert.Empty(result.Systems);
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
            { "system_name": "Beta" },
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
        Assert.Equal(2, result.Bodies.Count);
        Assert.Equal("Alpha", result.Bodies[0].SystemName);
        Assert.Equal(12.3, result.Bodies[0].DistanceLy);
        Assert.Equal("Unknown", result.Bodies[1].Name);
        Assert.Contains("/bodies/search", handler.Uris[0]!.ToString());
        Assert.Contains("Ammonia world", handler.Bodies[0]);
        Assert.Contains("\"reference_system\":\"Sol\"", handler.Bodies[0]);
        Assert.Contains("\"size\":5", handler.Bodies[0]);
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
        Assert.Contains("has_large_pad", handler.Bodies[0]);
        Assert.Contains("\"name\":\"Shipyard\"", handler.Bodies[0]);
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
}
