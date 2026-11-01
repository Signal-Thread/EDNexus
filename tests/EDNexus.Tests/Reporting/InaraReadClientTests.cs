using System.Net;
using System.Text;
using EliteDangerous.Inara;
using Xunit;

namespace EDNexus.Tests.Reporting;

public class InaraReadClientTests
{
    private static readonly InaraClientOptions Options = new() { AppName = "EDNexus.Tests", AppVersion = "1.0.0", IsBeingDeveloped = true };
    private static readonly InaraIdentity Identity = new() { ApiKey = "secret-key", CommanderName = "Jameson" };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Queue<Func<HttpResponseMessage>> Replies { get; } = new();
        public List<string> Bodies { get; } = new();
        public int Calls => Bodies.Count;
        public void Json(string json) => Replies.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return Replies.Dequeue()();
        }
    }

    private static (InaraReadClient Client, Handler Handler, Clock Clock) Make(Func<bool>? enabled = null, InaraReadOptions? opts = null)
    {
        var h = new Handler();
        var c = new Clock();
        return (new InaraReadClient(Options, new HttpClient(h), enabled, opts, c), h, c);
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
        Assert.Contains("\"eventName\":\"getCommanderProfile\"", h.Bodies[0]);
        Assert.Contains("\"APIkey\":\"secret-key\"", h.Bodies[0]);
        Assert.Contains("\"searchName\":\"Jameson\"", h.Bodies[0]);
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
        Assert.Equal(1, h.Calls);

        clock.Now += TimeSpan.FromMinutes(11);
        var fresh = await client.GetCommanderProfileAsync(Identity);
        Assert.False(fresh.FromCache);
        Assert.Equal(2, h.Calls);
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
    public async Task Local_limiter_blocks_back_to_back_live_requests()
    {
        var (client, h, clock) = Make();
        h.Json(Reply(ProfileJson));
        h.Json(Reply("[]"));

        await client.GetCommanderProfileAsync(Identity);
        var limited = await client.GetCommunityGoalsAsync(Identity);
        Assert.Equal(InaraReadStatus.RateLimited, limited.Status);
        Assert.Equal(1, h.Calls);

        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Equal(InaraReadStatus.Ok, (await client.GetCommunityGoalsAsync(Identity)).Status);
    }

    [Fact]
    public async Task Http_429_honours_retry_after_and_serves_stale_cache()
    {
        var (client, h, clock) = Make();
        h.Json(Reply(ProfileJson));
        await client.GetCommanderProfileAsync(Identity);

        clock.Now += TimeSpan.FromMinutes(11); // cache expired
        h.Replies.Enqueue(() =>
        {
            var m = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return m;
        });

        var r = await client.GetCommanderProfileAsync(Identity);
        Assert.True(r.HasValue);
        Assert.True(r.IsStale);
        Assert.Equal(TimeSpan.FromMinutes(5), r.RetryAfter);

        // Still cooling down: no further request.
        clock.Now += TimeSpan.FromMinutes(1);
        await client.GetCommanderProfileAsync(Identity);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Rate_limit_wording_in_body_without_cache_is_RateLimited()
    {
        var (client, h, _) = Make();
        h.Json("""{ "header": { "eventStatus": 400, "eventStatusText": "Too many requests" } }""");
        var r = await client.GetCommunityGoalsAsync(Identity);
        Assert.Equal(InaraReadStatus.RateLimited, r.Status);
    }

    [Fact]
    public async Task Bad_key_is_Rejected()
    {
        var (client, h, _) = Make();
        h.Json("""{ "header": { "eventStatus": 400, "eventStatusText": "Invalid API key" } }""");
        var r = await client.GetEngineersAsync(Identity);
        Assert.Equal(InaraReadStatus.Rejected, r.Status);
    }

    [Fact]
    public async Task Nearest_services_parse_and_skip_non_objects()
    {
        var (client, h, _) = Make();
        h.Json(Reply("""[ { "stationName": "Jameson Memorial", "starsystemName": "Shinrarta Dezhra", "distance": 0.0, "distanceToArrival": 123.4, "marketID": 42 }, 7, {} ]"""));

        var r = await client.GetNearestServicesAsync(Identity, "Sol", InaraServiceKind.MaterialTrader);

        Assert.Equal(2, r.Value!.Count);
        Assert.Equal("Jameson Memorial", r.Value[0].Name);
        Assert.Equal(42, r.Value[0].MarketId);
        Assert.Null(r.Value[1].Name);
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
    public async Task Event_204_is_NotFound_and_garbage_or_network_failure_never_throws()
    {
        var (client, h, clock) = Make();
        h.Json("""{ "header": { "eventStatus": 200 }, "events": [ { "eventStatus": 204, "eventStatusText": "not found" } ] }""");
        Assert.Equal(InaraReadStatus.NotFound, (await client.GetCommanderProfileAsync(Identity, "nobody")).Status);

        clock.Now += TimeSpan.FromMinutes(1);
        h.Json("<html>nope</html>");
        Assert.Equal(InaraReadStatus.Error, (await client.GetEngineersAsync(Identity)).Status);

        clock.Now += TimeSpan.FromMinutes(1);
        h.Replies.Enqueue(() => throw new HttpRequestException("boom"));
        Assert.Equal(InaraReadStatus.Error, (await client.GetCommunityGoalsAsync(Identity)).Status);
    }
}
