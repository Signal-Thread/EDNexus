using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EDNexus.Core.Colonisation;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Tests.Reporting;   // the shared RecordingHandler test double
using EliteDangerous.RavenColonial;
using Xunit;

namespace EDNexus.Tests.Colonisation;

/// <summary>
/// The opt-in delivery push-up. Everything runs against a fake contributor or a fake HTTP handler:
/// nothing here may ever reach Raven Colonial.
/// </summary>
public class RavenContributionSyncTests
{
    private sealed record Posted(string BuildId, string Commander, IReadOnlyDictionary<string, int> Deltas);

    private sealed class FakeContributor : ISharedProjectContributor
    {
        public SharedProjectTargetResult Target { get; set; } = new(Tracked());
        public Exception? ThrowOnFind { get; set; }
        public Exception? ThrowOnContribute { get; set; }
        public string? ContributeError { get; set; }
        public int FindCalls;
        public List<Posted> Posts { get; } = new();

        public Task<SharedProjectTargetResult> FindTargetAsync(string systemName, long marketId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FindCalls);
            if (ThrowOnFind is not null) throw ThrowOnFind;
            return Task.FromResult(Target);
        }

        public Task<string?> ContributeAsync(string buildId, string commander, IReadOnlyDictionary<string, int> deltas, CancellationToken ct = default)
        {
            if (ThrowOnContribute is not null) throw ThrowOnContribute;
            lock (Posts) Posts.Add(new Posted(buildId, commander, new Dictionary<string, int>(deltas)));
            return Task.FromResult(ContributeError);
        }
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>A project that lists aluminium and steel, spelled the way Raven spells them.</summary>
    private static SharedProjectTarget Tracked(bool complete = false) => new(
        "build-1", "Hutton Annex", complete,
        new Dictionary<string, string> { ["aluminium"] = "Aluminium", ["steel"] = "Steel" });

    private sealed class Rig : IDisposable
    {
        public JournalEventBus Bus { get; } = new();
        public CommanderState State { get; } = new() { Name = "Jane Doe", StarSystem = "Sol" };
        public FakeContributor Contributor { get; } = new();
        public bool Enabled { get; set; } = true;
        public bool Suppressed { get; set; }
        public MovableClock Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        public RavenContributionSync Sync { get; }
        public List<Exception> HandlerErrors { get; } = new();

        public Rig()
        {
            Sync = new RavenContributionSync(Bus, State, Contributor, () => Enabled, () => Suppressed, Clock);
            Bus.HandlerError += (_, ex) => HandlerErrors.Add(ex);
        }

        public async Task PublishAsync(string json, bool historical = false)
        {
            Assert.True(JournalEntry.TryParse(json, historical, out var entry), "sample JSON failed to parse");
            Bus.Publish(entry);
            await Sync.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Dispose() => Sync.Dispose();
    }

    private static string Contribution(string timestamp = "2026-10-01T10:00:00Z", long marketId = 3956023042, string contributions =
        """{ "Name":"$Aluminium_name;", "Name_Localised":"Aluminium", "Amount":50 }""")
        => $$"""{ "timestamp":"{{timestamp}}", "event":"ColonisationContribution", "MarketID":{{marketId}}, "Contributions":[ {{contributions}} ] }""";

    // --- When it sends ---

    [Fact]
    public async Task A_live_delivery_is_sent_once_under_the_projects_own_spelling()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Contribution());

        var post = Assert.Single(rig.Contributor.Posts);
        Assert.Equal("build-1", post.BuildId);
        Assert.Equal("Jane Doe", post.Commander);
        Assert.Equal(50, post.Deltas["Aluminium"]);    // the project's spelling, not the journal's "$Aluminium_name;"
        Assert.Single(post.Deltas);
        Assert.Empty(rig.HandlerErrors);
    }

    [Fact]
    public async Task One_event_with_several_commodities_is_one_post_and_repeated_names_add_up()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Contribution(contributions: """
            { "Name":"$Aluminium_name;", "Amount":50 },
            { "Name":"$steel_name;", "Amount":20 },
            { "Name":"$aluminium_name;", "Amount":5 }
            """));

        var post = Assert.Single(rig.Contributor.Posts);
        Assert.Equal(55, post.Deltas["Aluminium"]);
        Assert.Equal(20, post.Deltas["Steel"]);
    }

    [Fact]
    public async Task A_replayed_event_is_never_sent()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Contribution(), historical: true);

        Assert.Empty(rig.Contributor.Posts);
        Assert.Equal(0, rig.Contributor.FindCalls);
    }

    [Fact]
    public async Task Nothing_is_sent_while_the_setting_is_off_and_turning_it_on_takes_effect_live()
    {
        using var rig = new Rig { Enabled = false };

        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:00:00Z"));
        Assert.Empty(rig.Contributor.Posts);
        Assert.Equal(0, rig.Contributor.FindCalls);   // not even a lookup: no data leaves while off

        rig.Enabled = true;
        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:05:00Z"));
        Assert.Single(rig.Contributor.Posts);
    }

    [Fact]
    public async Task Nothing_is_sent_in_developer_mode()
    {
        using var rig = new Rig { Suppressed = true };

        await rig.PublishAsync(Contribution());

        Assert.Empty(rig.Contributor.Posts);
        Assert.Equal(0, rig.Contributor.FindCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Nothing_is_sent_without_a_commander_name(string? name)
    {
        using var rig = new Rig();
        rig.State.Name = name;

        await rig.PublishAsync(Contribution());

        Assert.Empty(rig.Contributor.Posts);
        Assert.Equal(0, rig.Contributor.FindCalls);
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_known_system()
    {
        using var rig = new Rig();
        rig.State.StarSystem = null;

        await rig.PublishAsync(Contribution());

        Assert.Empty(rig.Contributor.Posts);
    }

    [Fact]
    public async Task Nothing_is_sent_when_no_shared_project_matches_the_depot()
    {
        using var rig = new Rig();
        rig.Contributor.Target = new SharedProjectTargetResult(null);

        await rig.PublishAsync(Contribution());

        Assert.Empty(rig.Contributor.Posts);
        Assert.Equal(1, rig.Contributor.FindCalls);
    }

    [Fact]
    public async Task Nothing_is_sent_to_a_project_that_is_already_complete()
    {
        using var rig = new Rig();
        rig.Contributor.Target = new SharedProjectTargetResult(Tracked(complete: true));

        await rig.PublishAsync(Contribution());

        Assert.Empty(rig.Contributor.Posts);
    }

    [Fact]
    public async Task Commodities_the_project_does_not_list_are_skipped_not_guessed()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Contribution(contributions: """
            { "Name":"$Aluminium_name;", "Amount":50 },
            { "Name":"$unobtainium_name;", "Amount":9 }
            """));

        var post = Assert.Single(rig.Contributor.Posts);
        Assert.Equal(new[] { "Aluminium" }, post.Deltas.Keys);
    }

    [Fact]
    public async Task A_delivery_of_only_unlisted_commodities_sends_nothing()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Contribution(contributions: """{ "Name":"$unobtainium_name;", "Amount":9 }"""));

        Assert.Empty(rig.Contributor.Posts);
    }

    [Theory]
    [InlineData("""{ "Name":"$Aluminium_name;", "Amount":0 }""")]
    [InlineData("""{ "Name":"$Aluminium_name;", "Amount":-4 }""")]
    [InlineData("""{ "Name":"$Aluminium_name;" }""")]
    [InlineData("""{ "Name":"$Aluminium_name;", "Amount":"lots" }""")]
    [InlineData("""{ "Amount":10 }""")]
    [InlineData("""5""")]
    public async Task Malformed_or_non_positive_lines_are_ignored(string line)
    {
        using var rig = new Rig();

        await rig.PublishAsync(Contribution(contributions: line));

        Assert.Empty(rig.Contributor.Posts);
        Assert.Empty(rig.HandlerErrors);
    }

    [Theory]
    [InlineData("""{ "timestamp":"2026-10-01T10:00:00Z", "event":"ColonisationContribution", "Contributions":[ { "Name":"steel", "Amount":1 } ] }""")]
    [InlineData("""{ "timestamp":"2026-10-01T10:00:00Z", "event":"ColonisationContribution", "MarketID":5 }""")]
    [InlineData("""{ "timestamp":"2026-10-01T10:00:00Z", "event":"ColonisationContribution", "MarketID":5, "Contributions":"x" }""")]
    public async Task An_event_missing_its_market_or_contributions_is_ignored(string json)
    {
        using var rig = new Rig();

        await rig.PublishAsync(json);

        Assert.Empty(rig.Contributor.Posts);
        Assert.Empty(rig.HandlerErrors);
    }

    // --- Never twice ---

    [Fact]
    public async Task The_same_event_delivered_twice_is_reported_once()
    {
        using var rig = new Rig();
        var line = Contribution();

        await rig.PublishAsync(line);
        await rig.PublishAsync(line);

        Assert.Single(rig.Contributor.Posts);
    }

    [Fact]
    public async Task Two_genuinely_separate_deliveries_are_both_reported()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:00:00Z"));
        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:00:09Z"));

        Assert.Equal(2, rig.Contributor.Posts.Count);
    }

    // --- Project cache ---

    [Fact]
    public async Task The_matched_project_is_looked_up_once_per_depot_not_per_event()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:00:00Z"));
        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:01:00Z"));
        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:02:00Z"));

        Assert.Equal(3, rig.Contributor.Posts.Count);
        Assert.Equal(1, rig.Contributor.FindCalls);
    }

    [Fact]
    public async Task A_different_depot_is_looked_up_separately()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Contribution(marketId: 1));
        await rig.PublishAsync(Contribution(marketId: 2));

        Assert.Equal(2, rig.Contributor.FindCalls);
    }

    [Fact]
    public async Task A_depot_with_no_project_is_not_asked_about_again_until_the_cache_expires()
    {
        using var rig = new Rig();
        rig.Contributor.Target = new SharedProjectTargetResult(null);

        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:00:00Z"));
        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:01:00Z"));
        Assert.Equal(1, rig.Contributor.FindCalls);

        // A project registered mid-session must be found eventually.
        rig.Clock.Advance(RavenContributionSync.NoTargetTtl + TimeSpan.FromSeconds(1));
        rig.Contributor.Target = new SharedProjectTargetResult(Tracked());
        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:10:00Z"));

        Assert.Equal(2, rig.Contributor.FindCalls);
        Assert.Single(rig.Contributor.Posts);
    }

    [Fact]
    public async Task A_matched_project_is_refreshed_once_its_cache_expires()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:00:00Z"));
        rig.Clock.Advance(RavenContributionSync.TargetTtl + TimeSpan.FromSeconds(1));
        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T11:00:00Z"));

        Assert.Equal(2, rig.Contributor.FindCalls);
    }

    [Fact]
    public async Task A_failed_lookup_is_not_cached_so_the_next_delivery_tries_again()
    {
        using var rig = new Rig();
        rig.Contributor.Target = new SharedProjectTargetResult(null, "HTTP 503");

        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:00:00Z"));
        Assert.Empty(rig.Contributor.Posts);

        rig.Contributor.Target = new SharedProjectTargetResult(Tracked());
        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:01:00Z"));

        Assert.Equal(2, rig.Contributor.FindCalls);
        Assert.Single(rig.Contributor.Posts);
    }

    // --- Failure isolation ---

    [Fact]
    public async Task A_failed_post_is_not_retried_and_does_not_disturb_the_next_delivery()
    {
        using var rig = new Rig();
        rig.Contributor.ContributeError = "HTTP 500";

        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:00:00Z"));
        Assert.Single(rig.Contributor.Posts);          // tried once, and not again

        rig.Contributor.ContributeError = null;
        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:01:00Z"));
        Assert.Equal(2, rig.Contributor.Posts.Count);
        Assert.Empty(rig.HandlerErrors);
    }

    [Fact]
    public async Task A_contributor_that_throws_never_reaches_the_bus_and_the_worker_keeps_going()
    {
        using var rig = new Rig();
        rig.Contributor.ThrowOnContribute = new InvalidOperationException("boom");

        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:00:00Z"));

        rig.Contributor.ThrowOnContribute = null;
        await rig.PublishAsync(Contribution(timestamp: "2026-10-01T10:01:00Z"));

        Assert.Empty(rig.HandlerErrors);
        Assert.Single(rig.Contributor.Posts);
    }

    [Fact]
    public async Task A_lookup_that_throws_never_reaches_the_bus()
    {
        using var rig = new Rig();
        rig.Contributor.ThrowOnFind = new HttpRequestException("down");

        await rig.PublishAsync(Contribution());

        Assert.Empty(rig.HandlerErrors);
        Assert.Empty(rig.Contributor.Posts);
    }

    [Fact]
    public async Task Publishing_does_not_wait_for_a_slow_tracker()
    {
        var gate = new TaskCompletionSource();
        var slow = new GatedContributor(gate.Task);
        var bus = new JournalEventBus();
        using var sync = new RavenContributionSync(bus, new CommanderState { Name = "Jane", StarSystem = "Sol" }, slow, () => true);

        Assert.True(JournalEntry.TryParse(Contribution(), false, out var entry));
        bus.Publish(entry);                            // returns although the tracker is still "busy"

        Assert.False(sync.WhenIdleAsync().IsCompleted);
        gate.SetResult();
        await sync.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, slow.Posts);
    }

    private sealed class GatedContributor(Task gate) : ISharedProjectContributor
    {
        public int Posts;
        public async Task<SharedProjectTargetResult> FindTargetAsync(string systemName, long marketId, CancellationToken ct = default)
        {
            await gate;
            return new SharedProjectTargetResult(Tracked());
        }

        public Task<string?> ContributeAsync(string buildId, string commander, IReadOnlyDictionary<string, int> deltas, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Posts);
            return Task.FromResult<string?>(null);
        }
    }

    // --- Lifetime ---

    [Fact]
    public async Task After_dispose_nothing_is_sent_and_disposing_twice_is_harmless()
    {
        var rig = new Rig();
        rig.Dispose();
        rig.Dispose();

        await rig.PublishAsync(Contribution());

        Assert.Empty(rig.Contributor.Posts);
        Assert.Empty(rig.HandlerErrors);
    }

    // --- The Raven adapter: journal names to the project's own spelling ---

    private const string ListJson = """[ { "buildId": "build-9", "marketId": 3956023042, "systemName": "Sol" } ]""";

    private const string ProjectJson = """
    { "buildId": "build-9", "buildName": "Hutton Annex", "buildType": "t", "systemName": "Sol", "complete": false,
      "commodities": { "Aluminium": 100, "cmmcomposite": 40, "Fruit And Vegetables": 5 } }
    """;

    [Fact]
    public async Task The_adapter_maps_journal_symbols_onto_the_projects_own_commodity_names()
    {
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? ListJson : ProjectJson));
        var contributor = new RavenColonialContributor(new RavenColonialClient(
            new RavenColonialClientOptions { SoftwareName = "T", SoftwareVersion = "1", BaseUrl = "https://raven.test" },
            new HttpClient(handler)));

        var found = await contributor.FindTargetAsync("Sol", 3956023042);

        Assert.Null(found.Error);
        var target = found.Target!;
        Assert.Equal("build-9", target.BuildId);
        Assert.Equal("Aluminium", target.CommodityNames["aluminium"]);
        Assert.Equal("cmmcomposite", target.CommodityNames["cmmcomposite"]);
        Assert.Equal("Fruit And Vegetables", target.CommodityNames["fruitandvegetables"]);
        Assert.False(target.CommodityNames.ContainsKey("steel"));   // not listed: no entry, so it is never guessed
    }

    [Fact]
    public async Task The_adapter_tells_no_project_apart_from_an_unreachable_tracker()
    {
        RavenColonialContributor Over(RecordingHandler h) => new(new RavenColonialClient(
            new RavenColonialClientOptions { SoftwareName = "T", SoftwareVersion = "1", BaseUrl = "https://raven.test", TransientRetryDelay = TimeSpan.Zero },
            new HttpClient(h)));

        var none = await Over(new RecordingHandler(body: "[]")).FindTargetAsync("Sol", 1);
        Assert.Null(none.Target);
        Assert.Null(none.Error);

        var down = await Over(new RecordingHandler(HttpStatusCode.InternalServerError, "x")).FindTargetAsync("Sol", 1);
        Assert.Null(down.Target);
        Assert.NotNull(down.Error);
    }

    [Fact]
    public async Task The_adapter_posts_through_the_client_and_reports_failures_as_text()
    {
        var ok = new RecordingHandler(body: "");
        var contributor = new RavenColonialContributor(new RavenColonialClient(
            new RavenColonialClientOptions { SoftwareName = "T", SoftwareVersion = "1", BaseUrl = "https://raven.test" },
            new HttpClient(ok)));

        Assert.Null(await contributor.ContributeAsync("build-9", "Jane", new Dictionary<string, int> { ["Aluminium"] = 4 }));
        Assert.Equal("""{"Aluminium":4}""", ok.Bodies.Single());

        var bad = new RavenColonialContributor(new RavenColonialClient(
            new RavenColonialClientOptions { SoftwareName = "T", SoftwareVersion = "1", BaseUrl = "https://raven.test" },
            new HttpClient(new RecordingHandler(HttpStatusCode.BadRequest, ""))));
        Assert.Contains("400", await bad.ContributeAsync("build-9", "Jane", new Dictionary<string, int> { ["Aluminium"] = 4 }));
    }

    // --- Settings ---

    [Fact]
    public void Sharing_deliveries_is_off_by_default()
    {
        Assert.False(new ColonisationSettings().ShareDeliveries);
        Assert.False(new AppSettings().Colonisation.ShareDeliveries);
    }
}
