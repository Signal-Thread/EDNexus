using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using EDNexus.Core.Navigation;
using EDNexus.Tests.Reporting;   // reuse the shared RecordingHandler test double
using EliteDangerous.Edsm;
using EliteDangerous.Galnet;
using EliteDangerous.RavenColonial;
using EliteDangerous.Spansh;
using Xunit;

namespace EDNexus.Tests.Integrations;

/// <summary>Transient-failure, rate-limit and malformed-input handling shared by the read-side API clients.</summary>
public class ClientHardeningTests
{
    private sealed class FuncHandler(Func<int, HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<Uri?> Uris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Uris) Uris.Add(request.RequestUri);
            return respond(Interlocked.Increment(ref _calls), request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body) };
        if (retryAfter is { } wait) r.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return r;
    }

    private static readonly SpanshClientOptions FastSpansh = new()
    {
        SoftwareName = "T", SoftwareVersion = "1",
        RoutePollInterval = TimeSpan.Zero, TransientRetryDelay = TimeSpan.Zero, RoutePollRetries = 3,
    };

    // ---------------- Spansh ----------------

    private const string Submit = """{ "job": "j1", "status": "queued" }""";
    private const string Ready = """
        { "status": "ok", "result": { "system_jumps": [
            { "system": "Sol", "jumps": 0, "distance_jumped": 0, "distance_left": 400, "neutron_star": false },
            { "system": "Colonia", "jumps": 2, "distance_jumped": 150, "distance_left": 0, "neutron_star": false } ] } }
        """;

    [Fact]
    public async Task A_transient_poll_error_does_not_abort_the_route_job()
    {
        // submit, then 503, then 500, then the finished route.
        var handler = new FuncHandler((n, _) => Task.FromResult(n switch
        {
            1 => Json(HttpStatusCode.OK, Submit),
            2 => Json(HttpStatusCode.ServiceUnavailable, ""),
            3 => Json(HttpStatusCode.InternalServerError, ""),
            _ => Json(HttpStatusCode.OK, Ready),
        }));
        var client = new SpanshClient(FastSpansh, new HttpClient(handler));

        var result = await client.PlotRouteAsync(new SpanshRouteQuery { From = "Sol", To = "Colonia", RangeLy = 48 });

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Waypoints.Count);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task A_network_error_during_polling_is_retried()
    {
        var handler = new FuncHandler((n, _) => n switch
        {
            1 => Task.FromResult(Json(HttpStatusCode.OK, Submit)),
            2 => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset")),
            _ => Task.FromResult(Json(HttpStatusCode.OK, Ready)),
        });
        var client = new SpanshClient(FastSpansh, new HttpClient(handler));

        var result = await client.PlotRouteAsync(new SpanshRouteQuery { From = "Sol", To = "Colonia", RangeLy = 48 });

        Assert.True(result.IsOk);
    }

    [Fact]
    public async Task Poll_retries_are_bounded()
    {
        var handler = new FuncHandler((n, _) => Task.FromResult(n == 1 ? Json(HttpStatusCode.OK, Submit) : Json(HttpStatusCode.BadGateway, "")));
        var client = new SpanshClient(FastSpansh, new HttpClient(handler));

        var result = await client.PlotRouteAsync(new SpanshRouteQuery { From = "Sol", To = "Colonia", RangeLy = 48 });

        Assert.False(result.IsOk);
        Assert.Equal(1 + 4, handler.Calls);   // submit + the first poll + three retries
    }

    [Fact]
    public async Task A_permanent_poll_error_is_not_retried()
    {
        var handler = new FuncHandler((n, _) => Task.FromResult(n == 1 ? Json(HttpStatusCode.OK, Submit) : Json(HttpStatusCode.NotFound, "")));
        var client = new SpanshClient(FastSpansh, new HttpClient(handler));

        var result = await client.PlotRouteAsync(new SpanshRouteQuery { From = "Sol", To = "Colonia", RangeLy = 48 });

        Assert.False(result.IsOk);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task An_HttpClient_timeout_during_polling_is_a_failure_not_a_throw()
    {
        var handler = new FuncHandler((n, _) => n == 1
            ? Task.FromResult(Json(HttpStatusCode.OK, Submit))
            : Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out")));   // as HttpClient.Timeout surfaces
        var client = new SpanshClient(FastSpansh, new HttpClient(handler));

        var result = await client.PlotRouteAsync(new SpanshRouteQuery { From = "Sol", To = "Colonia", RangeLy = 48 });

        Assert.False(result.IsOk);
    }

    [Fact]
    public async Task A_dot_segment_job_id_is_refused()
    {
        var handler = new RecordingHandler(body: """{ "job": "..", "status": "queued" }""");
        var client = new SpanshClient(FastSpansh, new HttpClient(handler));

        var result = await client.PlotRouteAsync(new SpanshRouteQuery { From = "Sol", To = "Colonia", RangeLy = 48 });

        Assert.False(result.IsOk);
        Assert.Equal(1, handler.CallCount);   // never polled
    }

    [Fact]
    public async Task Station_search_retries_once_after_a_429_with_retry_after()
    {
        var handler = new FuncHandler((n, _) => Task.FromResult(n == 1
            ? Json((HttpStatusCode)429, "", TimeSpan.FromMilliseconds(10))
            : Json(HttpStatusCode.OK, """{ "results": [] }""")));
        var client = new SpanshClient(FastSpansh, new HttpClient(handler));

        var result = await client.SearchStationsAsync(new SpanshStationQuery { CommodityName = "gold", ReferenceSystem = "Sol" });

        Assert.True(result.IsOk);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_retry_after_beyond_the_cap_is_not_waited_for()
    {
        var handler = new FuncHandler((_, _) => Task.FromResult(Json((HttpStatusCode)429, "", TimeSpan.FromHours(1))));
        var client = new SpanshClient(FastSpansh, new HttpClient(handler));

        var sw = Stopwatch.StartNew();
        var result = await client.SearchStationsAsync(new SpanshStationQuery { CommodityName = "gold", ReferenceSystem = "Sol" });

        Assert.False(result.IsOk);
        Assert.Equal(1, handler.Calls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    // ---------------- EDSM ----------------

    private static readonly EdsmClientOptions FastEdsm = new()
    {
        SoftwareName = "T", SoftwareVersion = "1", TransientRetryDelay = TimeSpan.Zero,
    };

    [Fact]
    public async Task Edsm_partial_coordinates_are_null_not_zero()
    {
        var handler = new RecordingHandler(body: """{ "name": "Partial", "coords": { "x": 5.5, "y": 6.5 } }""");
        var client = new EdsmClient(FastEdsm, new HttpClient(handler));

        var result = await client.GetSystemAsync("Partial");

        Assert.True(result.IsOk);
        Assert.Equal("Partial", result.Value!.Name);
        Assert.Null(result.Value.Coords);
    }

    [Fact]
    public async Task Edsm_full_coordinates_are_read()
    {
        var handler = new RecordingHandler(body: """{ "name": "Origin", "coords": { "x": 0, "y": 0, "z": 0 } }""");
        var client = new EdsmClient(FastEdsm, new HttpClient(handler));

        var result = await client.GetSystemAsync("Origin");

        Assert.Equal(new EdsmCoords(0, 0, 0), result.Value!.Coords);   // a genuine zero origin still works
    }

    [Fact]
    public async Task Edsm_nan_radius_does_not_reach_the_query_string()
    {
        var handler = new RecordingHandler(body: "[]");
        var client = new EdsmClient(FastEdsm, new HttpClient(handler));

        await client.GetNearbySystemsAsync("Sol", double.NaN);

        Assert.DoesNotContain("NaN", handler.Uris[0]!.ToString());
        Assert.Contains("radius=0", handler.Uris[0]!.ToString());
    }

    [Fact]
    public async Task Edsm_429_with_retry_after_is_retried_once()
    {
        var handler = new FuncHandler((n, _) => Task.FromResult(n == 1
            ? Json((HttpStatusCode)429, "", TimeSpan.FromMilliseconds(10))
            : Json(HttpStatusCode.OK, """{ "name": "Sol", "coords": { "x": 0, "y": 0, "z": 0 } }""")));
        var client = new EdsmClient(FastEdsm, new HttpClient(handler));

        var result = await client.GetSystemAsync("Sol");

        Assert.True(result.IsOk);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Edsm_still_busy_after_the_retry_is_a_failure()
    {
        var handler = new FuncHandler((_, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "")));
        var client = new EdsmClient(FastEdsm, new HttpClient(handler));

        var result = await client.GetSystemAsync("Sol");

        Assert.False(result.IsOk);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Edsm_timeout_is_a_failure_not_a_throw()
    {
        var handler = new FuncHandler((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out")));
        var client = new EdsmClient(FastEdsm, new HttpClient(handler));

        var result = await client.GetSystemAsync("Sol");

        Assert.False(result.IsOk);
    }

    [Fact]
    public async Task Distance_skips_the_second_lookup_when_the_first_fails()
    {
        var handler = new FuncHandler((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, "")));
        var lookup = new EdsmSystemLookup(new EdsmClient(FastEdsm, new HttpClient(handler)));

        var distance = await lookup.DistanceBetweenAsync("A", "B");

        Assert.Null(distance);
        Assert.Equal(1, handler.Calls);
    }

    // ---------------- Raven Colonial ----------------

    private static readonly RavenColonialClientOptions FastRaven = new()
    {
        SoftwareName = "T", SoftwareVersion = "1", BaseUrl = "https://raven.test", TransientRetryDelay = TimeSpan.Zero,
    };

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(" .. ")]
    public async Task Raven_dot_segment_ids_never_reach_the_network(string id)
    {
        var handler = new RecordingHandler(body: "{}");
        var client = new RavenColonialClient(FastRaven, new HttpClient(handler));

        var project = await client.GetProjectAsync(id);
        var system = await client.GetSystemProjectsAsync(id);

        Assert.True(project.IsOk);
        Assert.Null(project.Value);
        Assert.True(system.IsOk);
        Assert.Empty(system.Value!);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Raven_numeric_strings_parse_the_same_under_any_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A culture with a different negative sign would mis-parse "-5" if the current culture were used.
            var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NegativeSign = "~";
            Thread.CurrentThread.CurrentCulture = culture;

            var handler = new RecordingHandler(body: """{ "buildId": "b1", "marketId": "-5", "systemAddress": "42" }""");
            var client = new RavenColonialClient(FastRaven, new HttpClient(handler));

            var project = (await client.GetProjectAsync("b1")).Value!;

            Assert.Equal(-5L, project.MarketId);
            Assert.Equal(42L, project.SystemAddress);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public async Task Raven_429_with_retry_after_is_retried_once()
    {
        var handler = new FuncHandler((n, _) => Task.FromResult(n == 1
            ? Json((HttpStatusCode)429, "", TimeSpan.FromMilliseconds(10))
            : Json(HttpStatusCode.OK, """{ "buildId": "b1" }""")));
        var client = new RavenColonialClient(FastRaven, new HttpClient(handler));

        var result = await client.GetProjectAsync("b1");

        Assert.Equal("b1", result.Value!.BuildId);
        Assert.Equal(2, handler.Calls);
    }

    // ---------------- Galnet ----------------

    private static readonly GalnetClientOptions SmallGalnet = new()
    {
        SoftwareName = "T", SoftwareVersion = "1", FeedUrl = "https://galnet.test/rss", MaxResponseBytes = 1000,
    };

    [Fact]
    public async Task Galnet_oversized_declared_body_is_refused()
    {
        var handler = new FuncHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, new string('x', 5000))));
        var client = new GalnetClient(SmallGalnet, new HttpClient(handler));

        var result = await client.GetLatestAsync();

        Assert.False(result.IsOk);
        Assert.Contains("larger than", result.Error);
    }

    [Fact]
    public async Task Galnet_oversized_streamed_body_is_refused_without_a_content_length()
    {
        var handler = new FuncHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[5000]),
        }));
        var client = new GalnetClient(SmallGalnet, new HttpClient(handler));

        var result = await client.GetLatestAsync();

        Assert.False(result.IsOk);
    }

    /// <summary>A body with no Content-Length (as a chunked response has), so only the streaming cap can stop it.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public void Galnet_feed_with_a_doctype_is_refused()
    {
        var result = GalnetClient.Parse("""
            <?xml version="1.0"?>
            <!DOCTYPE rss [ <!ENTITY a "aaaaaaaaaa"> <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;"> ]>
            <rss><channel><item><title>&b;</title></item></channel></rss>
            """);

        Assert.False(result.IsOk);
    }

    [Fact]
    public void Galnet_pathological_bodies_parse_in_linear_time()
    {
        // Long runs that make naive regexes quadratic: many '<' with no '>', and long space runs with no newline.
        var evil = new string('<', 200_000) + new string(' ', 200_000);
        var feed = $"<rss><channel><item><title>T</title><description><![CDATA[{evil}]]></description></item></channel></rss>";

        var sw = Stopwatch.StartNew();
        var result = GalnetClient.Parse(feed);

        Assert.True(result.IsOk);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Galnet_text_cleanup_is_unchanged_for_ordinary_bodies()
    {
        var result = GalnetClient.Parse("""
            <rss><channel><item><title>T</title>
              <description><![CDATA[Line one.   <br />
            Line <b>two</b>.<br />



            Last &amp; final.<p>Para</p>]]></description></item></channel></rss>
            """);

        Assert.Equal("Line one.\nLine two.\nLast & final.Para", result.Value![0].Body);
    }
}
