using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EDNexus.Ebs.Endpoints;
using EDNexus.Ebs.Security;
using EDNexus.Ebs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EDNexus.Ebs.Tests;

/// <summary>
/// End-to-end coverage of the desktop↔EBS OAuth flow described in the README: <c>/oauth/authorize</c>
/// redirects to Twitch, <c>/oauth/callback</c> exchanges the code and identifies the broadcaster
/// (mocking Twitch's token/user endpoints so no real network call is made), and <c>/oauth/token</c>
/// hands back the long-lived EBS token once the desktop proves possession of the PKCE verifier.
/// </summary>
public class OAuthEndpointsTests : IClassFixture<OAuthEndpointsTests.Factory>
{
    private readonly Factory _factory;

    public OAuthEndpointsTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeTwitchOAuthClient TwitchClient { get; } = new();
        public FakePubSubClient PubSubClient { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                // The default per-channel rate limit (1 request / 2 seconds) is far too tight for a
                // test class that fires several requests back-to-back against a shared test host.
                // Twitch:ExtensionSecret must be a valid base64 string — Program.cs validates it at
                // startup (fail-fast) and the default appsettings.json value is an empty string.
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Twitch:ExtensionSecret"] = "c3VwZXItc2VjcmV0LWV4dGVuc2lvbi1rZXktMTIzNA==",
                    ["Twitch:ClientId"] = "test-client-id",
                    ["Twitch:ClientSecret"] = "test-client-secret",
                    ["Twitch:ExtensionId"] = "test-extension-id",
                    // Keep the shared fixture off disk; the restart-survival tests opt into Sqlite explicitly.
                    ["Ebs:StorageProvider"] = "InMemory",
                    ["Ebs:UpdateStateRateLimit"] = "1000",
                    ["Ebs:UpdateStateRateLimitWindowSeconds"] = "1",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITwitchOAuthClient>();
                services.AddSingleton<ITwitchOAuthClient>(TwitchClient);
                services.RemoveAll<ITwitchPubSubClient>();
                services.AddSingleton<ITwitchPubSubClient>(PubSubClient);
            });
        }
    }

    /// <summary>Fake PubSub relay — the OAuth flow tests only care that authentication succeeded, not that Twitch PubSub itself is reachable.</summary>
    public sealed class FakePubSubClient : ITwitchPubSubClient
    {
        public Task<bool> BroadcastAsync(string broadcasterId, JsonElement state, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    public sealed class FakeTwitchOAuthClient : ITwitchOAuthClient
    {
        public Func<string, TwitchTokenResponse>? OnExchange { get; set; }
        public Func<string, TwitchUser?>? OnGetUser { get; set; }

        public Task<TwitchTokenResponse> ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken ct = default)
        {
            var result = OnExchange?.Invoke(code) ?? new TwitchTokenResponse { AccessToken = "twitch-access", RefreshToken = "twitch-refresh", ExpiresIn = 14400 };
            return Task.FromResult(result);
        }

        public Task<TwitchTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TwitchUser?> GetUserAsync(string accessToken, CancellationToken ct = default)
        {
            TwitchUser? result = OnGetUser is not null ? OnGetUser(accessToken) : new TwitchUser { Id = "999", Login = "cmdr_jameson", DisplayName = "CMDR_Jameson" };
            return Task.FromResult(result);
        }

        public Task RevokeTokenAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string ExtractQueryParam(Uri uri, string name)
    {
        var parsed = QueryHelpers.ParseQuery(uri.Query);
        return parsed.TryGetValue(name, out var values) ? values.ToString() : "";
    }

    [Fact]
    public async Task Authorize_redirects_to_Twitch_with_its_own_redirect_uri_and_a_session_state()
    {
        using var client = NoRedirectClient(_factory);

        var response = await client.GetAsync(
            "/oauth/authorize?redirect_uri=" + Uri.EscapeDataString("http://localhost:59123/callback") +
            "&state=desktop-state-123&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith("https://id.twitch.tv/oauth2/authorize", location.ToString());
        Assert.NotEmpty(ExtractQueryParam(location, "state"));
        // Twitch's state must NOT be the desktop's own state — it's the EBS's session id.
        Assert.NotEqual("desktop-state-123", ExtractQueryParam(location, "state"));
    }

    [Fact]
    public async Task Authorize_asks_Twitch_for_each_scope_only_once()
    {
        using var client = NoRedirectClient(_factory);

        var response = await client.GetAsync(
            "/oauth/authorize?redirect_uri=" + Uri.EscapeDataString("http://localhost:59123/callback") +
            "&state=desktop-state-123&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256");

        var scope = ExtractQueryParam(response.Headers.Location!, "scope");
        var requested = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // The configuration binder appends a bound array onto the options default rather than
        // replacing it, so a scope named in both arrives twice and Twitch is sent
        // "user:read:email user:read:email".
        Assert.Equal(requested.Length, requested.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Authorize_rejects_a_non_loopback_redirect_uri()
    {
        using var client = NoRedirectClient(_factory);

        var response = await client.GetAsync(
            "/oauth/authorize?redirect_uri=" + Uri.EscapeDataString("http://evil.example.com/callback") +
            "&state=s&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_rejects_missing_required_params()
    {
        using var client = NoRedirectClient(_factory);

        var response = await client.GetAsync("/oauth/authorize?state=s");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Full_flow_authorize_then_callback_then_token_issues_the_long_lived_EBS_token()
    {
        using var client = NoRedirectClient(_factory);
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var challenge = EDNexus.Ebs.Security.Pkce.ComputeCodeChallenge(verifier);

        // Step 1: desktop hits /oauth/authorize.
        var authorizeResponse = await client.GetAsync(
            "/oauth/authorize?redirect_uri=" + Uri.EscapeDataString("http://localhost:59123/callback") +
            "&state=desktop-state-123&code_challenge=" + challenge + "&code_challenge_method=S256");
        var twitchAuthorizeUrl = authorizeResponse.Headers.Location!;
        var sessionId = ExtractQueryParam(twitchAuthorizeUrl, "state");

        _factory.TwitchClient.OnExchange = code =>
        {
            Assert.Equal("twitch-auth-code", code);
            return new TwitchTokenResponse { AccessToken = "twitch-access", RefreshToken = "twitch-refresh", ExpiresIn = 14400 };
        };

        // Step 2: Twitch "redirects" back to /oauth/callback with the session id as state.
        var callbackResponse = await client.GetAsync($"/oauth/callback?code=twitch-auth-code&state={sessionId}");
        Assert.Equal(HttpStatusCode.Found, callbackResponse.StatusCode);
        var desktopRedirect = callbackResponse.Headers.Location!;
        Assert.StartsWith("http://localhost:59123/callback", desktopRedirect.ToString());
        Assert.Equal("desktop-state-123", ExtractQueryParam(desktopRedirect, "state"));
        var authCode = ExtractQueryParam(desktopRedirect, "code");
        Assert.NotEmpty(authCode);

        // Step 3: desktop's loopback listener has the code; exchange it (with the verifier) for the EBS token.
        var tokenResponse = await client.PostAsJsonAsync("/oauth/token", new
        {
            code = authCode,
            code_verifier = verifier,
            redirect_uri = "http://localhost:59123/callback",
        });

        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var issued = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("999", issued.GetProperty("channelId").GetString());
        Assert.Equal("CMDR_Jameson", issued.GetProperty("username").GetString());
        var ebsToken = issued.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(ebsToken));

        // The minted token authenticates POST /api/update-state.
        using var authedClient = _factory.CreateClient();
        authedClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ebsToken);
        var updateResponse = await authedClient.PostAsJsonAsync("/api/update-state", new { state = new { system = "Sol" } });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
    }

    [Fact]
    public async Task Token_exchange_fails_when_the_code_verifier_does_not_match_the_original_challenge()
    {
        using var client = NoRedirectClient(_factory);
        var challenge = EDNexus.Ebs.Security.Pkce.ComputeCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        var authorizeResponse = await client.GetAsync(
            "/oauth/authorize?redirect_uri=" + Uri.EscapeDataString("http://localhost:59123/callback") +
            "&state=s&code_challenge=" + challenge + "&code_challenge_method=S256");
        var sessionId = ExtractQueryParam(authorizeResponse.Headers.Location!, "state");

        var callbackResponse = await client.GetAsync($"/oauth/callback?code=twitch-code&state={sessionId}");
        var authCode = ExtractQueryParam(callbackResponse.Headers.Location!, "code");

        var tokenResponse = await client.PostAsJsonAsync("/oauth/token", new
        {
            code = authCode,
            code_verifier = "totally-the-wrong-verifier",
            redirect_uri = "http://localhost:59123/callback",
        });

        Assert.Equal(HttpStatusCode.BadRequest, tokenResponse.StatusCode);
    }

    [Fact]
    public async Task Token_exchange_fails_when_redirect_uri_does_not_match_the_one_used_to_authorize()
    {
        using var client = NoRedirectClient(_factory);
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var challenge = EDNexus.Ebs.Security.Pkce.ComputeCodeChallenge(verifier);

        var authorizeResponse = await client.GetAsync(
            "/oauth/authorize?redirect_uri=" + Uri.EscapeDataString("http://localhost:59123/callback") +
            "&state=s&code_challenge=" + challenge + "&code_challenge_method=S256");
        var sessionId = ExtractQueryParam(authorizeResponse.Headers.Location!, "state");

        _factory.TwitchClient.OnExchange = _ => new TwitchTokenResponse { AccessToken = "a", RefreshToken = "r", ExpiresIn = 14400 };
        var callbackResponse = await client.GetAsync($"/oauth/callback?code=twitch-code&state={sessionId}");
        var authCode = ExtractQueryParam(callbackResponse.Headers.Location!, "code");

        // A different redirect_uri than the one bound at /oauth/authorize — must be rejected even
        // though the code and verifier are both otherwise correct.
        var tokenResponse = await client.PostAsJsonAsync("/oauth/token", new
        {
            code = authCode,
            code_verifier = verifier,
            redirect_uri = "http://localhost:59999/callback",
        });

        Assert.Equal(HttpStatusCode.BadRequest, tokenResponse.StatusCode);
    }

    [Fact]
    public async Task Token_exchange_fails_for_an_unknown_or_already_used_code()
    {
        using var client = NoRedirectClient(_factory);

        var tokenResponse = await client.PostAsJsonAsync("/oauth/token", new
        {
            code = "never-issued",
            code_verifier = "whatever",
            redirect_uri = "http://localhost:59123/callback",
        });

        Assert.Equal(HttpStatusCode.BadRequest, tokenResponse.StatusCode);
    }

    [Fact]
    public async Task Callback_with_an_unknown_state_shows_an_error_instead_of_redirecting_anywhere()
    {
        using var client = NoRedirectClient(_factory);

        var response = await client.GetAsync("/oauth/callback?code=abc&state=not-a-real-session");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Callback_forwards_a_Twitch_denial_to_the_desktop_redirect_with_the_original_state()
    {
        using var client = NoRedirectClient(_factory);
        var authorizeResponse = await client.GetAsync(
            "/oauth/authorize?redirect_uri=" + Uri.EscapeDataString("http://localhost:59123/callback") +
            "&state=desktop-state-xyz&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
        var sessionId = ExtractQueryParam(authorizeResponse.Headers.Location!, "state");

        var callbackResponse = await client.GetAsync($"/oauth/callback?state={sessionId}&error=access_denied&error_description=nope");

        Assert.Equal(HttpStatusCode.Found, callbackResponse.StatusCode);
        var location = callbackResponse.Headers.Location!;
        Assert.StartsWith("http://localhost:59123/callback", location.ToString());
        Assert.Equal("access_denied", ExtractQueryParam(location, "error"));
        Assert.Equal("desktop-state-xyz", ExtractQueryParam(location, "state"));
    }

    [Fact]
    public async Task Update_state_rejects_a_missing_or_unknown_token()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/update-state", new { state = new { system = "Sol" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
