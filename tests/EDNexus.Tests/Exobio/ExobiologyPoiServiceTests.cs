using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EDNexus.Core.Exobio;
using EDNexus.Core.Trade;
using EDNexus.Tests.Reporting;   // shared RecordingHandler test double
using EliteDangerous.Edsm;
using Xunit;

namespace EDNexus.Tests.Exobio;

/// <summary>Canned-response tests for the GEC POI client and service. No live calls.</summary>
public class ExobiologyPoiServiceTests
{
    private static readonly EdsmClientOptions Options = new()
    {
        SoftwareName = "EDNexus.Tests",
        SoftwareVersion = "1.0.0",
        GecBaseUrl = "https://gec.test/",
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

    private static EdsmClient Client(RecordingHandler h) => new(Options, new HttpClient(h));

    [Fact]
    public async Task Client_parses_defensively_and_uses_the_gec_endpoint()
    {
        var handler = new RecordingHandler(body: Canned);
        using var client = Client(handler);

        var result = await client.GetGecPoisAsync();

        Assert.True(result.IsOk);
        Assert.Equal("https://gec.test/gec/json/all", handler.Uris.Single()!.ToString());
        Assert.Equal(3, result.Value!.Count);   // nameless / non-object entries dropped
        var bio = result.Value[0];
        Assert.Equal("Black Sage Fields", bio.Name);
        Assert.Equal(11225133574, bio.SystemId64);
        Assert.Equal("Eoch Flyi AA-A g167", bio.SystemName);
        Assert.Equal(new EdsmCoords(-6159.62, -680.875, 17346.4), bio.Coords);
        Assert.Equal(5.11, bio.Rating);
        var odd = result.Value[2];
        Assert.Null(odd.Coords);       // wrong-length array
        Assert.Null(odd.SystemId64);   // wrong type
        Assert.Null(odd.Rating);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task Client_never_throws_on_odd_bodies(string body)
    {
        using var client = Client(new RecordingHandler(body: body));
        var result = await client.GetGecPoisAsync();
        Assert.True(result.IsOk ? result.Value!.Count == 0 : result.Error is not null);
    }

    [Fact]
    public async Task Client_reports_http_failure_as_a_failure_result()
    {
        using var client = Client(new RecordingHandler(HttpStatusCode.InternalServerError));
        var result = await client.GetGecPoisAsync();
        Assert.False(result.IsOk);
    }

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

    [Fact]
    public async Task Nearby_is_sorted_by_distance_and_honours_type_and_radius()
    {
        using var client = Client(new RecordingHandler(body: Canned));
        var svc = new ExobiologyPoiService(client, () => true);

        var near = await svc.GetNearbyAsync(new EdsmCoords(334, -55, 23000), 100);
        Assert.Equal("The Ammonia Lyceum", Assert.Single(near).Poi.Name);

        var all = await svc.GetNearbyAsync(new EdsmCoords(334, -55, 23000), 1e9);
        Assert.Equal(2, all.Count);   // the coordinate-less entry is excluded
        Assert.True(all[0].DistanceLy <= all[1].DistanceLy);

        var organic = await svc.GetNearbyAsync(new EdsmCoords(334, -55, 23000), 1e9, "organic");
        Assert.Equal("Black Sage Fields", Assert.Single(organic).Poi.Name);

        Assert.Empty(await svc.GetNearbyAsync(new EdsmCoords(0, 0, 0), double.NaN));
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
        Assert.DoesNotContain("descriptionHtml", cache.Get(ExobiologyPoiService_Key())!);   // trimmed before caching
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

    private static string ExobiologyPoiService_Key() => "edastro|gec|all|v1";

    private sealed class MemoryCache : IResponseCache
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Expire { get; set; }
        public string? Get(string key) => !Expire && _d.TryGetValue(key, out var v) ? v : null;
        public void Put(string key, string body) => _d[key] = body;
        public string? GetStale(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Remove(string key) => _d.Remove(key);
    }
}
