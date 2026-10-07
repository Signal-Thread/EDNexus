using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EDNexus.Ebs.Options;
using EDNexus.Ebs.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EDNexus.Ebs.Tests;

/// <summary>
/// Endpoint-level coverage for the hardening review findings: the real client address behind a proxy,
/// initial-state caching/CORS, request-body caps, state validation, the OAuth rate limit and input
/// checks, sign-out vs in-flight publish ordering, and startup validation.
/// </summary>
public class EbsHardeningTests
{
    private const string ValidChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private static readonly IPAddress ProxyAddress = IPAddress.Parse("172.18.0.2");
    private static readonly IPAddress PublicAddress = IPAddress.Parse("203.0.113.50");

    private static Dictionary<string, string?> TightInitialStateLimit() => new()
    {
        ["Ebs:InitialStateRateLimit"] = "1",
        ["Ebs:InitialStateRateLimitWindowSeconds"] = "60",
    };

    private static HttpRequestMessage InitialState(string channel, string? forwardedFor = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/initial-state/" + channel);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    // ───── EBS-2: client address behind a proxy ─────

    [Fact]
    public async Task Behind_a_trusted_proxy_each_viewer_gets_their_own_initial_state_budget()
    {
        using var factory = new EbsHostFactory(TightInitialStateLimit(), ProxyAddress);
        var client = factory.CreateClient();

        var viewerOne = await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.1"));
        var viewerTwo = await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.2"));
        var viewerOneAgain = await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.1"));

        Assert.Equal(HttpStatusCode.NotFound, viewerOne.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viewerTwo.StatusCode); // would be 429 if all viewers shared the proxy's bucket
        Assert.Equal(HttpStatusCode.TooManyRequests, viewerOneAgain.StatusCode);
    }

    [Fact]
    public async Task A_peer_that_is_not_a_trusted_proxy_cannot_choose_its_own_rate_limit_bucket()
    {
        using var factory = new EbsHostFactory(TightInitialStateLimit(), PublicAddress);
        var client = factory.CreateClient();

        await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.1"));
        var spoofed = await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.99"));

        Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.StatusCode);
    }

    [Fact]
    public async Task Configured_proxy_networks_replace_the_defaults()
    {
        var config = TightInitialStateLimit();
        config["Ebs:TrustedProxyNetworks:0"] = "10.99.0.0/16"; // the default private ranges no longer apply
        using var factory = new EbsHostFactory(config, ProxyAddress); // 172.18.0.2 is now untrusted
        var client = factory.CreateClient();

        await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.1"));
        var second = await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.2"));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Only_one_forwarded_hop_is_trusted()
    {
        using var factory = new EbsHostFactory(TightInitialStateLimit(), ProxyAddress);
        var client = factory.CreateClient();

        // The client prepended a made-up address; the proxy appended the real one. Only the last hop counts.
        await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.77, 198.51.100.1"));
        var sameRealClient = await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.88, 198.51.100.1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, sameRealClient.StatusCode);
    }

    // ───── EBS-2 / coordinator: initial-state caching, CORS and Retry-After ─────

    [Fact]
    public async Task A_served_snapshot_is_publicly_cacheable_for_a_few_seconds_and_varies_by_origin()
    {
        using var factory = new EbsHostFactory();
        var (broadcaster, _) = factory.CreateBroadcasterClient("chan-cache");
        (await broadcaster.PostAsJsonAsync("/api/update-state", new { state = new { v = 1 } })).EnsureSuccessStatusCode();

        var response = await factory.CreateClient().SendAsync(InitialState("chan-cache", origin: "https://abc.ext-twitch.tv"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=5", response.Headers.CacheControl?.ToString());
        Assert.Contains("Origin", response.Headers.Vary);
        Assert.Equal("https://abc.ext-twitch.tv", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task The_cache_lifetime_is_configurable_and_zero_turns_it_off()
    {
        using var factory = new EbsHostFactory(new() { ["Ebs:InitialStateCacheSeconds"] = "0" });
        var (broadcaster, _) = factory.CreateBroadcasterClient("chan-nocache");
        (await broadcaster.PostAsJsonAsync("/api/update-state", new { state = new { v = 1 } })).EnsureSuccessStatusCode();

        var response = await factory.CreateClient().SendAsync(InitialState("chan-nocache"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.CacheControl);
    }

    [Fact]
    public async Task A_missing_snapshot_is_not_publicly_cacheable()
    {
        using var factory = new EbsHostFactory();

        var response = await factory.CreateClient().SendAsync(InitialState("never-published"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(true, response.Headers.CacheControl?.Public);
    }

    [Fact]
    public async Task A_rate_limited_initial_state_tells_the_browser_when_to_retry_and_is_not_cacheable()
    {
        using var factory = new EbsHostFactory(TightInitialStateLimit(), ProxyAddress);
        var client = factory.CreateClient();
        const string origin = "https://abc.ext-twitch.tv";

        await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.1", origin: origin));
        var limited = await client.SendAsync(InitialState("chan", forwardedFor: "198.51.100.1", origin: origin));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        // Without Access-Control-Expose-Headers the extension's script cannot read Retry-After at all.
        Assert.Equal(origin, Assert.Single(limited.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("Retry-After", Assert.Single(limited.Headers.GetValues("Access-Control-Expose-Headers")).Split(',').Select(h => h.Trim()));
        Assert.InRange(limited.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0, 1, 60);
        Assert.True(limited.Headers.CacheControl?.NoStore);
        Assert.NotEqual(true, limited.Headers.CacheControl?.Public);
    }

    // ───── EBS-3 / EBS-4: request body cap and state validation ─────

    [Fact]
    public async Task A_request_body_over_the_cap_is_refused_before_it_is_read()
    {
        using var factory = new EbsHostFactory();
        var (client, _) = factory.CreateBroadcasterClient("chan-big");
        var oversized = new StringContent("{\"state\":{\"blob\":\"" + new string('x', 20_000) + "\"}}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/update-state", oversized);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(factory.PubSub.BroadcastCalls.ContainsKey("chan-big"));
    }

    [Fact]
    public async Task The_oauth_endpoints_have_the_same_body_cap()
    {
        using var factory = new EbsHostFactory();
        var oversized = new StringContent("{\"code\":\"" + new string('x', 20_000) + "\"}", Encoding.UTF8, "application/json");

        var response = await factory.CreateClient().PostAsync("/oauth/token", oversized);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("{}")]                      // no "state" at all: an undefined JsonElement used to be a 500
    [InlineData("{\"state\":null}")]
    [InlineData("{\"state\":[1,2]}")]
    [InlineData("{\"state\":\"text\"}")]
    [InlineData("{\"state\":12}")]
    public async Task A_missing_null_or_non_object_state_is_a_400_not_a_500(string body)
    {
        using var factory = new EbsHostFactory();
        var (client, _) = factory.CreateBroadcasterClient("chan-bad-state");

        var response = await client.PostAsync("/api/update-state", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(factory.PubSub.BroadcastCalls.ContainsKey("chan-bad-state"));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().SendAsync(InitialState("chan-bad-state"))).StatusCode);
    }

    [Fact]
    public async Task The_stored_and_broadcast_state_is_the_compact_form_that_was_size_checked()
    {
        using var factory = new EbsHostFactory(new() { ["Ebs:MaxStatePayloadBytes"] = "200" });
        var (client, _) = factory.CreateBroadcasterClient("chan-compact");
        // 8 bytes compact, hundreds as sent: the limit only ever saw the compact form.
        var padded = "{\n  \"state\": {\n    \"a\"   :   1,\n    \"b\"   :   [ 1 ,  2 ]\n  }\n}";

        var response = await client.PostAsync("/api/update-state", new StringContent(padded, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = factory.Services.GetRequiredService<IChannelStateStore>();
        Assert.True(stored.TryGet("chan-compact", out var state));
        Assert.Equal("{\"a\":1,\"b\":[1,2]}", state.GetRawText());
        Assert.Equal("{\"a\":1,\"b\":[1,2]}", factory.PubSub.LastMessage["chan-compact"].GetRawText());
    }

    // ───── EBS-5: OAuth rate limit and input checks ─────

    [Fact]
    public async Task The_oauth_endpoints_are_rate_limited_per_client_address()
    {
        using var factory = new EbsHostFactory(new() { ["Ebs:OAuthRateLimit"] = "2", ["Ebs:OAuthRateLimitWindowSeconds"] = "60" }, ProxyAddress);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpRequestMessage Callback(string client) => new(HttpMethod.Get, "/oauth/callback?state=nope") { Headers = { { "X-Forwarded-For", client } } };

        var first = await client.SendAsync(Callback("198.51.100.1"));
        var second = await client.SendAsync(Callback("198.51.100.1"));
        var third = await client.SendAsync(Callback("198.51.100.1"));
        var someoneElse = await client.SendAsync(Callback("198.51.100.2"));

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.BadRequest, someoneElse.StatusCode);
    }

    [Fact]
    public async Task Every_oauth_route_shares_the_one_budget()
    {
        using var factory = new EbsHostFactory(new() { ["Ebs:OAuthRateLimit"] = "2", ["Ebs:OAuthRateLimitWindowSeconds"] = "60" });
        var client = factory.CreateClient();

        await client.PostAsync("/oauth/revoke", null);
        await client.PostAsJsonAsync("/oauth/token", new { code = "a", code_verifier = "b", redirect_uri = "c" });
        var limited = await client.PostAsync("/oauth/revoke", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private static string AuthorizeUrl(string? redirect = "http://localhost:59123/callback", string? state = "s", string? challenge = ValidChallenge) =>
        "/oauth/authorize?redirect_uri=" + Uri.EscapeDataString(redirect ?? "") + "&state=" + Uri.EscapeDataString(state ?? "")
        + "&code_challenge=" + Uri.EscapeDataString(challenge ?? "") + "&code_challenge_method=S256";

    [Theory]
    [InlineData("short")]                                          // not 43 characters
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cMx")]   // 44 characters
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM")]    // '+' is base64, not base64url
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw=cM")]    // padding
    public async Task Authorize_rejects_a_code_challenge_that_is_not_a_43_character_base64url_string(string challenge)
    {
        using var factory = new EbsHostFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(AuthorizeUrl(challenge: challenge));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_rejects_oversized_state_and_redirect_uri()
    {
        using var factory = new EbsHostFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var longState = await client.GetAsync(AuthorizeUrl(state: new string('s', 600)));
        var longRedirect = await client.GetAsync(AuthorizeUrl(redirect: "http://localhost:59123/" + new string('p', 600)));
        var fine = await client.GetAsync(AuthorizeUrl(state: new string('s', 43)));

        Assert.Equal(HttpStatusCode.BadRequest, longState.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longRedirect.StatusCode);
        Assert.Equal(HttpStatusCode.Found, fine.StatusCode);
    }

    // ───── EBS-8: sign-out vs an in-flight publish ─────

    [Fact]
    public async Task A_publish_in_flight_when_the_broadcaster_signs_out_cannot_leave_a_snapshot_behind()
    {
        var store = new GatedSetChannelStateStore("chan-race");
        using var factory = new EbsHostFactory(services: services =>
        {
            services.RemoveAll<IChannelStateStore>();
            services.AddSingleton<IChannelStateStore>(store);
        });
        var (publisher, token) = factory.CreateBroadcasterClient("chan-race");
        var signOut = factory.CreateClient();
        signOut.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var update = Task.Run(() => publisher.PostAsJsonAsync("/api/update-state", new { state = new { headline = "Docked" } }));
        Assert.True(store.SetEntered.Wait(TimeSpan.FromSeconds(10)), "the publish never reached the store");
        var revoke = Task.Run(() => signOut.PostAsync("/oauth/revoke", null));
        await Task.WhenAny(revoke, Task.Delay(500)); // the sign-out must now be queued behind the publish
        store.ReleaseSet.Set();
        await Task.WhenAll(update, revoke);

        Assert.False(factory.Tokens.TryGetByToken(token, out _));
        // Whichever finished last, no snapshot may outlive the sign-out.
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().SendAsync(InitialState("chan-race"))).StatusCode);
    }

    [Fact]
    public async Task A_publish_that_waited_behind_a_sign_out_is_refused_and_stores_nothing()
    {
        var pubSub = new GatedOfflinePubSubClient("chan-queued");
        using var factory = new EbsHostFactory(services: services =>
        {
            services.RemoveAll<ITwitchPubSubClient>();
            services.AddSingleton<ITwitchPubSubClient>(pubSub);
        });
        var (publisher, token) = factory.CreateBroadcasterClient("chan-queued");
        var signOut = factory.CreateClient();
        signOut.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var revoke = Task.Run(() => signOut.PostAsync("/oauth/revoke", null));
        Assert.True(pubSub.OfflineEntered.Wait(TimeSpan.FromSeconds(10)), "the sign-out never reached the offline broadcast");
        // The token still exists, so this passes authentication and then queues for the channel.
        var update = Task.Run(() => publisher.PostAsJsonAsync("/api/update-state", new { state = new { headline = "Too late" } }));
        await Task.Delay(300);
        pubSub.ReleaseOffline.SetResult();
        var updateResponse = await update;
        (await revoke).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, updateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().SendAsync(InitialState("chan-queued"))).StatusCode);
    }

    // ───── clears are idempotent (the desktop sends one on shutdown, on exit and at launch) ─────

    [Fact]
    public async Task Repeated_clears_of_an_absent_snapshot_are_quiet_204s()
    {
        using var factory = new EbsHostFactory();
        var (client, _) = factory.CreateBroadcasterClient("chan-repeat-clear");
        (await client.PostAsJsonAsync("/api/update-state", new { state = new { headline = "Docked" } })).EnsureSuccessStatusCode();

        var first = await client.DeleteAsync("/api/update-state");
        var second = await client.DeleteAsync("/api/update-state");
        var third = await client.DeleteAsync("/api/update-state");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, third.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().SendAsync(InitialState("chan-repeat-clear"))).StatusCode);
    }

    [Fact]
    public async Task A_clear_for_a_channel_that_never_published_is_a_204()
    {
        using var factory = new EbsHostFactory();
        var (client, _) = factory.CreateBroadcasterClient("chan-never-published");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/update-state")).StatusCode);
    }

    // ───── EBS-9: startup validation ─────

    public static TheoryData<string, string?, string> InvalidConfiguration => new()
    {
        { "Twitch:ClientId", "", "Twitch:ClientId" },
        { "Twitch:ClientSecret", "", "Twitch:ClientSecret" },
        { "Twitch:ExtensionId", "", "Twitch:ExtensionId" },
        { "Twitch:ExtensionSecret", "", "Twitch:ExtensionSecret" },
        { "Twitch:ExtensionSecret", "not base64!", "Twitch:ExtensionSecret" },
        { "Twitch:ExtensionSecret", "YWJj", "Twitch:ExtensionSecret" }, // "abc": decodes to 3 bytes
        { "Twitch:OAuthRedirectUri", "http://ebs.example.com/oauth/callback", "Twitch:OAuthRedirectUri" },
        { "Twitch:OAuthRedirectUri", "/oauth/callback", "Twitch:OAuthRedirectUri" },
        { "Twitch:OwnerUserId", "not-a-number", "Twitch:OwnerUserId" },
        { "Ebs:MaxStatePayloadBytes", "6000", "Ebs:MaxStatePayloadBytes" },
        { "Ebs:MaxStatePayloadBytes", "0", "Ebs:MaxStatePayloadBytes" },
        { "Ebs:TrustedProxyNetworks:0", "not-a-cidr", "Ebs:TrustedProxyNetworks" },
        { "Ebs:TrustedProxies:0", "not-an-ip", "Ebs:TrustedProxies" },
    };

    [Theory]
    [MemberData(nameof(InvalidConfiguration))]
    public void Invalid_configuration_is_refused_at_startup(string key, string? value, string expectedInMessage)
    {
        using var factory = new EbsHostFactory(new() { [key] = value });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains(expectedInMessage, exception.ToString());
    }

    [Theory]
    [InlineData("https://ebs.example.com/oauth/callback")]
    [InlineData("http://localhost:8787/oauth/callback")] // Twitch allows http for localhost
    public void A_valid_redirect_uri_starts(string redirectUri)
    {
        using var factory = new EbsHostFactory(new() { ["Twitch:OAuthRedirectUri"] = redirectUri, ["Twitch:OwnerUserId"] = "1234567" });

        Assert.Null(Record.Exception(() => factory.CreateClient()));
    }

    [Fact]
    public void The_validators_report_every_problem_at_once()
    {
        var result = new TwitchEbsOptionsValidator().Validate(null, new TwitchEbsOptions());

        Assert.True(result.Failed);
        Assert.Contains("Twitch:ClientId", result.FailureMessage);
        Assert.Contains("Twitch:ClientSecret", result.FailureMessage);
        Assert.Contains("Twitch:ExtensionId", result.FailureMessage);
        Assert.Contains("Twitch:ExtensionSecret", result.FailureMessage);
    }

    // ───── test doubles ─────

    /// <summary>Wraps the in-memory store but parks <see cref="Set"/> for one channel until released.</summary>
    private sealed class GatedSetChannelStateStore(string gatedChannel) : IChannelStateStore
    {
        private readonly InMemoryChannelStateStore _inner = new();

        public ManualResetEventSlim SetEntered { get; } = new();
        public ManualResetEventSlim ReleaseSet { get; } = new();

        public void Set(string channelId, JsonElement state)
        {
            if (channelId == gatedChannel)
            {
                SetEntered.Set();
                ReleaseSet.Wait(TimeSpan.FromSeconds(30));
            }

            _inner.Set(channelId, state);
        }

        public bool TryGet(string channelId, out JsonElement state) => _inner.TryGet(channelId, out state);
        public void Remove(string channelId) => _inner.Remove(channelId);
        public int PruneExpired() => 0;
    }

    /// <summary>Parks the "offline" broadcast of a clear for one channel, and lets everything else through.</summary>
    private sealed class GatedOfflinePubSubClient(string gatedChannel) : ITwitchPubSubClient
    {
        public ManualResetEventSlim OfflineEntered { get; } = new();
        public TaskCompletionSource ReleaseOffline { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> BroadcastAsync(string broadcasterId, JsonElement state, CancellationToken cancellationToken)
        {
            if (broadcasterId == gatedChannel && state.TryGetProperty("offline", out _))
            {
                OfflineEntered.Set();
                await ReleaseOffline.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return true;
        }
    }
}
