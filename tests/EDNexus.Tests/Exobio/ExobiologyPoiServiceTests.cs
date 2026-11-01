using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EDNexus.Core.Exobio;
using EDNexus.Core.Trade;
using EDNexus.Tests.Reporting;   // shared RecordingHandler test double
using EliteDangerous.Edsm;
using Xunit;

namespace EDNexus.Tests.Exobio;

/// <summary>Canned-response tests for the EDAstro GEC client and the POI service. No live calls.</summary>
public class ExobiologyPoiServiceTests
{
    private static readonly EdAstroClientOptions Options = new()
    {
        SoftwareName = "EDNexus.Tests",
        SoftwareVersion = "1.0.0",
        BaseUrl = "https://gec.test/",
        TransientRetryDelay = TimeSpan.Zero,
    };

    // Shaped like a real /gec/json/all reply (trimmed), plus deliberately hostile entries.
    private const string Canned = """
    [
      { "id": 74, "type": "Organic", "name": "Black Sage Fields", "region": "Odin's Hold",
        "galMapSearch": "Eoch Flyi AA-A g167", "coordinates": [-6159.62, -680.875, 17346.4],
        "summary": "Many bio signs.", "descriptionHtml": "<p>huge</p>", "id64": 11225133574,
        "rating": 5.11, "poiUrl": "https://edastro.com/gec/view/74", "latitude": 1.5 },
      { "id": 10, "type": "Sights and Scenery", "name": "The Ammonia Lyceum",
        "galMapSearch": "Athaip WR-H d11-7577", "coordinates": [334.969, -55.4375, 23014.3], "id64": 5 },
      { "id": 11, "type": "Organic", "name": "No Coords", "coordinates": [1, 2], "id64": "oops", "rating": "x" },
      { "id": 12, "type": "Organic", "name": "", "id64": 9 },
      { "type": "Organic" },
      42
    ]
    """;

    private static EdAstroClient Client(HttpMessageHandler h, EdAstroClientOptions? o = null) => new(o ?? Options, new HttpClient(h));

    private static EdAstroClientOptions WithLimit(long bytes) => new()
    {
        SoftwareName = "t", SoftwareVersion = "1", BaseUrl = "https://gec.test", MaxResponseBytes = bytes,
    };

    private static readonly EdsmCoords Lyceum = new(334, -55, 23000);

    private static string Catalogue(int count) =>
        "[" + string.Join(",", Enumerable.Range(1, count).Select(i => $$"""{"id":{{i}},"name":"P{{i}}","id64":{{1000 + i}}}""")) + "]";

    // ---- client ----

    [Fact]
    public async Task Client_parses_defensively_and_uses_the_gec_endpoint()
    {
        var handler = new RecordingHandler(body: Canned);
        using var client = Client(handler);

        var result = await client.GetPointsOfInterestAsync();

        Assert.True(result.IsOk);
        Assert.Equal("https://gec.test/gec/json/all", handler.Uris.Single()!.ToString());
        Assert.Equal(3, result.Value!.Count);   // nameless / non-object entries dropped
        var bio = result.Value[0];
        Assert.Equal("Black Sage Fields", bio.Name);
        Assert.Equal(11225133574, bio.SystemId64);
        Assert.Equal("Eoch Flyi AA-A g167", bio.SystemName);
        Assert.Equal(new EdsmCoords(-6159.62, -680.875, 17346.4), bio.Coords);
        Assert.Equal(5.11, bio.Rating);
        Assert.Equal("https://edastro.com/gec/view/74", bio.Url);
        var odd = result.Value[2];
        Assert.Null(odd.Coords);       // wrong-length array
        Assert.Null(odd.SystemId64);   // wrong type
        Assert.Null(odd.Rating);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("42")]
    public async Task Client_treats_a_non_catalogue_body_as_ok_but_empty(string body)
    {
        using var client = Client(new RecordingHandler(body: body));
        var result = await client.GetPointsOfInterestAsync();
        Assert.True(result.IsOk);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task Client_reports_unparseable_json_as_a_failure()
    {
        using var client = Client(new RecordingHandler(body: "not json"));
        var result = await client.GetPointsOfInterestAsync();
        Assert.False(result.IsOk);
        Assert.Contains("unparseable", result.Error);
    }

    [Fact]
    public async Task Client_reports_http_failure_as_a_failure_result()
    {
        using var client = Client(new RecordingHandler(HttpStatusCode.InternalServerError));
        var result = await client.GetPointsOfInterestAsync();
        Assert.False(result.IsOk);
        Assert.Equal("HTTP 500", result.Error);
    }

    [Fact]
    public async Task Client_retries_once_after_a_429_then_succeeds()
    {
        var handler = new RecordingHandler(n => n == 1 ? (HttpStatusCode.TooManyRequests, "") : (HttpStatusCode.OK, Canned));
        using var client = Client(handler);

        var result = await client.GetPointsOfInterestAsync();

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Count);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Client_gives_up_after_a_second_429()
    {
        var handler = new RecordingHandler(HttpStatusCode.TooManyRequests);
        using var client = Client(handler);

        var result = await client.GetPointsOfInterestAsync();

        Assert.False(result.IsOk);
        Assert.Equal("HTTP 429", result.Error);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Client_sanitises_third_party_strings_and_links()
    {
        var longText = new string('x', 5000);
        var json = $$"""
        [
          { "id": 1, "name": "Evil‮\u0007 Name", "type": "Organic", "poiUrl": "javascript:alert(1)", "summary": "{{longText}}",
            "region": "{{longText}}", "galMapSearch": "{{longText}}", "id64": 100 },
          { "id": 2, "name": "{{longText}}", "poiUrl": "file:///etc/passwd", "id64": 101 },
          { "id": 3, "name": "ftp", "poiUrl": "ftp://example.com/a", "id64": 102 },
          { "id": 4, "name": "creds", "poiUrl": "https://user:pw@example.com/a", "id64": 103 },
          { "id": 5, "name": "relative", "poiUrl": "/gec/view/5", "id64": 104 },
          { "id": 6, "name": "huge url", "poiUrl": "https://example.com/{{longText}}", "id64": 105 },
          { "id": 7, "name": "plain http", "poiUrl": "http://example.com/ok", "id64": 106 }
        ]
        """;
        using var client = Client(new RecordingHandler(body: json));

        var pois = (await client.GetPointsOfInterestAsync()).Value!;

        Assert.Equal("Evil Name", pois[0].Name);                // bidi override + BEL stripped
        Assert.Null(pois[0].Url);                                // javascript: refused
        Assert.Equal(300, pois[0].Summary!.Length);
        Assert.Equal(100, pois[0].Region!.Length);
        Assert.Equal(100, pois[0].SystemName!.Length);
        Assert.Equal(200, pois[1].Name.Length);
        Assert.Null(pois[1].Url);                                // file:
        Assert.Null(pois[2].Url);                                // ftp:
        Assert.Null(pois[3].Url);                                // credentials
        Assert.Null(pois[4].Url);                                // relative
        Assert.Null(pois[5].Url);                                // over-long
        Assert.Equal("http://example.com/ok", pois[6].Url);      // http is allowed
    }

    [Fact]
    public async Task Client_does_not_give_system_address_zero_or_negative()
    {
        using var client = Client(new RecordingHandler(body: """[{"id":1,"name":"A","id64":0},{"id":2,"name":"B","id64":-5},{"id":3,"name":"C"}]"""));
        var pois = (await client.GetPointsOfInterestAsync()).Value!;
        Assert.Equal(3, pois.Count);
        Assert.All(pois, p => Assert.Null(p.SystemId64));
    }

    [Fact]
    public async Task Client_drops_duplicates_and_keeps_id_less_entries_distinct_by_name_and_system()
    {
        var json = """
        [
          { "id": 7, "name": "First", "id64": 1 },
          { "id": 7, "name": "Second copy of id 7", "id64": 2 },
          { "name": "No id", "id64": 3 },
          { "name": "no ID", "id64": 3 },
          { "name": "No id", "id64": 4 },
          { "id": 0, "name": "Zero id", "id64": 5 }
        ]
        """;
        using var client = Client(new RecordingHandler(body: json));
        var pois = (await client.GetPointsOfInterestAsync()).Value!;

        Assert.Equal(new[] { "First", "No id", "No id", "Zero id" }, pois.Select(p => p.Name).ToArray());
        Assert.Equal(new long?[] { 1, 3, 4, 5 }, pois.Select(p => p.SystemId64).ToArray());
    }

    [Fact]
    public async Task Client_refuses_a_response_declared_larger_than_the_limit()
    {
        using var client = Client(new RecordingHandler(body: Canned), WithLimit(64));

        var result = await client.GetPointsOfInterestAsync();

        Assert.False(result.IsOk);
        Assert.Contains("too large", result.Error);
    }

    [Fact]
    public async Task Client_refuses_an_oversize_body_with_no_declared_length()
    {
        using var client = Client(new FixedHandler(() => new UnknownLengthContent(new string(' ', 5000) + "[]")), WithLimit(1000));

        var result = await client.GetPointsOfInterestAsync();

        Assert.False(result.IsOk);
        Assert.Contains("too large", result.Error);
    }

    [Fact]
    public async Task Client_does_not_accept_a_redirect()
    {
        using var client = Client(new RecordingHandler(HttpStatusCode.Found, "moved"));
        var result = await client.GetPointsOfInterestAsync();
        Assert.False(result.IsOk);
        Assert.Contains("redirect", result.Error);
    }

    [Fact]
    public async Task Client_refuses_a_reply_that_came_from_another_host()
    {
        using var client = Client(new FixedHandler(() => new StringContent(Canned), finalHost: "evil.test"));
        var result = await client.GetPointsOfInterestAsync();
        Assert.False(result.IsOk);
        Assert.Contains("another host", result.Error);
    }

    [Theory]
    [InlineData("http://edastro.com")]
    [InlineData("ftp://edastro.com")]
    [InlineData("edastro.com")]
    [InlineData("https://user:pw@edastro.com")]
    [InlineData("")]
    public void Client_rejects_a_base_url_that_is_not_plain_absolute_https(string baseUrl)
    {
        var o = new EdAstroClientOptions { SoftwareName = "t", SoftwareVersion = "1", BaseUrl = baseUrl };
        Assert.Throws<ArgumentException>(() => new EdAstroClient(o, new HttpClient(new RecordingHandler())));
    }

    [Fact]
    public async Task Client_rethrows_cancellation_instead_of_returning_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var gate = new GatedHandler(Canned);
        using var client = Client(gate);

        var pending = client.GetPointsOfInterestAsync(cts.Token);
        await gate.Started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // ---- service ----

    [Fact]
    public async Task Disabled_service_makes_no_request()
    {
        var handler = new RecordingHandler(body: Canned);
        using var client = Client(handler);
        var svc = new ExobiologyPoiService(client, () => false);

        Assert.Empty(await svc.GetForSystemAsync(11225133574));
        Assert.Empty(await svc.GetNearbyAsync(new EdsmCoords(0, 0, 0), 1e9));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Turning_the_service_off_releases_the_catalogue_and_back_on_refetches()
    {
        var enabled = true;
        var handler = new RecordingHandler(body: Canned);
        using var client = Client(handler);
        var svc = new ExobiologyPoiService(client, () => enabled);

        Assert.Single(await svc.GetForSystemAsync(11225133574));
        enabled = false;
        Assert.Empty(await svc.GetForSystemAsync(11225133574));
        enabled = true;
        Assert.Single(await svc.GetForSystemAsync(11225133574));

        Assert.Equal(2, handler.CallCount);   // memory was dropped while off (no disk cache in this test)
    }

    [Fact]
    public async Task Enabled_service_filters_by_system_and_fetches_once()
    {
        var handler = new RecordingHandler(body: Canned);
        using var client = Client(handler);
        var svc = new ExobiologyPoiService(client, () => true);

        var hit = await svc.GetForSystemAsync(11225133574);
        Assert.Equal("Black Sage Fields", Assert.Single(hit).Name);
        Assert.Empty(await svc.GetForSystemAsync(11225133574, type: "Nebulae"));
        Assert.Empty(await svc.GetForSystemAsync(999));
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task System_address_zero_or_negative_matches_nothing_even_with_id64_zero_in_the_data(long address)
    {
        var handler = new RecordingHandler(body: """[{"id":1,"name":"Zero","id64":0},{"id":2,"name":"Missing"}]""");
        using var client = Client(handler);
        var svc = new ExobiologyPoiService(client, () => true);

        Assert.Empty(await svc.GetForSystemAsync(address));
        Assert.Equal(0, handler.CallCount);   // short-circuited before any fetch
    }

    [Fact]
    public async Task Concurrent_first_lookups_share_a_single_fetch()
    {
        var gate = new GatedHandler(Canned);
        using var client = Client(gate);
        var svc = new ExobiologyPoiService(client, () => true);

        var a = svc.GetForSystemAsync(11225133574);
        await gate.Started.Task;          // first request is in flight
        var b = svc.GetForSystemAsync(5); // second caller queues on the gate
        gate.Release();
        var results = await Task.WhenAll(a, b);

        Assert.Single(results[0]);
        Assert.Single(results[1]);
        Assert.Equal(1, gate.CallCount);
    }

    [Fact]
    public async Task Cancelling_a_lookup_surfaces_as_cancellation_and_does_not_poison_later_lookups()
    {
        using var cts = new CancellationTokenSource();
        var gate = new GatedHandler(Canned);
        using var client = Client(gate);
        var svc = new ExobiologyPoiService(client, () => true);

        var pending = svc.GetForSystemAsync(5, ct: cts.Token);
        await gate.Started.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        using var already = new CancellationTokenSource();
        already.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.GetForSystemAsync(5, ct: already.Token));

        gate.Release();   // a later, un-cancelled lookup still works (a cancel records no back-off)
        Assert.Single(await svc.GetForSystemAsync(5));
    }

    [Fact]
    public async Task Nearby_is_sorted_by_distance_and_honours_type_and_radius()
    {
        using var client = Client(new RecordingHandler(body: Canned));
        var svc = new ExobiologyPoiService(client, () => true);

        var near = await svc.GetNearbyAsync(Lyceum, 100);
        Assert.Equal("The Ammonia Lyceum", Assert.Single(near).Poi.Name);

        var all = await svc.GetNearbyAsync(Lyceum, 1e9);
        Assert.Equal(2, all.Count);   // the coordinate-less entry is excluded
        Assert.True(all[0].DistanceLy <= all[1].DistanceLy);

        var organic = await svc.GetNearbyAsync(Lyceum, 1e9, "organic");
        Assert.Equal("Black Sage Fields", Assert.Single(organic).Poi.Name);

        Assert.Empty(await svc.GetNearbyAsync(new EdsmCoords(0, 0, 0), double.NaN));
        Assert.Empty(await svc.GetNearbyAsync(new EdsmCoords(0, 0, 0), -1));
    }

    [Fact]
    public async Task Infinite_radius_returns_every_poi_with_coordinates_and_non_finite_origin_returns_none()
    {
        using var client = Client(new RecordingHandler(body: Canned));
        var svc = new ExobiologyPoiService(client, () => true);

        Assert.Equal(2, (await svc.GetNearbyAsync(Lyceum, double.PositiveInfinity)).Count);
        Assert.Empty(await svc.GetNearbyAsync(new EdsmCoords(double.NaN, 0, 0), 1e9));
        Assert.Empty(await svc.GetNearbyAsync(new EdsmCoords(0, double.PositiveInfinity, 0), double.PositiveInfinity));
    }

    [Fact]
    public async Task Cache_serves_a_second_service_without_a_request()
    {
        var cache = new MemoryCache();
        var h1 = new RecordingHandler(body: Canned);
        using (var c1 = Client(h1))
            await new ExobiologyPoiService(c1, () => true, cache).GetForSystemAsync(5);
        Assert.Equal(1, h1.CallCount);

        var h2 = new RecordingHandler(HttpStatusCode.InternalServerError);
        using var c2 = Client(h2);
        var hit = await new ExobiologyPoiService(c2, () => true, cache).GetForSystemAsync(5);

        Assert.Equal("The Ammonia Lyceum", Assert.Single(hit).Name);
        Assert.Equal(0, h2.CallCount);
        Assert.DoesNotContain("descriptionHtml", cache.Get(ExobiologyPoiService.CacheKey)!);   // trimmed before caching
    }

    [Fact]
    public async Task Failed_fetch_falls_back_to_stale_cache_then_to_empty()
    {
        var cache = new MemoryCache();
        using (var c1 = Client(new RecordingHandler(body: Canned)))
            await new ExobiologyPoiService(c1, () => true, cache).GetForSystemAsync(5);
        cache.Expire = true;

        using var failing = Client(new RecordingHandler(HttpStatusCode.InternalServerError));
        var stale = await new ExobiologyPoiService(failing, () => true, cache).GetForSystemAsync(5);
        Assert.Single(stale);

        using var failing2 = Client(new RecordingHandler(HttpStatusCode.InternalServerError));
        Assert.Empty(await new ExobiologyPoiService(failing2, () => true).GetForSystemAsync(5));
    }

    [Fact]
    public async Task An_outage_with_no_data_backs_off_instead_of_hitting_the_network_every_lookup()
    {
        var clock = new FakeTime();
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        using var client = Client(handler);
        var svc = new ExobiologyPoiService(client, () => true, time: clock);

        Assert.Empty(await svc.GetForSystemAsync(5));
        Assert.Empty(await svc.GetForSystemAsync(5));
        Assert.Empty(await svc.GetNearbyAsync(Lyceum, 100));
        Assert.Equal(1, handler.CallCount);

        clock.Advance(ExobiologyPoiService.DefaultFailureBackoff + TimeSpan.FromSeconds(1));
        Assert.Empty(await svc.GetForSystemAsync(5));
        Assert.Equal(2, handler.CallCount);   // retried once the back-off lapsed
    }

    [Fact]
    public async Task An_outage_serves_the_stale_copy_from_memory_without_rereading_the_cache_or_the_network()
    {
        var clock = new FakeTime();
        var cache = new MemoryCache();
        using (var c1 = Client(new RecordingHandler(body: Canned)))
            await new ExobiologyPoiService(c1, () => true, cache).GetForSystemAsync(5);
        cache.Expire = true;
        cache.StaleReads = 0;

        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        using var failing = Client(handler);
        var svc = new ExobiologyPoiService(failing, () => true, cache, clock);

        for (var i = 0; i < 5; i++) Assert.Single(await svc.GetForSystemAsync(5));

        Assert.Equal(2, handler.CallCount);   // one attempt plus its single 503 retry; none for the other four lookups
        Assert.Equal(1, cache.StaleReads);    // deserialised once, then memoised

        clock.Advance(ExobiologyPoiService.DefaultFailureBackoff + TimeSpan.FromSeconds(1));
        Assert.Single(await svc.GetForSystemAsync(5));
        Assert.Equal(4, handler.CallCount);   // tried again after the back-off, still serving the stale copy
        Assert.Equal(1, cache.StaleReads);
    }

    [Fact]
    public async Task A_stale_service_recovers_when_the_fetch_works_again()
    {
        var clock = new FakeTime();
        var cache = new MemoryCache();
        using (var c1 = Client(new RecordingHandler(body: Canned)))
            await new ExobiologyPoiService(c1, () => true, cache).GetForSystemAsync(5);
        cache.Expire = true;

        var ok = false;
        var handler = new RecordingHandler(_ => ok ? (HttpStatusCode.OK, Canned) : (HttpStatusCode.InternalServerError, ""));
        using var client = Client(handler);
        var svc = new ExobiologyPoiService(client, () => true, cache, clock);

        Assert.Single(await svc.GetForSystemAsync(5));   // stale
        ok = true;
        clock.Advance(ExobiologyPoiService.DefaultFailureBackoff + TimeSpan.FromSeconds(1));
        Assert.Single(await svc.GetForSystemAsync(5));   // refreshed
        var calls = handler.CallCount;
        Assert.Single(await svc.GetForSystemAsync(5));
        Assert.Equal(calls, handler.CallCount);          // no longer stale: no more requests
    }

    [Fact]
    public async Task A_suspiciously_small_reply_does_not_replace_a_good_catalogue()
    {
        var cache = new MemoryCache();
        using (var c1 = Client(new RecordingHandler(body: Catalogue(10))))
            await new ExobiologyPoiService(c1, () => true, cache).GetForSystemAsync(1001);
        cache.Expire = true;

        using var client = Client(new RecordingHandler(body: Catalogue(1)));
        var svc = new ExobiologyPoiService(client, () => true, cache);

        Assert.Single(await svc.GetForSystemAsync(1001));
        Assert.Single(await svc.GetForSystemAsync(1010));   // still present: the 1-entry reply was ignored
        Assert.Contains("\"P10\"", cache.GetStale(ExobiologyPoiService.CacheKey)!);   // and not written over the cache
    }

    [Fact]
    public async Task A_reply_at_half_the_previous_size_is_accepted()
    {
        var cache = new MemoryCache();
        using (var c1 = Client(new RecordingHandler(body: Catalogue(10))))
            await new ExobiologyPoiService(c1, () => true, cache).GetForSystemAsync(1001);
        cache.Expire = true;

        using var client = Client(new RecordingHandler(body: Catalogue(5)));
        var svc = new ExobiologyPoiService(client, () => true, cache);

        Assert.Empty(await svc.GetForSystemAsync(1010));   // shrink to 5 was legitimate
        Assert.Single(await svc.GetForSystemAsync(1001));
    }

    // ---- test doubles ----

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class MemoryCache : IResponseCache
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Expire { get; set; }
        public int StaleReads { get; set; }
        public string? Get(string key) => !Expire && _d.TryGetValue(key, out var v) ? v : null;
        public void Put(string key, string body) => _d[key] = body;
        public string? GetStale(string key) { StaleReads++; return _d.TryGetValue(key, out var v) ? v : null; }
        public void Remove(string key) => _d.Remove(key);
    }

    /// <summary>Blocks every response until <see cref="Release"/> so tests can interleave callers deterministically.</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => _count;
        public GatedHandler(string body) => _body = body;
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) };
        }
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly Func<HttpContent> _content;
        private readonly string? _finalHost;
        public FixedHandler(Func<HttpContent> content, string? finalHost = null) { _content = content; _finalHost = finalHost; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = _finalHost is null ? request.RequestUri : new UriBuilder(request.RequestUri!) { Host = _finalHost }.Uri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = _content(),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
            });
        }
    }

    /// <summary>Content with no Content-Length (like a chunked reply), so only the streaming limit can catch it.</summary>
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;
        public UnknownLengthContent(string text) => _bytes = Encoding.UTF8.GetBytes(text);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes, 0, _bytes.Length);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
