using System.Net;
using System.Text;
using EliteDangerous.Inara;
using Xunit;

namespace EDNexus.Tests.Reporting;

public class InaraReadClientTests
{
    private static readonly InaraClientOptions Options = new() { AppName = "EDNexus.Tests", AppVersion = "1.0.0", IsBeingDeveloped = true };
    private static readonly InaraIdentity Identity = new() { ApiKey = "secret-key", CommanderName = "Jameson" };

    /// <summary>Wall clock and monotonic clock are separate so a test can step the wall clock backwards.</summary>
    private sealed class Clock : TimeProvider
    {
        private TimeSpan _mono = TimeSpan.FromHours(1);
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan t) { Now += t; _mono += t; }
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => _mono.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly object _lock = new();
        public Queue<Func<HttpResponseMessage>> Replies { get; } = new();
        public List<string> Bodies { get; } = new();
        public Task? Hold { get; set; }
        public int Calls { get { lock (_lock) return Bodies.Count; } }
        public void Json(string json) => Replies.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        public void Status(HttpStatusCode code, TimeSpan? retryAfter = null) => Replies.Enqueue(() =>
        {
            var m = new HttpResponseMessage(code);
            if (retryAfter is { } ra) m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
            return m;
        });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (_lock) Bodies.Add(body);
            if (Hold is not null) await Hold.WaitAsync(ct);
            return Replies.Dequeue()();
        }
    }

    private static (InaraReadClient Client, Handler Handler, Clock Clock) Make(Func<bool>? enabled = null, InaraReadOptions? opts = null)
    {
        var h = new Handler();
        var c = new Clock();
        return (new InaraReadClient(Options, enabled ?? (() => true), new HttpClient(h), opts, c), h, c);
    }

    private static string Reply(string eventData, int status = 200)
        => $$"""{ "header": { "eventStatus": 200 }, "events": [ { "eventStatus": {{status}}, "eventData": {{eventData}} } ] }""";

    private const string ProfileJson = """
        { "commanderName": "Jameson", "userName": "Jameson", "inaraURL": "https://inara.cz/cmdr/1/",
          "commanderSquadron": { "squadronName": "Some Squad", "squadronMemberRank": "Leader" },
          "preferredPowerName": "Zachary Hudson",
          "commanderRanksPilot": [ { "rankName": "combat", "rankValue": 5, "rankProgress": 0.5 }, "junk" ],
          "commanderReputationMajorFaction": [ { "majorfactionName": "federation", "majorfactionReputation": 80.5 } ] }
        """;

    private static readonly TimeSpan Expired = TimeSpan.FromMinutes(11); // past the 10 minute profile TTL

    /// <summary>Primes the cache with a profile, then lets it expire (still inside the 40 minute stale window).</summary>
    private static async Task<(InaraReadClient Client, Handler Handler, Clock Clock)> MakeWithExpiredProfile(InaraReadOptions? opts = null)
    {
        var made = Make(opts: opts);
        made.Handler.Json(Reply(ProfileJson));
        Assert.True((await made.Client.GetCommanderProfileAsync(Identity)).HasValue);
        made.Clock.Advance(Expired);
        return made;
    }

    [Fact]
    public async Task Profile_parses_and_sends_query_with_key()
    {
        var (client, h, _) = Make();
        h.Json(Reply(ProfileJson));

        var r = await client.GetCommanderProfileAsync(Identity);

        Assert.True(r.HasValue);
        Assert.Equal("Some Squad", r.Value!.SquadronName);
        Assert.Equal("Leader", r.Value.SquadronRank);
        Assert.Equal("Zachary Hudson", r.Value.Power);
        var rank = Assert.Single(r.Value.Ranks);
        Assert.Equal(5, rank.Value);
        Assert.Equal(80.5, Assert.Single(r.Value.Reputations).Reputation);
        Assert.Equal(TimeSpan.Zero, r.Age);
        Assert.NotNull(r.FetchedAt);
        Assert.Contains("\"eventName\":\"getCommanderProfile\"", h.Bodies[0]);
        Assert.Contains("\"APIkey\":\"secret-key\"", h.Bodies[0]);
        Assert.Contains("\"searchName\":\"Jameson\"", h.Bodies[0]);
        Assert.DoesNotContain("commanderFrontierID", h.Bodies[0]); // null is omitted, not sent
    }

    [Fact]
    public async Task Second_call_is_served_from_cache_until_ttl_expires()
    {
        var (client, h, clock) = Make();
        h.Json(Reply(ProfileJson));
        h.Json(Reply(ProfileJson));

        await client.GetCommanderProfileAsync(Identity);
        var cached = await client.GetCommanderProfileAsync(Identity);
        Assert.True(cached.FromCache);
        Assert.False(cached.IsStale);
        Assert.Equal(1, h.Calls);

        clock.Advance(Expired);
        var fresh = await client.GetCommanderProfileAsync(Identity);
        Assert.False(fresh.FromCache);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Case_and_whitespace_variants_share_one_cache_entry()
    {
        var (client, h, _) = Make();
        h.Json(Reply("[ { \"stationName\": \"A\" } ]"));

        await client.GetNearestServicesAsync(Identity, "Sol", InaraServiceKind.Shipyard);
        var again = await client.GetNearestServicesAsync(Identity, "  sOL ", InaraServiceKind.Shipyard);
        var umlaut1 = await client.GetNearestServicesAsync(Identity, "Ünïcode", InaraServiceKind.Shipyard); // new key: limited locally
        Assert.True(again.FromCache);
        Assert.Equal(1, h.Calls);
        Assert.Equal(InaraReadStatus.RateLimited, umlaut1.Status);
        Assert.Contains("\"searchName\":\"Sol\"", h.Bodies[0]); // trimmed, original case sent
    }

    [Fact]
    public async Task Blank_names_are_rejected_locally()
    {
        var (client, h, _) = Make();
        Assert.Equal(InaraReadStatus.Error, (await client.GetCommanderProfileAsync(Identity, "   ")).Status);
        Assert.Equal(InaraReadStatus.Error, (await client.GetNearestServicesAsync(Identity, "", InaraServiceKind.Engineer)).Status);
        Assert.Equal(InaraReadStatus.Error, (await client.GetNearestServicesAsync(Identity, "Sol", (InaraServiceKind)99)).Status);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Disabled_gate_or_missing_key_makes_no_request()
    {
        var (off, h1, _) = Make(() => false);
        Assert.Equal(InaraReadStatus.Disabled, (await off.GetCommunityGoalsAsync(Identity)).Status);
        Assert.Equal(0, h1.Calls);

        var (on, h2, _) = Make();
        var blank = new InaraIdentity { ApiKey = " ", CommanderName = "J" };
        Assert.Equal(InaraReadStatus.Disabled, (await on.GetEngineersAsync(blank)).Status);
        Assert.Equal(0, h2.Calls);
    }

    [Fact]
    public async Task Gate_turning_off_serves_no_cached_data_even_after_it_turns_back_on()
    {
        var enabled = true;
        var (client, h, clock) = Make(() => enabled);
        h.Json(Reply(ProfileJson));
        Assert.True((await client.GetCommanderProfileAsync(Identity)).HasValue);

        enabled = false;
        var off = await client.GetCommanderProfileAsync(Identity);
        Assert.Equal(InaraReadStatus.Disabled, off.Status);
        Assert.Null(off.Value);

        enabled = true; // cache was cleared when consent was withdrawn: this is a live request again
        clock.Advance(TimeSpan.FromSeconds(31));
        h.Json(Reply(ProfileJson));
        var back = await client.GetCommanderProfileAsync(Identity);
        Assert.False(back.FromCache);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public void Endpoint_must_be_https_or_loopback()
    {
        var bad = new InaraClientOptions { AppName = "x", AppVersion = "1", Endpoint = "http://inara.cz/inapi/v1/" };
        Assert.Throws<ArgumentException>(() => new InaraReadClient(bad, () => true));
        var local = new InaraClientOptions { AppName = "x", AppVersion = "1", Endpoint = "http://localhost:5000/" };
        new InaraReadClient(local, () => true).Dispose();
    }

    [Fact]
    public async Task Local_limiter_is_per_api_key()
    {
        var (client, h, clock) = Make();
        h.Json(Reply(ProfileJson));
        h.Json(Reply("[]"));
        h.Json(Reply("[]"));

        await client.GetCommanderProfileAsync(Identity);
        var limited = await client.GetCommunityGoalsAsync(Identity);
        Assert.Equal(InaraReadStatus.RateLimited, limited.Status);
        Assert.Equal(1, h.Calls);

        var other = new InaraIdentity { ApiKey = "other-key", CommanderName = "Other" };
        Assert.Equal(InaraReadStatus.Ok, (await client.GetCommunityGoalsAsync(other)).Status); // different key, own slot
        Assert.Equal(2, h.Calls);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(InaraReadStatus.Ok, (await client.GetCommunityGoalsAsync(Identity)).Status);
    }

    [Fact]
    public async Task Concurrent_identical_queries_share_one_live_request()
    {
        var (client, h, _) = Make();
        var release = new TaskCompletionSource();
        h.Hold = release.Task;
        h.Json(Reply(ProfileJson));

        var a = client.GetCommanderProfileAsync(Identity);
        var b = client.GetCommanderProfileAsync(Identity, " JAMESON ");
        var other = await client.GetCommunityGoalsAsync(Identity); // different query, same key: still within the 30s slot
        Assert.Equal(InaraReadStatus.RateLimited, other.Status);
        release.SetResult();

        Assert.True((await a).HasValue);
        Assert.True((await b).HasValue);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Http_429_honours_retry_after_and_serves_stale_cache()
    {
        var (client, h, clock) = await MakeWithExpiredProfile();
        h.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(5));

        var r = await client.GetCommanderProfileAsync(Identity);
        Assert.True(r.HasValue);
        Assert.True(r.IsStale);
        Assert.Equal(TimeSpan.FromMinutes(5), r.RetryAfter);
        Assert.True(r.Age >= Expired);

        // Still cooling down: no further request.
        clock.Advance(TimeSpan.FromMinutes(1));
        await client.GetCommanderProfileAsync(Identity);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Huge_retry_after_is_clamped_and_a_shorter_one_never_shortens_a_longer_cooldown()
    {
        var (client, h, clock) = Make();
        h.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(99_999_999));
        var r = await client.GetEngineersAsync(Identity);
        Assert.Equal(InaraReadStatus.RateLimited, r.Status);
        Assert.Equal(TimeSpan.FromHours(1), r.RetryAfter);

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(InaraReadStatus.RateLimited, (await client.GetEngineersAsync(Identity)).Status);
        Assert.Equal(1, h.Calls);

        clock.Advance(TimeSpan.FromMinutes(2));
        h.Json(Reply("[ { \"engineerName\": \"E\" } ]"));
        Assert.Equal(InaraReadStatus.Ok, (await client.GetEngineersAsync(Identity)).Status);
    }

    [Fact]
    public async Task Retry_after_http_date_is_honoured()
    {
        var (client, h, clock) = Make();
        h.Replies.Enqueue(() =>
        {
            var m = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(clock.Now + TimeSpan.FromMinutes(7));
            return m;
        });
        var r = await client.GetEngineersAsync(Identity);
        Assert.Equal(TimeSpan.FromMinutes(7), r.RetryAfter);
    }

    [Fact]
    public async Task Backwards_wall_clock_does_not_extend_freshness_or_cooldown()
    {
        var (client, h, clock) = Make();
        h.Json(Reply(ProfileJson));
        h.Json(Reply(ProfileJson));
        await client.GetCommanderProfileAsync(Identity);

        clock.Now -= TimeSpan.FromHours(5); // NTP step / user change; monotonic time is unaffected
        clock.Advance(Expired);
        var r = await client.GetCommanderProfileAsync(Identity);
        Assert.False(r.FromCache); // expired by monotonic time, not still "fresh" for 5 more hours
        Assert.Equal(2, h.Calls);

        h.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(2));
        clock.Advance(TimeSpan.FromMinutes(11));
        await client.GetCommanderProfileAsync(Identity); // 429, cooldown 2 min
        clock.Now -= TimeSpan.FromHours(5);
        clock.Advance(TimeSpan.FromMinutes(3));
        h.Json(Reply(ProfileJson));
        Assert.Equal(InaraReadStatus.Ok, (await client.GetCommanderProfileAsync(Identity)).Status); // not locked out for 5 hours
    }

    [Fact]
    public async Task Rate_limit_wording_in_header_or_event_is_RateLimited_with_cooldown()
    {
        var (client, h, clock) = Make();
        h.Json("""{ "header": { "eventStatus": 400, "eventStatusText": "Too many requests" } }""");
        var r = await client.GetCommunityGoalsAsync(Identity);
        Assert.Equal(InaraReadStatus.RateLimited, r.Status);
        Assert.Equal(TimeSpan.FromMinutes(2), r.RetryAfter);

        clock.Advance(TimeSpan.FromMinutes(3));
        h.Json("""{ "header": { "eventStatus": 200 }, "events": [ { "eventStatus": 400, "eventStatusText": "Rate limit exceeded" } ] }""");
        var ev = await client.GetEngineersAsync(Identity);
        Assert.Equal(InaraReadStatus.RateLimited, ev.Status);
        Assert.Equal(TimeSpan.FromMinutes(2), ev.RetryAfter);
    }

    [Fact]
    public async Task Bad_key_is_Rejected_and_key_never_appears_in_message()
    {
        var (client, h, _) = Make();
        h.Json("""{ "header": { "eventStatus": 400, "eventStatusText": "Invalid API key" } }""");
        var r = await client.GetEngineersAsync(Identity);
        Assert.Equal(InaraReadStatus.Rejected, r.Status);
        Assert.Null(r.RetryAfter);
        Assert.DoesNotContain("secret-key", r.Message);
    }

    [Fact]
    public async Task Http_4xx_is_Rejected_and_5xx_is_Error()
    {
        var (client, h, clock) = Make();
        h.Status(HttpStatusCode.BadRequest);
        Assert.Equal(InaraReadStatus.Rejected, (await client.GetEngineersAsync(Identity)).Status);
        clock.Advance(TimeSpan.FromSeconds(31));
        h.Status(HttpStatusCode.ServiceUnavailable, TimeSpan.FromMinutes(4));
        var r = await client.GetEngineersAsync(Identity);
        Assert.Equal(InaraReadStatus.Error, r.Status);
        Assert.Equal(TimeSpan.FromMinutes(4), r.RetryAfter);
        clock.Advance(TimeSpan.FromMinutes(1)); // the 5xx Retry-After keeps the limiter closed
        Assert.Equal(InaraReadStatus.RateLimited, (await client.GetEngineersAsync(Identity)).Status);
    }

    [Fact]
    public async Task Stale_profile_is_served_on_5xx()
    {
        var (client, h, _) = await MakeWithExpiredProfile();
        h.Status(HttpStatusCode.BadGateway);
        var r = await client.GetCommanderProfileAsync(Identity);
        Assert.True(r.HasValue);
        Assert.True(r.IsStale);
        Assert.Equal("HTTP 502", r.Message);
    }

    [Theory]
    [InlineData("<html>maintenance</html>")]
    [InlineData("[]")]
    [InlineData("""{ "header": { "eventStatus": 200 }, "events": [ { "eventStatus": 200, "eventData": { "wrapper": { "commanderName": "x" } } } ] }""")]
    public async Task Stale_profile_is_served_when_the_refresh_does_not_parse(string badBody)
    {
        var (client, h, _) = await MakeWithExpiredProfile();
        h.Json(badBody);
        var r = await client.GetCommanderProfileAsync(Identity);
        Assert.True(r.HasValue);
        Assert.True(r.IsStale);
        Assert.Equal("Some Squad", r.Value!.SquadronName);
    }

    [Fact]
    public async Task Stale_data_past_MaxStale_is_not_served()
    {
        var (client, h, clock) = await MakeWithExpiredProfile(new InaraReadOptions { MaxStale = TimeSpan.FromMinutes(30) });
        clock.Advance(TimeSpan.FromMinutes(25)); // age 36 min > 30
        h.Status(HttpStatusCode.BadGateway);
        var r = await client.GetCommanderProfileAsync(Identity);
        Assert.Equal(InaraReadStatus.Error, r.Status);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task NotFound_204_evicts_the_cache_so_stale_data_cannot_resurrect_it()
    {
        var (client, h, clock) = await MakeWithExpiredProfile();
        h.Json("""{ "header": { "eventStatus": 200 }, "events": [ { "eventStatus": 204, "eventStatusText": "not found" } ] }""");
        Assert.Equal(InaraReadStatus.NotFound, (await client.GetCommanderProfileAsync(Identity)).Status);

        clock.Advance(TimeSpan.FromSeconds(5)); // inside the limiter window: would have fallen back to the old profile
        var again = await client.GetCommanderProfileAsync(Identity);
        Assert.Equal(InaraReadStatus.RateLimited, again.Status);
        Assert.Null(again.Value);
    }

    [Fact]
    public async Task Cancellation_returns_Cancelled_and_does_not_fail_other_callers()
    {
        var (client, h, _) = Make();
        var release = new TaskCompletionSource();
        h.Hold = release.Task;
        h.Json(Reply(ProfileJson));
        using var cts = new CancellationTokenSource();

        var cancelled = client.GetCommanderProfileAsync(Identity, null, cts.Token);
        var other = client.GetCommanderProfileAsync(Identity);
        cts.Cancel();
        Assert.Equal(InaraReadStatus.Cancelled, (await cancelled).Status);

        release.SetResult();
        Assert.True((await other).HasValue);
    }

    [Fact]
    public async Task Disposing_during_a_request_cancels_it()
    {
        var (client, h, _) = Make();
        h.Hold = new TaskCompletionSource().Task; // never completes
        var pending = client.GetCommanderProfileAsync(Identity);
        client.Dispose();
        Assert.Equal(InaraReadStatus.Cancelled, (await pending).Status);
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        var (client, h, _) = Make(opts: new InaraReadOptions { MaxResponseBytes = 2048 });
        h.Json(Reply("[ " + string.Join(",", Enumerable.Repeat("{ \"stationName\": \"AAAAAAAAAAAAAAAA\" }", 200)) + " ]"));
        var r = await client.GetNearestServicesAsync(Identity, "Sol", InaraServiceKind.Shipyard);
        Assert.Equal(InaraReadStatus.Error, r.Status);
        Assert.Equal("response too large", r.Message);
    }

    [Fact]
    public async Task Server_status_text_is_capped_and_control_characters_stripped()
    {
        var (client, h, _) = Make();
        h.Json("{ \"header\": { \"eventStatus\": 500, \"eventStatusText\": \"bad\\nline " + new string('x', 5000) + "\" } }");
        var r = await client.GetEngineersAsync(Identity);
        Assert.Equal(InaraReadStatus.Error, r.Status);
        Assert.True(r.Message!.Length <= 200);
        Assert.DoesNotContain('\n', r.Message);
    }

    [Fact]
    public async Task Nearest_services_parse_and_skip_unrecognised_rows()
    {
        var (client, h, _) = Make();
        h.Json(Reply("""[ { "stationName": "Jameson Memorial", "starsystemName": "Shinrarta Dezhra", "distance": 0.0, "distanceToArrival": 123.4, "marketID": 42 }, 7, {} ]"""));

        var r = await client.GetNearestServicesAsync(Identity, "Sol", InaraServiceKind.MaterialTrader);

        var only = Assert.Single(r.Value!);
        Assert.Equal("Jameson Memorial", only.Name);
        Assert.Equal(42, only.MarketId);
        Assert.Contains("\"searchName\":\"Sol\"", h.Bodies[0]);
        Assert.Contains("materialtrader", h.Bodies[0]);
    }

    [Fact]
    public async Task Engineers_parse_blueprints_and_materials()
    {
        var (client, h, _) = Make();
        h.Json(Reply("""
            [ { "engineerName": "Felicity Farseer", "starsystemName": "Deciat", "unlockRequirements": "Bring 5 Meta-Alloys",
                "blueprints": [ { "blueprintName": "fsd_increased_range", "blueprintGrade": 3,
                                  "materials": [ { "materialName": "Atypical Disrupted Wake Echoes", "materialCount": 4 } ] } ] } ]
            """));

        var e = Assert.Single((await client.GetEngineersAsync(Identity)).Value!);
        Assert.Equal("Felicity Farseer", e.Name);
        var bp = Assert.Single(e.Blueprints);
        Assert.Equal(3, bp.Grade);
        Assert.Equal(4, Assert.Single(bp.Materials).Count);
    }

    [Fact]
    public async Task Missing_or_out_of_range_material_count_is_null_not_zero()
    {
        var (client, h, _) = Make();
        h.Json(Reply("""
            [ { "engineerName": "E", "blueprints": [ { "blueprintName": "b", "blueprintGrade": 99999999999,
                  "materials": [ { "materialName": "NoCount" }, { "materialName": "Huge", "materialCount": 9999999999 }, { "materialCount": 3 } ] } ] } ]
            """));

        var bp = Assert.Single(Assert.Single((await client.GetEngineersAsync(Identity)).Value!).Blueprints);
        Assert.Null(bp.Grade);
        Assert.Equal(2, bp.Materials.Count); // nameless material skipped
        Assert.All(bp.Materials, m => Assert.Null(m.Count));
    }

    [Theory]
    [InlineData("""{ "engineers": [ { "engineerName": "E" } ] }""")]            // wrapper object
    [InlineData("""[ { "foo": 1 }, { "bar": 2 } ]""")]                           // unknown keys
    public async Task Guessed_shape_that_does_not_match_is_an_error_and_is_not_cached(string eventData)
    {
        var (client, h, clock) = Make();
        h.Json(Reply(eventData));
        var r = await client.GetEngineersAsync(Identity);
        Assert.Equal(InaraReadStatus.Error, r.Status);
        Assert.Null(r.Value);

        clock.Advance(TimeSpan.FromSeconds(31)); // not cached: a retry goes live instead of replaying junk for 24h
        h.Json(Reply("""[ { "engineerName": "E" } ]"""));
        Assert.Equal(InaraReadStatus.Ok, (await client.GetEngineersAsync(Identity)).Status);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Profile_with_no_recognised_fields_is_an_error()
    {
        var (client, h, _) = Make();
        h.Json(Reply("""{ "something": "else" }"""));
        Assert.Equal(InaraReadStatus.Error, (await client.GetCommanderProfileAsync(Identity)).Status);
    }

    [Fact]
    public async Task Empty_list_is_a_valid_answer_and_null_data_is_NotFound()
    {
        var (client, h, clock) = Make();
        h.Json(Reply("[]"));
        var empty = await client.GetCommunityGoalsAsync(Identity);
        Assert.True(empty.HasValue);
        Assert.Empty(empty.Value!);

        clock.Advance(TimeSpan.FromMinutes(20));
        h.Json(Reply("null"));
        Assert.Equal(InaraReadStatus.NotFound, (await client.GetCommunityGoalsAsync(Identity)).Status);
    }

    [Fact]
    public async Task Community_goals_parse_with_tiers_and_missing_fields()
    {
        var (client, h, _) = Make();
        h.Json(Reply("""
            [ { "communitygoalGameID": 900, "communitygoalName": "Defend Foo", "goalExpiry": "2026-02-01T00:00:00Z",
                "tiers": [ { "tierName": "Tier 1", "tierTarget": 100000 } ] },
              { "communitygoalName": "Bare" } ]
            """));

        var goals = (await client.GetCommunityGoalsAsync(Identity)).Value!;
        Assert.Equal(900, goals[0].Id);
        Assert.NotNull(goals[0].Expires);
        Assert.Equal(100000, Assert.Single(goals[0].Tiers).Target);
        Assert.Null(goals[1].Id);
        Assert.Empty(goals[1].Tiers);
    }

    [Fact]
    public async Task Cache_evicts_the_oldest_entry_beyond_MaxCacheEntries()
    {
        var (client, h, clock) = Make(opts: new InaraReadOptions { MaxCacheEntries = 1 });
        h.Json(Reply(ProfileJson));
        h.Json(Reply("[]"));
        h.Json(Reply(ProfileJson));

        await client.GetCommanderProfileAsync(Identity);
        clock.Advance(TimeSpan.FromSeconds(31));
        await client.GetCommunityGoalsAsync(Identity); // evicts the profile
        clock.Advance(TimeSpan.FromSeconds(31));
        var again = await client.GetCommanderProfileAsync(Identity);
        Assert.False(again.FromCache);
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task Event_204_is_NotFound_and_garbage_or_network_failure_never_throws()
    {
        var (client, h, clock) = Make();
        h.Json("""{ "header": { "eventStatus": 200 }, "events": [ { "eventStatus": 204, "eventStatusText": "not found" } ] }""");
        Assert.Equal(InaraReadStatus.NotFound, (await client.GetCommanderProfileAsync(Identity, "nobody")).Status);

        clock.Advance(TimeSpan.FromMinutes(1));
        h.Json("<html>nope</html>");
        Assert.Equal(InaraReadStatus.Error, (await client.GetEngineersAsync(Identity)).Status);

        clock.Advance(TimeSpan.FromMinutes(1));
        h.Replies.Enqueue(() => throw new HttpRequestException("boom"));
        Assert.Equal(InaraReadStatus.Error, (await client.GetCommunityGoalsAsync(Identity)).Status);
    }
}
