using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EDNexus.Ebs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EDNexus.Ebs.Tests;

/// <summary>
/// End-to-end coverage for <c>POST /api/update-state</c> and <c>GET /api/initial-state/{channelId}</c>
/// via <see cref="WebApplicationFactory{TEntryPoint}"/> — properties the unit tests around individual
/// services alone cannot exercise: the actual auth-failure status codes for the EBS-issued
/// broadcaster-token scheme, that the channel id used for the broadcast/rate-limit comes only from
/// the verified token (never the request body), and the CORS policy on the browser-facing endpoint.
/// </summary>
public class UpdateStateEndpointTests : IClassFixture<UpdateStateEndpointTests.Factory>
{
    private readonly Factory _factory;

    public UpdateStateEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task UpdateState_without_authorization_header_is_unauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/update-state", new { state = new { foo = "bar" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateState_rejects_an_unknown_token()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-real-token");

        var response = await client.PostAsJsonAsync("/api/update-state", new { state = new { foo = "bar" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateState_rejects_a_token_whose_underlying_Twitch_grant_was_invalidated()
    {
        var record = _factory.TokenStore.IssueToken("chan-invalid-grant", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        _factory.TokenStore.MarkTwitchGrantInvalid("chan-invalid-grant");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", record.Token);

        var response = await client.PostAsJsonAsync("/api/update-state", new { state = new { foo = "bar" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateState_accepts_a_valid_broadcaster_token_and_broadcasts_to_its_own_channel()
    {
        var record = _factory.TokenStore.IssueToken("chan-valid", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", record.Token);

        var response = await client.PostAsJsonAsync("/api/update-state", new { state = new { foo = "bar" } });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("chan-valid", body.GetProperty("channelId").GetString());
        Assert.True(_factory.PubSubClient.BroadcastCalls.ContainsKey("chan-valid"));
    }

    [Fact]
    public async Task Distinct_channels_get_independent_rate_limit_budgets_instead_of_sharing_one_ip_bucket()
    {
        // Regression test for the bug where the partition-key callback ran before the endpoint had a
        // chance to record the authenticated channel id in HttpContext.Items, so every request fell
        // back to partitioning by RemoteIpAddress — which TestServer reports as null/"unknown" for
        // every in-process request, meaning every channel used to share exactly one bucket.
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ebs:UpdateStateRateLimit"] = "1",
                ["Ebs:UpdateStateRateLimitWindowSeconds"] = "60",
            })));
        var tokenStore = factory.Services.GetRequiredService<IBroadcasterTokenStore>();
        var recordA = tokenStore.IssueToken("chan-a", "A", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        var recordB = tokenStore.IssueToken("chan-b", "B", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));

        var clientA = factory.CreateClient();
        clientA.DefaultRequestHeaders.Authorization = new("Bearer", recordA.Token);
        var clientB = factory.CreateClient();
        clientB.DefaultRequestHeaders.Authorization = new("Bearer", recordB.Token);

        var firstA = await clientA.PostAsJsonAsync("/api/update-state", new { state = new { foo = "bar" } });
        var firstB = await clientB.PostAsJsonAsync("/api/update-state", new { state = new { foo = "bar" } });
        var secondA = await clientA.PostAsJsonAsync("/api/update-state", new { state = new { foo = "bar" } });

        Assert.Equal(HttpStatusCode.OK, firstA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstB.StatusCode); // would be 429 under the old IP-fallback bug
        Assert.Equal(HttpStatusCode.TooManyRequests, secondA.StatusCode); // channel A's own budget is still enforced
    }

    [Fact]
    public async Task ClearState_forgets_the_stored_snapshot_and_tells_viewers_the_card_is_offline()
    {
        var record = _factory.TokenStore.IssueToken("chan-clear", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", record.Token);
        (await client.PostAsJsonAsync("/api/update-state", new { state = new { headline = "Docked at Home" } })).EnsureSuccessStatusCode();

        var cleared = await client.DeleteAsync("/api/update-state");

        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync("/api/initial-state/chan-clear")).StatusCode);
        Assert.True(_factory.PubSubClient.LastMessage["chan-clear"].GetProperty("offline").GetBoolean());
    }

    [Fact]
    public async Task ClearState_requires_a_valid_broadcaster_token()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-real-token");

        var response = await client.DeleteAsync("/api/update-state");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signing_out_also_takes_the_card_off_the_air()
    {
        var record = _factory.TokenStore.IssueToken("chan-signout", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", record.Token);
        (await client.PostAsJsonAsync("/api/update-state", new { state = new { headline = "Docked at Home" } })).EnsureSuccessStatusCode();

        (await client.PostAsync("/oauth/revoke", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync("/api/initial-state/chan-signout")).StatusCode);
        Assert.True(_factory.PubSubClient.LastMessage["chan-signout"].GetProperty("offline").GetBoolean());
    }

    [Fact]
    public async Task A_broadcaster_whose_Twitch_grant_lapsed_can_still_take_the_card_down()
    {
        var record = _factory.TokenStore.IssueToken("chan-lapsed-clear", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", record.Token);
        (await client.PostAsJsonAsync("/api/update-state", new { state = new { headline = "Docked at Home" } })).EnsureSuccessStatusCode();

        // A failed background refresh. Publishing now needs a new sign-in; clearing must not.
        _factory.TokenStore.MarkTwitchGrantInvalid("chan-lapsed-clear");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/update-state", new { state = new { headline = "Still here" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/update-state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync("/api/initial-state/chan-lapsed-clear")).StatusCode);
    }

    [Theory]
    [InlineData("1", false)]  // shorter than the app's refresh: live cards would vanish
    [InlineData("11", false)]
    [InlineData("12", true)]
    [InlineData("0", true)]   // no limit
    public void A_snapshot_age_limit_shorter_than_two_refreshes_is_refused_at_startup(string hours, bool starts)
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ebs:ChannelStateMaxAgeHours"] = hours,
            })));

        var exception = Record.Exception(() => factory.CreateClient());

        if (starts) Assert.Null(exception);
        else Assert.Contains("ChannelStateMaxAgeHours", exception?.ToString() ?? "");
    }

    [Fact]
    public async Task A_clear_whose_offline_broadcast_fails_is_a_502_so_the_client_retries()
    {
        var record = _factory.TokenStore.IssueToken("chan-pubsub-down", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", record.Token);
        (await client.PostAsJsonAsync("/api/update-state", new { state = new { headline = "Docked at Home" } })).EnsureSuccessStatusCode();
        _factory.PubSubClient.RejectFor.Add("chan-pubsub-down");

        var response = await client.DeleteAsync("/api/update-state");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        // What tells the app this 502 is the EBS's own, with the card already gone.
        Assert.Equal("true", Assert.Single(response.Headers.GetValues(ChannelStateClearing.SnapshotRemovedHeader)));

        // Removed regardless: new viewers never see it, and retrying the DELETE is harmless.
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync("/api/initial-state/chan-pubsub-down")).StatusCode);
    }

    [Fact]
    public async Task Signing_out_revokes_the_token_even_when_the_offline_broadcast_fails()
    {
        var record = _factory.TokenStore.IssueToken("chan-signout-pubsub-down", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", record.Token);
        (await client.PostAsJsonAsync("/api/update-state", new { state = new { headline = "Docked at Home" } })).EnsureSuccessStatusCode();
        _factory.PubSubClient.RejectFor.Add("chan-signout-pubsub-down");

        (await client.PostAsync("/oauth/revoke", null)).EnsureSuccessStatusCode();

        // A PubSub outage must not keep a signed-out credential alive.
        Assert.False(_factory.TokenStore.TryGetByToken(record.Token, out _));
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync("/api/initial-state/chan-signout-pubsub-down")).StatusCode);
    }

    [Fact]
    public async Task A_sign_out_whose_clear_fails_leaves_the_token_able_to_retry()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChannelStateStore>();
            services.AddSingleton<IChannelStateStore, FailingRemoveChannelStateStore>();
        }));
        var tokens = factory.Services.GetRequiredService<IBroadcasterTokenStore>();
        var record = tokens.IssueToken("chan-failed-clear", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", record.Token);

        // A 500 in production; TestServer rethrows the app's exception instead.
        try { Assert.False((await client.PostAsync("/oauth/revoke", null)).IsSuccessStatusCode); }
        catch (InvalidOperationException) { }

        // The card is still up, so the credential that can take it down must survive.
        Assert.True(tokens.TryGetByToken(record.Token, out _));
    }

    [Fact]
    public async Task A_rate_limited_update_says_when_to_retry()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ebs:UpdateStateRateLimit"] = "1",
                ["Ebs:UpdateStateRateLimitWindowSeconds"] = "30",
            })));
        var record = factory.Services.GetRequiredService<IBroadcasterTokenStore>()
            .IssueToken("chan-retry-after", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", record.Token);

        await client.PostAsJsonAsync("/api/update-state", new { state = new { foo = "bar" } });
        var limited = await client.PostAsJsonAsync("/api/update-state", new { state = new { foo = "bar" } });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var retryAfter = limited.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value.TotalSeconds, 1, 30);
    }

    [Fact]
    public async Task InitialState_allows_the_extension_iframe_origin()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/initial-state/chan-1");
        request.Headers.Add("Origin", "https://abc123.ext-twitch.tv");

        var response = await client.SendAsync(request);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task InitialState_does_not_allow_an_untrusted_origin()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/initial-state/chan-1");
        request.Headers.Add("Origin", "https://evil.example.com");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeTwitchPubSubClient PubSubClient { get; } = new();

        public IBroadcasterTokenStore TokenStore => Services.GetRequiredService<IBroadcasterTokenStore>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Twitch:ExtensionSecret"] = "c3VwZXItc2VjcmV0LWV4dGVuc2lvbi1rZXktMTIzNA==",
                ["Twitch:ClientId"] = "test-client-id",
                ["Twitch:ClientSecret"] = "test-client-secret",
                ["Twitch:ExtensionId"] = "test-extension-id",
                // Keep the shared fixture off disk; the restart-survival tests opt into Sqlite explicitly.
                ["Ebs:StorageProvider"] = "InMemory",
                // Generous by default so unrelated tests sharing this fixture's single server instance
                // don't throttle each other (they'd otherwise all share one "unknown" IP bucket for
                // any request that never reaches a valid channel id). The dedicated rate-limit test
                // above spins up its own factory with a deliberately tight limit.
                ["Ebs:UpdateStateRateLimit"] = "1000",
                ["Ebs:UpdateStateRateLimitWindowSeconds"] = "1",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITwitchPubSubClient>();
                services.AddSingleton<ITwitchPubSubClient>(PubSubClient);
            });
        }
    }
}

/// <summary>A channel-state store whose delete fails, standing in for a storage error mid-clear.</summary>
public sealed class FailingRemoveChannelStateStore : IChannelStateStore
{
    private readonly InMemoryChannelStateStore _inner = new();

    public void Set(string channelId, JsonElement state) => _inner.Set(channelId, state);

    public bool TryGet(string channelId, out JsonElement state) => _inner.TryGet(channelId, out state);

    public void Remove(string channelId) => throw new InvalidOperationException("simulated storage failure");

    public int PruneExpired() => 0;
}

/// <summary>Records every broadcast call instead of making a real Helix API request.</summary>
public sealed class FakeTwitchPubSubClient : ITwitchPubSubClient
{
    public Dictionary<string, int> BroadcastCalls { get; } = new();

    public Dictionary<string, JsonElement> LastMessage { get; } = new();

    /// <summary>Channels whose broadcasts Twitch rejects.</summary>
    public HashSet<string> RejectFor { get; } = new();

    public Task<bool> BroadcastAsync(string broadcasterId, JsonElement state, CancellationToken cancellationToken)
    {
        BroadcastCalls[broadcasterId] = BroadcastCalls.GetValueOrDefault(broadcasterId) + 1;
        LastMessage[broadcasterId] = state.Clone();
        return Task.FromResult(!RejectFor.Contains(broadcasterId));
    }
}
