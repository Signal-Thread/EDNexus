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
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.UtcTicks;
    }

    private static EliteBgsClient NewClient(Handler h, FakeClock? clock = null, Func<bool>? gate = null,
        TimeSpan? maxRetryAfter = null, int maxCache = 256, HttpClient? http = null, TimeSpan? maxCooldown = null) =>
        new(new EliteBgsClientOptions
        {
            SoftwareName = "EDNexus.Tests",
            SoftwareVersion = "1.0.0",
            Clock = clock ?? new FakeClock(),
            IsEnabled = gate,
            MaxRetryAfter = maxRetryAfter ?? TimeSpan.FromSeconds(5),
            TransientRetryDelay = TimeSpan.Zero,
            MaxCacheEntries = maxCache,
            MaxCooldown = maxCooldown ?? TimeSpan.FromMinutes(15),
        }, http ?? new HttpClient(h));

    [Fact]
    public async Task A_throwing_gate_is_treated_as_disabled()
    {
        var h = new Handler(_ => Json(SystemJson));
        using var client = NewClient(h, gate: () => throw new InvalidOperationException("settings disposed"));

        var result = await client.GetSystemAsync("Sol");

        Assert.False(result.IsOk);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task A_huge_retry_after_is_clamped_to_max_cooldown()
    {
        var clock = new FakeClock();
        var h = new Handler(_ => Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2_000_000_000)));
        using var client = NewClient(h, clock);

        await client.GetSystemAsync("Sol");
        clock.Now += TimeSpan.FromMinutes(16);
        await client.GetSystemAsync("Sol");

        Assert.Equal(2, h.Calls);   // cool-down capped at 15 minutes, not 63 years
    }

    [Fact]
    public async Task A_503_with_a_long_retry_after_starts_a_cooldown()
    {
        var h = new Handler(_ => Status(HttpStatusCode.ServiceUnavailable, TimeSpan.FromMinutes(2)));
        using var client = NewClient(h);

        var first = await client.GetSystemAsync("Sol");
        var second = await client.GetFactionAsync("X");

        Assert.False(first.IsOk);
        Assert.Equal("HTTP 503", first.Error);
        Assert.True(second.IsRateLimited);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task A_503_is_retried_once_and_a_second_429_gives_up_with_a_cooldown()
    {
        var h = new Handler(n => n == 1 ? Status(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero) : Status(HttpStatusCode.TooManyRequests, TimeSpan.Zero));
        using var client = NewClient(h);

        var result = await client.GetSystemAsync("Sol");

        Assert.False(result.IsOk);
        Assert.True(result.IsRateLimited);
        Assert.Equal(2, h.Calls);
        await client.GetSystemAsync("Sol");
        Assert.Equal(2, h.Calls);   // cooling down
    }

    [Fact]
    public async Task A_retry_after_date_is_measured_against_the_injected_clock()
    {
        var clock = new FakeClock();
        var h = new Handler(n =>
        {
            if (n > 1) return Json(SystemJson);
            var r = Status(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(clock.Now);   // already due: no wait
            return r;
        });
        using var client = NewClient(h, clock);

        Assert.True((await client.GetSystemAsync("Sol")).IsOk);
        Assert.Equal(2, h.Calls);

        var far = new Handler(_ =>
        {
            var r = Status(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(clock.Now + TimeSpan.FromMinutes(5));
            return r;
        });
        using var client2 = NewClient(far, clock);
        await client2.GetSystemAsync("Sol");
        Assert.Equal(1, far.Calls);   // 5 minutes exceeds MaxRetryAfter, so no retry
        clock.Now += TimeSpan.FromMinutes(4);
        Assert.True((await client2.GetSystemAsync("Sol")).IsRateLimited);
        Assert.Equal(1, far.Calls);
        clock.Now += TimeSpan.FromMinutes(2);
        await client2.GetSystemAsync("Sol");
        Assert.Equal(2, far.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_but_an_http_timeout_is_a_failure()
    {
        var gate = new TaskCompletionSource();
        var slow = new GateHandler(gate.Task);
        using var client = NewClient(null!, http: new HttpClient(slow));
        using var cts = new CancellationTokenSource();

        var pending = client.GetSystemAsync("Sol", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        gate.SetResult();

        using var timingOut = NewClient(null!, http: new HttpClient(new Handler(_ => throw new TaskCanceledException("timeout"))));
        var result = await timingOut.GetSystemAsync("Sol");
        Assert.False(result.IsOk);
    }

    private sealed class GateHandler(Task gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await gate;
            return Json(SystemJson);
        }
    }

    [Fact]
    public async Task Concurrent_lookups_of_one_name_share_a_single_request()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var handler = new CountingGateHandler(gate.Task, () => Interlocked.Increment(ref calls));
        using var client = NewClient(null!, http: new HttpClient(handler));

        var tasks = Enumerable.Range(0, 8).Select(_ => client.GetSystemAsync("Sol")).ToArray();
        await Task.Delay(100);
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.NotNull(r.Value));
        Assert.Equal(1, calls);
    }

    private sealed class CountingGateHandler(Task gate, Action onCall) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onCall();
            await gate;
            return Json(SystemJson);
        }
    }

    [Fact]
    public async Task Cache_evicts_the_oldest_entry_at_capacity_and_clear_cache_empties_it()
    {
        var clock = new FakeClock();
        // Eviction: capacity 2, three distinct names -> the first is evicted.
        var seq = new Handler(_ => Json("""{ "docs": [ { "name": "A" }, { "name": "B" }, { "name": "C" } ] }"""));
        using var c3 = NewClient(seq, clock, maxCache: 2);
        await c3.GetSystemAsync("A"); clock.Now += TimeSpan.FromSeconds(1);
        await c3.GetSystemAsync("B"); clock.Now += TimeSpan.FromSeconds(1);
        await c3.GetSystemAsync("C"); clock.Now += TimeSpan.FromSeconds(1);
        Assert.False((await c3.GetSystemAsync("A")).FromCache);   // evicted
        Assert.Equal(4, seq.Calls);
        Assert.True((await c3.GetSystemAsync("C")).FromCache);

        c3.ClearCache();
        Assert.False((await c3.GetSystemAsync("C")).FromCache);
    }

    [Fact]
    public async Task A_faction_and_a_system_with_the_same_name_do_not_share_a_cache_entry()
    {
        var h = new Handler(_ => Json("""{ "docs": [ { "name": "Sol" } ] }"""));
        using var client = NewClient(h);

        await client.GetSystemAsync("Sol");
        var faction = await client.GetFactionAsync("Sol");

        Assert.False(faction.FromCache);
        Assert.Equal(2, h.Calls);
        Assert.IsType<BgsFaction>(faction.Value);
    }

    [Fact]
    public async Task Non_empty_docs_without_any_name_is_an_uncached_unrecognised_shape_failure()
    {
        var h = new Handler(_ => Json("""{ "docs": [ { "title": "Sol" }, { "title": "Other" } ] }"""));
        using var client = NewClient(h);

        var first = await client.GetSystemAsync("Sol");
        await client.GetSystemAsync("Sol");

        Assert.False(first.IsOk);
        Assert.Equal("unrecognised response shape", first.Error);
        Assert.Equal(2, h.Calls);   // not cached
    }

    [Fact]
    public async Task A_near_match_only_reply_is_not_cached_as_a_definitive_miss()
    {
        var h = new Handler(_ => Json("""{ "docs": [ { "name": "Sol Workers" } ], "hasNextPage": true }"""));
        using var client = NewClient(h);

        Assert.Null((await client.GetFactionAsync("Sol")).Value);
        Assert.Null((await client.GetFactionAsync("Sol")).Value);

        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task An_empty_reply_is_cached_as_a_definitive_miss()
    {
        var h = new Handler(_ => Json("""{ "docs": [] }"""));
        using var client = NewClient(h);

        await client.GetFactionAsync("Nobody");
        Assert.True((await client.GetFactionAsync("Nobody")).FromCache);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task An_oversized_response_fails_without_parsing()
    {
        var h = new Handler(_ => Json(new string('x', 5000)));
        using var client = new EliteBgsClient(new EliteBgsClientOptions
        {
            SoftwareName = "t", SoftwareVersion = "1", MaxResponseBytes = 2048,
        }, new HttpClient(h));

        var result = await client.GetSystemAsync("Sol");

        Assert.False(result.IsOk);
        Assert.Equal("response too large", result.Error);
    }

    [Theory]
    [InlineData("a+b#c/d?e%f", "a%2Bb%23c%2Fd%3Fe%25f")]
    [InlineData("Zeta Reticuli é", "Zeta%20Reticuli%20%C3%A9")]
    public async Task Special_and_unicode_names_are_escaped(string name, string expected)
    {
        var h = new Handler(_ => Json("""{ "docs": [] }"""));
        using var client = NewClient(h);

        await client.GetFactionAsync(name);

        Assert.EndsWith("?name=" + expected, h.Uris[0]!.AbsoluteUri);
    }

    [Fact]
    public async Task Nfc_and_nfd_spellings_share_a_cache_entry()
    {
        var h = new Handler(_ => Json("""{ "docs": [ { "name": "Café" } ] }"""));
        using var client = NewClient(h);

        await client.GetSystemAsync("Café");
        var nfd = await client.GetSystemAsync("Café");

        Assert.True(nfd.FromCache);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task An_overlong_name_fails_without_a_request()
    {
        var h = new Handler(_ => Json("{}"));
        using var client = NewClient(h);

        var result = await client.GetSystemAsync(new string('a', 5000));

        Assert.False(result.IsOk);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Influence_is_a_clamped_fraction_and_ties_sort_by_name()
    {
        var h = new Handler(_ => Json("""
            { "docs": [ { "name": "Sol", "factions": [
              { "name": "Zed", "influence": 0.2 }, { "name": "Alpha", "influence": 0.2 },
              { "name": "None" }, { "name": "Big", "influence": 150 } ] } ] }
            """));
        using var client = NewClient(h);

        var s = (await client.GetSystemAsync("Sol")).Value!;

        Assert.Equal(new[] { "Big", "Alpha", "Zed", "None" }, s.Factions.Select(f => f.Name).ToArray());
        Assert.Equal(1.0, s.Factions[0].Influence);
        Assert.False(s.Factions is List<BgsSystemFaction>);
    }

    [Fact]
    public async Task User_agent_is_set_per_request_not_on_a_shared_client()
    {
        var h = new Handler(_ => Json("""{ "docs": [] }"""));
        var http = new HttpClient(h);
        using var client = NewClient(h, http: http);

        await client.GetSystemAsync("Sol");

        Assert.Empty(http.DefaultRequestHeaders.UserAgent);
    }

    [Theory]
    [InlineData("http://elitebgs.app")]
    [InlineData("ftp://x")]
    [InlineData("not a url")]
    public void Non_https_base_urls_are_rejected_except_loopback(string url)
    {
        Assert.Throws<ArgumentException>(() => new EliteBgsClient(
            new EliteBgsClientOptions { SoftwareName = "t", SoftwareVersion = "1", BaseUrl = url }));
        new EliteBgsClient(new EliteBgsClientOptions { SoftwareName = "t", SoftwareVersion = "1", BaseUrl = "http://localhost:5000" }).Dispose();
    }

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

        // Influence is a 0..1 fraction; out-of-range values clamp. Missing state/lists are empty, not errors.
        var alpha = f.Presence[1];
        Assert.Equal(1.0, alpha.Influence);
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
