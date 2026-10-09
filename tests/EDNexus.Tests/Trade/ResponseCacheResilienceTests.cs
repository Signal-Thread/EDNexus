using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EDNexus.Core.Navigation;
using EDNexus.Core.Routes;
using EDNexus.Core.Trade;
using EDNexus.Tests.Reporting;   // reuse the shared RecordingHandler test double
using EliteDangerous.Edsm;
using EliteDangerous.Spansh;
using Xunit;

namespace EDNexus.Tests.Trade;

/// <summary>The disk cache is best-effort: bad files, bad folders and changed record shapes are misses, never throws.</summary>
public class ResponseCacheResilienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ednexus-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string EntryPath(string key) =>
        Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_body_that_is_not_a_string_is_a_miss_not_a_throw()
    {
        var cache = new DiskResponseCache(_dir, TimeSpan.FromDays(1));
        File.WriteAllText(EntryPath("k"), $$"""{ "at": "{{DateTimeOffset.UtcNow:O}}", "body": 5 }""");

        Assert.Null(cache.Get("k"));
        Assert.Null(cache.GetStale("k"));
    }

    [Fact]
    public void A_corrupt_entry_is_a_miss_and_is_deleted()
    {
        var cache = new DiskResponseCache(_dir, TimeSpan.FromDays(1));
        File.WriteAllText(EntryPath("k"), "{ not json");

        Assert.Null(cache.Get("k"));
        Assert.False(File.Exists(EntryPath("k")));
    }

    [Fact]
    public void A_locked_entry_is_a_miss_not_an_IOException()
    {
        var cache = new DiskResponseCache(_dir, TimeSpan.FromDays(1));
        cache.Put("k", "v");

        using (new FileStream(EntryPath("k"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(cache.Get("k"));
            Assert.Null(cache.GetStale("k"));
            cache.Put("k", "new");   // must not throw either
        }
    }

    [Fact]
    public void An_uncreatable_directory_does_not_throw_from_the_constructor_or_operations()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "afile");
        File.WriteAllText(blocker, "x");

        var cache = new DiskResponseCache(Path.Combine(blocker, "sub"), TimeSpan.FromDays(1));   // a path under a file

        cache.Put("k", "v");
        Assert.Null(cache.Get("k"));
        Assert.Null(cache.GetStale("k"));
        cache.Remove("k");
    }

    [Fact]
    public void Put_replaces_atomically_and_leaves_no_temp_files()
    {
        var cache = new DiskResponseCache(_dir, TimeSpan.FromDays(1));

        cache.Put("k", "one");
        cache.Put("k", "two");

        Assert.Equal("two", cache.Get("k"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Single(Directory.GetFiles(_dir, "*.json"));
    }

    [Fact]
    public void Remove_discards_the_entry()
    {
        var cache = new DiskResponseCache(_dir, TimeSpan.FromDays(1));
        cache.Put("k", "v");

        cache.Remove("k");

        Assert.Null(cache.GetStale("k"));
    }

    [Fact]
    public void Startup_deletes_entries_past_retention_and_orphaned_temp_files()
    {
        Directory.CreateDirectory(_dir);
        var oldEntry = Path.Combine(_dir, "old.json");
        var newEntry = Path.Combine(_dir, "new.json");
        var oldTemp = Path.Combine(_dir, "x.json.abc.tmp");
        foreach (var f in new[] { oldEntry, newEntry, oldTemp }) File.WriteAllText(f, "{}");
        File.SetLastWriteTimeUtc(oldEntry, DateTime.UtcNow.AddDays(-100));
        File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddDays(-2));

        _ = new DiskResponseCache(_dir, TimeSpan.FromHours(1), retention: TimeSpan.FromDays(30));

        Assert.False(File.Exists(oldEntry));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(newEntry));
    }

    [Fact]
    public void Startup_keeps_only_the_newest_entries_up_to_the_cap()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < 5; i++)
        {
            var f = Path.Combine(_dir, $"e{i}.json");
            File.WriteAllText(f, "{}");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-10 + i));   // e4 is newest
        }

        _ = new DiskResponseCache(_dir, TimeSpan.FromHours(1), maxEntries: 2);

        Assert.Equal(new[] { "e3.json", "e4.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n));
    }

    // --- GetTyped ---

    private sealed class FakeCache(string? body) : IResponseCache
    {
        public List<string> Removed { get; } = new();
        public string? Get(string key) => body;
        public void Put(string key, string b) { }
        public void Remove(string key) => Removed.Add(key);
    }

    private sealed record Shape(string Name, int Count);

    [Fact]
    public void GetTyped_returns_a_value_that_deserialises()
    {
        var cache = new FakeCache("""{ "name": "x", "count": 3 }""");

        Assert.Equal(new Shape("x", 3), cache.GetTyped<Shape>("k", Web));
        Assert.Empty(cache.Removed);
    }

    [Theory]
    [InlineData("""{ "name": "x", "count": "not a number" }""")]   // the record shape changed since it was cached
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("not json at all")]
    public void GetTyped_treats_an_undeserialisable_entry_as_a_miss_and_drops_it(string body)
    {
        var cache = new FakeCache(body);

        Assert.Null(cache.GetTyped<Shape>("k", Web));
        Assert.Equal(new[] { "k" }, cache.Removed);
    }

    [Fact]
    public void GetTyped_on_a_null_cache_or_a_miss_is_null()
    {
        IResponseCache? none = null;
        Assert.Null(none.GetTyped<Shape>("k", Web));
        Assert.Null(new FakeCache(null).GetTyped<Shape>("k", Web));
    }

    // --- Consumers refetch instead of throwing on a changed shape ---

    [Fact]
    public async Task Trade_search_refetches_when_the_cached_shape_is_incompatible()
    {
        var handler = new RecordingHandler(body: """
            { "count": 1, "results": [ { "system_name":"Alioth", "name":"Golden Gate", "distance":31.5, "market_updated_at":"2026-07-06T09:30:00Z",
              "market":[ { "commodity":"Painite", "sell_price":455000, "buy_price":0, "demand":800, "supply":0 } ] } ] }
            """);
        var cache = new FakeCache("""{ "unexpected": "shape" }""");
        var search = new SpanshTradeSearch(new SpanshClient(new SpanshClientOptions { SoftwareName = "T", SoftwareVersion = "1" }, new HttpClient(handler)), cache);

        var quotes = await search.SearchAsync(new TradeQuery("painite", "Sol"));

        Assert.Single(quotes);
        Assert.Equal(1, handler.CallCount);
        Assert.Single(cache.Removed);
    }

    [Fact]
    public async Task System_lookup_refetches_when_the_cached_entry_is_a_json_null()
    {
        var handler = new RecordingHandler(body: """{ "name": "Sol", "coords": { "x": 1, "y": 2, "z": 3 } }""");
        var cache = new FakeCache("null");
        var lookup = new EdsmSystemLookup(new EdsmClient(new EdsmClientOptions { SoftwareName = "T", SoftwareVersion = "1" }, new HttpClient(handler)), cache);

        var info = await lookup.GetSystemAsync("Sol");

        Assert.Equal(new SystemCoords(1, 2, 3), info!.Coords);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Route_plotter_refetches_when_the_cached_shape_is_incompatible()
    {
        const string Ready = """
            { "status": "ok", "result": { "system_jumps": [
                { "system": "Sol", "jumps": 0, "distance_jumped": 0, "distance_left": 400, "neutron_star": false },
                { "system": "Colonia", "jumps": 2, "distance_jumped": 150, "distance_left": 0, "neutron_star": false } ] } }
            """;
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? """{ "job": "j1", "status": "queued" }""" : Ready));
        var cache = new FakeCache("""[ { "system": 5 } ]""");
        var plotter = new SpanshRoutePlotter(new SpanshClient(
            new SpanshClientOptions { SoftwareName = "T", SoftwareVersion = "1", RoutePollInterval = TimeSpan.Zero }, new HttpClient(handler)), cache);

        var plan = await plotter.PlotAsync(new RoutePlotRequest("Sol", "Colonia", 48));

        Assert.NotNull(plan);
        Assert.Single(cache.Removed);
    }
}
