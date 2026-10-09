using System.Net;
using System.Text.Json;
using EDNexus.Ebs.Models;
using EDNexus.Ebs.Options;
using EDNexus.Ebs.Security;
using EDNexus.Ebs.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EDNexus.Ebs.Tests;

public class OAuthPendingStateTests
{
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PendingBroadcasterAuth Auth() =>
        new("1", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4), "challenge", "http://localhost:1/cb", default);

    [Fact]
    public void Abandoned_sessions_and_codes_are_swept_on_the_next_insert()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var state = new OAuthPendingState(time);
        for (var i = 0; i < 10; i++)
        {
            state.CreateSession("http://localhost:1/cb", "s", "c", TimeSpan.FromMinutes(10));
            state.CreateAuthorizationCode(Auth(), TimeSpan.FromSeconds(60));
        }

        time.Now += TimeSpan.FromMinutes(11); // nobody redeemed any of them
        state.CreateSession("http://localhost:1/cb", "s", "c", TimeSpan.FromMinutes(10));
        state.CreateAuthorizationCode(Auth(), TimeSpan.FromSeconds(60));

        Assert.Equal(1, state.SessionCount);
        Assert.Equal(1, state.CodeCount); // the abandoned codes no longer hold Twitch refresh tokens in memory
    }

    [Fact]
    public void Live_entries_survive_a_sweep()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var state = new OAuthPendingState(time);
        var live = state.CreateSession("http://localhost:1/cb", "s", "c", TimeSpan.FromMinutes(10));

        time.Now += OAuthPendingState.SweepInterval + TimeSpan.FromSeconds(1);
        state.CreateSession("http://localhost:1/cb", "s2", "c", TimeSpan.FromMinutes(10));

        Assert.Equal(2, state.SessionCount);
        Assert.True(state.TryConsumeSession(live, out _));
    }

    [Fact]
    public void The_maps_are_capped_and_the_entry_closest_to_expiry_is_evicted()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var state = new OAuthPendingState(time, maxEntries: 3);
        var oldest = state.CreateSession("http://localhost:1/cb", "s", "c", TimeSpan.FromMinutes(1));
        var kept = new[]
        {
            state.CreateSession("http://localhost:1/cb", "s", "c", TimeSpan.FromMinutes(10)),
            state.CreateSession("http://localhost:1/cb", "s", "c", TimeSpan.FromMinutes(10)),
        };

        var newest = state.CreateSession("http://localhost:1/cb", "s", "c", TimeSpan.FromMinutes(10));

        Assert.Equal(3, state.SessionCount);
        Assert.False(state.TryConsumeSession(oldest, out _));
        Assert.True(state.TryConsumeSession(newest, out _));
        Assert.All(kept, id => Assert.True(state.TryConsumeSession(id, out _)));
    }

    [Fact]
    public void The_authorization_code_map_is_capped_too()
    {
        var state = new OAuthPendingState(new FakeTimeProvider(DateTimeOffset.UtcNow), maxEntries: 2);

        for (var i = 0; i < 20; i++)
            state.CreateAuthorizationCode(Auth(), TimeSpan.FromSeconds(60));

        Assert.Equal(2, state.CodeCount);
    }
}

public class ChannelStatePruneBackgroundServiceTests
{
    private sealed class CountingStore(Func<int> prune) : IChannelStateStore
    {
        public int Calls { get; private set; }
        public void Set(string channelId, JsonElement state) { }
        public bool TryGet(string channelId, out JsonElement state) { state = default; return false; }
        public void Remove(string channelId) { }
        public int PruneExpired() { Calls++; return prune(); }
    }

    private static ChannelStatePruneBackgroundService Create(IChannelStateStore store) =>
        new(store, Microsoft.Extensions.Options.Options.Create(new EbsOptions()), TimeProvider.System, NullLogger<ChannelStatePruneBackgroundService>.Instance);

    [Fact]
    public void A_pass_asks_the_store_to_prune()
    {
        var store = new CountingStore(() => 3);

        Create(store).RunOnce();

        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public void A_failing_prune_is_swallowed_so_the_timer_keeps_running()
    {
        var store = new CountingStore(() => throw new InvalidOperationException("database is locked"));

        Assert.Null(Record.Exception(() => Create(store).RunOnce()));
        Assert.Equal(1, store.Calls);
    }
}

public class TwitchPubSubClientHeaderTests
{
    private static TwitchPubSubClient Create(RecordingHandler handler, TwitchEbsOptions twitch) => new(
        new HttpClient(handler),
        new TwitchExtensionJwtService(Microsoft.Extensions.Options.Options.Create(twitch)),
        Microsoft.Extensions.Options.Options.Create(twitch),
        Microsoft.Extensions.Options.Options.Create(new EbsOptions()),
        NullLogger<TwitchPubSubClient>.Instance);

    private const string Secret = "c3VwZXItc2VjcmV0LWV4dGVuc2lvbi1rZXktMTIzNA==";

    [Fact]
    public async Task The_Client_Id_header_is_the_extensions_id_not_the_oauth_applications()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var client = Create(handler, new TwitchEbsOptions { ClientId = "oauth-app", ExtensionId = "the-extension", ExtensionSecret = Secret });

        Assert.True(await client.BroadcastAsync("123", JsonSerializer.SerializeToElement(new { v = 1 }), CancellationToken.None));

        Assert.Equal("the-extension", Assert.Single(handler.ClientIds));
    }

    [Fact]
    public async Task Without_an_extension_id_the_applications_client_id_is_used()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var client = Create(handler, new TwitchEbsOptions { ClientId = "oauth-app", ExtensionId = "", ExtensionSecret = Secret });

        await client.BroadcastAsync("123", JsonSerializer.SerializeToElement(new { v = 1 }), CancellationToken.None);

        Assert.Equal("oauth-app", Assert.Single(handler.ClientIds));
    }
}

public class PubSubStateValidationTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("\"text\"")]
    [InlineData("5")]
    [InlineData("true")]
    public void Create_rejects_a_state_that_is_not_a_json_object(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Throws<InvalidPubSubStateException>(() => PubSubBroadcastRequest.Create("1", document.RootElement));
    }

    [Fact]
    public void Create_rejects_an_undefined_state_instead_of_throwing_from_the_serializer()
    {
        // What a request body with no "state" property binds to.
        Assert.Throws<InvalidPubSubStateException>(() => PubSubBroadcastRequest.Create("1", default));
    }
}

public class TwitchOAuthExceptionTests
{
    [Theory]
    [InlineData(400, "Invalid refresh token", true)]
    [InlineData(401, "Unauthorized", true)]
    [InlineData(400, "invalid client", false)]
    [InlineData(400, "Invalid Client Secret", false)]
    [InlineData(429, "Too Many Requests", false)]
    [InlineData(500, "oops", false)]
    [InlineData(503, "oops", false)]
    [InlineData(null, "Unparseable Twitch token response", false)]
    public void Only_a_400_or_401_for_the_grant_counts_as_a_rejection(int? status, string message, bool rejection)
    {
        Assert.Equal(rejection, new TwitchOAuthException(message, status).IsGrantRejection);
    }
}
