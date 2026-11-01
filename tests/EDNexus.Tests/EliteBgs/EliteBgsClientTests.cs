using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using EliteDangerous.EliteBgs;
using Xunit;

namespace EDNexus.Tests.EliteBgs;

public class EliteBgsClientTests
{
    private const string FactionJson = """
    { "docs": [
      { "name": "Other Faction Of Sol", "faction_presence": [] },
      { "name": "Federal Reclamation Co", "allegiance": "federation", "government": "corporate",
        "updated_at": "2026-10-30T12:00:00.000Z",
        "faction_presence": [
          { "system_name": "Sol", "state": "boom", "influence": 0.4231, "happiness": "$Faction_HappinessBand2;",
            "active_states": [ { "state": "boom" } ],
            "pending_states": [ { "state": "war", "trend": 1 } ],
            "recovering_states": [ { "state": "retreat", "trend": -1 } ],
            "conflicts": [ { "type": "war", "status": "active", "opponent_name": "Sol Workers", "stake": "X", "days_won": 2 },
                           { "status": "no type" } ],
            "updated_at": "2026-10-30T12:00:00.000Z" },
          { "system_name": "Alpha Centauri", "influence": 12.5 },
          { "state": "no system name" },
          "garbage"
        ] } ], "total": 2 }
    """;

    private const string SystemJson = """
    { "docs": [ { "name": "Sol", "controlling_minor_faction": "Mother Gaia", "allegiance": "federation",
        "government": "democracy", "security": "$SYSTEM_SECURITY_high;", "population": 22780919531,
        "factions": [
          { "name": "Low", "faction_details": { "faction_presence": { "state": "none", "influence": 0.1 } } },
          { "name": "High", "faction_details": { "faction_presence": { "state": "boom", "influence": 0.6,
              "active_states": [ { "state": "boom" } ] } } },
          { "name": "Inline", "state": "war", "influence": 0.3 },
          { "no": "name" }
        ],
        "conflicts": [ { "type": "election", "status": "pending" } ] } ] }
    """;

    private sealed class Handler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public List<Uri?> Uris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri);
            return Task.FromResult(respond(Interlocked.Increment(ref Calls)));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent("") };
        if (retryAfter is { } ra) r.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
        return r;
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static EliteBgsClient NewClient(Handler h, FakeClock? clock = null, Func<bool>? gate = null,
        TimeSpan? maxRetryAfter = null) =>
        new(new EliteBgsClientOptions
        {
            SoftwareName = "EDNexus.Tests",
            SoftwareVersion = "1.0.0",
            Clock = clock ?? new FakeClock(),
            IsEnabled = gate,
            MaxRetryAfter = maxRetryAfter ?? TimeSpan.FromSeconds(5),
            TransientRetryDelay = TimeSpan.Zero,
        }, new HttpClient(h));

    [Fact]
    public async Task Parses_the_exact_faction_match_with_presence_and_states()
    {
        using var client = NewClient(new Handler(_ => Json(FactionJson)));

        var result = await client.GetFactionAsync("federal reclamation co");

        Assert.True(result.IsOk);
        var f = result.Value!;
        Assert.Equal("Federal Reclamation Co", f.Name);
        Assert.Equal("federation", f.Allegiance);
        Assert.Equal(2, f.Presence.Count);   // nameless + garbage entries dropped

        var sol = f.Presence[0];
        Assert.Equal("Sol", sol.SystemName);
        Assert.Equal("boom", sol.State);
        Assert.Equal(0.4231, sol.Influence);
        Assert.Equal("boom", Assert.Single(sol.ActiveStates).State);
        Assert.Equal(new BgsState("war", 1), Assert.Single(sol.PendingStates));
        Assert.Equal(new BgsState("retreat", -1), Assert.Single(sol.RecoveringStates));
        var conflict = Assert.Single(sol.Conflicts);   // the typeless conflict is skipped
        Assert.Equal("Sol Workers", conflict.OpponentName);
        Assert.Equal(2, conflict.DaysWon);
        Assert.NotNull(sol.UpdatedAt);

        // A percentage-style influence is normalised to a fraction; missing state/lists are empty, not errors.
        var alpha = f.Presence[1];
        Assert.Equal(0.125, alpha.Influence);
        Assert.Null(alpha.State);
        Assert.Empty(alpha.ActiveStates);
    }

    [Fact]
    public async Task Parses_a_system_with_factions_sorted_by_influence()
    {
        using var client = NewClient(new Handler(_ => Json(SystemJson)));

        var result = await client.GetSystemAsync("Sol");

        var s = result.Value!;
        Assert.Equal("Mother Gaia", s.ControllingFaction);
        Assert.Equal(22780919531, s.Population);
        Assert.Equal(new[] { "High", "Inline", "Low" }, s.Factions.Select(f => f.Name).ToArray());
        Assert.Equal("boom", s.Factions[0].State);
        Assert.Equal("war", s.Factions[1].State);   // inline (non-nested) shape tolerated
        Assert.Equal("election", Assert.Single(s.Conflicts).Type);
    }

    [Fact]
    public async Task Request_targets_the_v5_endpoint_with_an_escaped_name()
    {
        var h = new Handler(_ => Json("""{ "docs": [] }"""));
        using var client = NewClient(h);

        await client.GetFactionAsync("Fed & Co");

        Assert.Equal("https://elitebgs.app/api/ebgs/v5/factions?name=Fed%20%26%20Co", h.Uris[0]!.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{ "docs": [] }""")]
    [InlineData("""{ "docs": [ { "name": "Someone Else" } ] }""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{ "docs": "nope" }""")]
    public async Task Unknown_or_oddly_shaped_replies_are_ok_with_a_null_value(string body)
    {
        using var client = NewClient(new Handler(_ => Json(body)));

        var result = await client.GetSystemAsync("Sol");

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Malformed_json_and_server_errors_fail_without_throwing()
    {
        using var bad = NewClient(new Handler(_ => Json("<html>")));
        var r1 = await bad.GetFactionAsync("X");
        Assert.False(r1.IsOk);
        Assert.Contains("unparseable", r1.Error);

        using var down = NewClient(new Handler(_ => Status(HttpStatusCode.InternalServerError)));
        var r2 = await down.GetFactionAsync("X");
        Assert.False(r2.IsOk);
        Assert.Equal("HTTP 500", r2.Error);
        Assert.False(r2.IsRateLimited);
    }

    [Fact]
    public async Task Repeat_lookups_are_served_from_cache_until_the_ttl_expires()
    {
        var clock = new FakeClock();
        var h = new Handler(_ => Json(SystemJson));
        using var client = NewClient(h, clock);

        var first = await client.GetSystemAsync("Sol");
        var second = await client.GetSystemAsync("  SOL ");
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(1, h.Calls);

        clock.Now += TimeSpan.FromMinutes(16);
        var third = await client.GetSystemAsync("Sol");
        Assert.False(third.FromCache);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        var h = new Handler(n => n == 1 ? Status(HttpStatusCode.InternalServerError) : Json(SystemJson));
        using var client = NewClient(h);

        Assert.False((await client.GetSystemAsync("Sol")).IsOk);
        Assert.NotNull((await client.GetSystemAsync("Sol")).Value);
    }

    [Fact]
    public async Task Disabled_gate_makes_no_request()
    {
        var h = new Handler(_ => Json(SystemJson));
        var enabled = false;
        using var client = NewClient(h, gate: () => enabled);

        var off = await client.GetSystemAsync("Sol");
        Assert.False(off.IsOk);
        Assert.Equal(0, h.Calls);

        enabled = true;
        Assert.True((await client.GetSystemAsync("Sol")).IsOk);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task A_short_rate_limit_is_retried_once()
    {
        var h = new Handler(n => n == 1 ? Status(HttpStatusCode.TooManyRequests, TimeSpan.Zero) : Json(SystemJson));
        using var client = NewClient(h);

        var result = await client.GetSystemAsync("Sol");

        Assert.True(result.IsOk);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task A_long_rate_limit_fails_fast_then_cools_down_without_hitting_the_network()
    {
        var clock = new FakeClock();
        var h = new Handler(_ => Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(2)));
        using var client = NewClient(h, clock);

        var first = await client.GetSystemAsync("Sol");
        Assert.False(first.IsOk);
        Assert.True(first.IsRateLimited);
        Assert.Equal(1, h.Calls);   // Retry-After exceeded the wait ceiling: no retry

        var during = await client.GetFactionAsync("Anyone");
        Assert.True(during.IsRateLimited);
        Assert.Equal(1, h.Calls);   // cool-down: no request sent

        clock.Now += TimeSpan.FromMinutes(3);
        await client.GetFactionAsync("Anyone");
        Assert.Equal(2, h.Calls);   // cool-down over (Retry-After of 2 min honoured, not just the 30s default)
    }

    [Fact]
    public async Task Blank_names_short_circuit_to_an_empty_ok_result()
    {
        var h = new Handler(_ => Json(SystemJson));
        using var client = NewClient(h);

        var result = await client.GetSystemAsync("   ");

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
        Assert.Equal(0, h.Calls);
    }
}
