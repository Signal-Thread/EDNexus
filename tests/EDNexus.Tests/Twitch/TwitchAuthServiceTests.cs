using System.Text.RegularExpressions;
using EDNexus.Core.Settings;
using EDNexus.Core.Twitch;
using Xunit;

namespace EDNexus.Tests.Twitch;

public class TwitchAuthServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ednexus-twitch-settings-").FullName;
    private string SettingsPath => Path.Combine(_root, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly TwitchOAuthOptions Options = new() { EbsBaseUrl = "http://localhost:8787", LoginTimeout = TimeSpan.FromSeconds(5) };

    private static string ExtractState(string url) => Regex.Match(url, "state=([^&]+)").Groups[1].Value;

    private (AppSettings Settings, SettingsStore Store) NewStore()
    {
        var store = new SettingsStore(SettingsPath);
        return (store.Load(), store);
    }

    [Fact]
    public async Task LoginAsync_persists_the_EBS_token_and_broadcaster_identity_on_success()
    {
        var (settings, store) = NewStore();
        var browser = new FakeBrowserLauncher();
        var api = new FakeEbsAuthApiClient
        {
            OnExchange = (code, verifier, redirect) =>
            {
                Assert.Equal("auth-code-123", code);
                Assert.NotEmpty(verifier);
                Assert.Equal("http://localhost:59123/callback", redirect);
                return new EbsTokenResponse { Token = "ebs-token-1", ChannelId = "999", Username = "CMDR_Jameson" };
            },
        };
        var listener = new FakeCallbackListener(browser, () => new Dictionary<string, string>
        {
            ["code"] = "auth-code-123",
            ["state"] = ExtractState(browser.LastUrl ?? ""),
        });

        var service = new TwitchAuthService(settings, store, Options, api, browser, listener);
        var result = await service.LoginAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("CMDR_Jameson", result.Username);
        Assert.Equal("999", result.ChannelId);
        Assert.Equal(1, api.ExchangeCalls);

        Assert.True(service.IsLoggedIn);
        Assert.Equal("ebs-token-1", service.Token);
        Assert.Equal("999", settings.Twitch.ChannelId);
        Assert.Equal("CMDR_Jameson", settings.Twitch.Username);

        // Round-trips through disk, like the rest of AppSettings.
        var reloaded = new SettingsStore(SettingsPath).Load();
        Assert.Equal("ebs-token-1", reloaded.Twitch.Token);
        Assert.Equal("999", reloaded.Twitch.ChannelId);
    }

    [Fact]
    public async Task LoginAsync_opens_the_EBS_authorize_url_with_pkce_params_and_no_direct_Twitch_call()
    {
        var (settings, store) = NewStore();
        var browser = new FakeBrowserLauncher();
        var api = new FakeEbsAuthApiClient
        {
            OnExchange = (_, _, _) => new EbsTokenResponse { Token = "t", ChannelId = "1", Username = "X" },
        };
        var listener = new FakeCallbackListener(browser, () => new Dictionary<string, string>
        {
            ["code"] = "code",
            ["state"] = ExtractState(browser.LastUrl ?? ""),
        });

        var service = new TwitchAuthService(settings, store, Options, api, browser, listener);
        await service.LoginAsync();

        Assert.NotNull(browser.LastUrl);
        Assert.StartsWith("http://localhost:8787/oauth/authorize?", browser.LastUrl);
        Assert.Contains("response_type=code", browser.LastUrl);
        Assert.Contains("code_challenge_method=S256", browser.LastUrl);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A59123%2Fcallback", browser.LastUrl);
        Assert.DoesNotContain("twitch.tv", browser.LastUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginAsync_fails_closed_on_state_mismatch_without_exchanging_the_code()
    {
        var (settings, store) = NewStore();
        var browser = new FakeBrowserLauncher();
        var api = new FakeEbsAuthApiClient();
        var listener = new FakeCallbackListener(new Dictionary<string, string> { ["code"] = "code", ["state"] = "not-the-real-state" });

        var service = new TwitchAuthService(settings, store, Options, api, browser, listener);
        var result = await service.LoginAsync();

        Assert.Equal(TwitchAuthStatus.Error, result.Status);
        Assert.Contains("State mismatch", result.Error);
        Assert.Equal(0, api.ExchangeCalls);
        Assert.False(service.IsLoggedIn);
    }

    [Fact]
    public async Task LoginAsync_reports_denial_when_the_commander_declines_on_the_EBS_hosted_consent_page()
    {
        var (settings, store) = NewStore();
        var browser = new FakeBrowserLauncher();
        var listener = new FakeCallbackListener(new Dictionary<string, string>
        {
            ["error"] = "access_denied",
            ["error_description"] = "The user denied you access",
        });

        var service = new TwitchAuthService(settings, store, Options, new FakeEbsAuthApiClient(), browser, listener);
        var result = await service.LoginAsync();

        Assert.Equal(TwitchAuthStatus.Denied, result.Status);
        Assert.Equal("The user denied you access", result.Error);
        Assert.False(service.IsLoggedIn);
    }

    [Fact]
    public async Task LoginAsync_reports_timeout_when_no_callback_arrives()
    {
        var (settings, store) = NewStore();
        var options = new TwitchOAuthOptions { EbsBaseUrl = "http://localhost:8787", LoginTimeout = TimeSpan.FromMilliseconds(50) };
        var listener = new FakeCallbackListener(new OperationCanceledException());

        var service = new TwitchAuthService(settings, store, options, new FakeEbsAuthApiClient(), new FakeBrowserLauncher(), listener);
        var result = await service.LoginAsync();

        Assert.Equal(TwitchAuthStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task LoginAsync_reports_cancelled_when_the_caller_cancels()
    {
        var (settings, store) = NewStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var listener = new FakeCallbackListener(new OperationCanceledException());

        var service = new TwitchAuthService(settings, store, Options, new FakeEbsAuthApiClient(), new FakeBrowserLauncher(), listener);
        var result = await service.LoginAsync(cts.Token);

        Assert.Equal(TwitchAuthStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task LoginAsync_surfaces_EBS_token_exchange_failures_as_an_error_result()
    {
        var (settings, store) = NewStore();
        var browser = new FakeBrowserLauncher();
        var api = new FakeEbsAuthApiClient
        {
            OnExchange = (_, _, _) => throw new EbsAuthApiException("PKCE verifier did not match", 400),
        };
        var listener = new FakeCallbackListener(browser, () => new Dictionary<string, string>
        {
            ["code"] = "code",
            ["state"] = ExtractState(browser.LastUrl ?? ""),
        });

        var service = new TwitchAuthService(settings, store, Options, api, browser, listener);
        var result = await service.LoginAsync();

        Assert.Equal(TwitchAuthStatus.Error, result.Status);
        Assert.Contains("PKCE verifier did not match", result.Error);
        Assert.False(service.IsLoggedIn);
    }

    [Fact]
    public async Task LoginAsync_is_listening_before_the_browser_is_opened()
    {
        var (settings, store) = NewStore();
        var listener = new FakeCallbackListener(new Dictionary<string, string> { ["error"] = "access_denied" });
        var browser = new ProbingBrowserLauncher(listener);

        var service = new TwitchAuthService(settings, store, Options, new FakeEbsAuthApiClient(), browser, listener);
        await service.LoginAsync();

        Assert.True(browser.ListeningWhenOpened);
        Assert.False(string.IsNullOrEmpty(listener.ExpectedState));
        Assert.Contains($"state={listener.ExpectedState}", browser.LastUrl);
    }

    [Fact]
    public async Task LoginAsync_reports_a_bind_failure_without_ever_opening_the_browser()
    {
        var (settings, store) = NewStore();
        var browser = new FakeBrowserLauncher();
        var listener = new FakeCallbackListener(new Dictionary<string, string>())
        {
            ListenFailure = new InvalidOperationException("port 59123 is taken"),
        };

        var service = new TwitchAuthService(settings, store, Options, new FakeEbsAuthApiClient(), browser, listener);
        var result = await service.LoginAsync();

        Assert.Equal(TwitchAuthStatus.Error, result.Status);
        Assert.Contains("port 59123 is taken", result.Error);
        Assert.Null(browser.LastUrl);
    }

    [Fact]
    public async Task LoginAsync_releases_the_port_when_the_browser_cannot_be_opened()
    {
        var (settings, store) = NewStore();
        var listener = new FakeCallbackListener(new Dictionary<string, string>());
        var browser = new ProbingBrowserLauncher(listener, fail: true);

        var service = new TwitchAuthService(settings, store, Options, new FakeEbsAuthApiClient(), browser, listener);
        var result = await service.LoginAsync();

        Assert.Equal(TwitchAuthStatus.Error, result.Status);
        Assert.Contains("browser", result.Error);
        Assert.True(listener.Released);
    }

    [Fact]
    public async Task LoginAsync_releases_the_port_after_a_completed_login()
    {
        var (settings, store) = NewStore();
        var browser = new FakeBrowserLauncher();
        var listener = new FakeCallbackListener(browser, () => new Dictionary<string, string>
        {
            ["code"] = "code",
            ["state"] = ExtractState(browser.LastUrl ?? ""),
        });
        var api = new FakeEbsAuthApiClient
        {
            OnExchange = (_, _, _) => new EbsTokenResponse { Token = "t", ChannelId = "1", Username = "X" },
        };

        var service = new TwitchAuthService(settings, store, Options, api, browser, listener);
        var result = await service.LoginAsync();

        Assert.True(result.IsSuccess);
        Assert.True(listener.Released);
    }

    [Fact]
    public async Task LoginAsync_uses_the_redirect_that_was_actually_bound_for_the_authorize_url_and_the_exchange()
    {
        var (settings, store) = NewStore();
        var browser = new FakeBrowserLauncher();
        var fallback = new Uri("http://localhost:51234/callback");
        var listener = new FakeCallbackListener(browser, () => new Dictionary<string, string>
        {
            ["code"] = "code",
            ["state"] = ExtractState(browser.LastUrl ?? ""),
        })
        {
            BoundRedirect = fallback,
        };
        string? exchangedRedirect = null;
        var api = new FakeEbsAuthApiClient
        {
            OnExchange = (_, _, redirect) =>
            {
                exchangedRedirect = redirect;
                return new EbsTokenResponse { Token = "t", ChannelId = "1", Username = "X" };
            },
        };

        var service = new TwitchAuthService(settings, store, Options, api, browser, listener);
        await service.LoginAsync();

        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A51234%2Fcallback", browser.LastUrl);
        Assert.Equal("http://localhost:51234/callback", exchangedRedirect);
    }

    [Theory]
    [InlineData("http://ebs.example.com")]
    [InlineData("ebs.example.com")]
    public async Task LoginAsync_refuses_an_insecure_EBS_without_opening_the_browser(string baseUrl)
    {
        var (settings, store) = NewStore();
        var browser = new FakeBrowserLauncher();
        var api = new FakeEbsAuthApiClient();
        var options = new TwitchOAuthOptions { EbsBaseUrl = baseUrl, LoginTimeout = TimeSpan.FromSeconds(5) };

        var service = new TwitchAuthService(settings, store, options, api, browser, new FakeCallbackListener(new Dictionary<string, string>()));
        var result = await service.LoginAsync();

        Assert.Equal(TwitchAuthStatus.Error, result.Status);
        Assert.Contains("https", result.Error);
        Assert.Null(browser.LastUrl);
        Assert.Equal(0, api.ExchangeCalls);
    }

    [Fact]
    public async Task LogoutAsync_does_not_send_the_token_to_an_insecure_EBS_or_queue_it()
    {
        var (settings, store) = NewStore();
        settings.Twitch.Token = "ebs-token-1";
        settings.Twitch.ChannelId = "1";
        var api = new FakeEbsAuthApiClient();
        using var cleanup = new EbsCleanupQueue(settings, store, new FakeStreamStateApiClient(), api);
        var options = new TwitchOAuthOptions { EbsBaseUrl = "http://ebs.example.com" };

        var service = new TwitchAuthService(settings, store, options, api, new FakeBrowserLauncher(),
            new FakeCallbackListener(new Dictionary<string, string>()), cleanup);
        await service.LogoutAsync();

        Assert.Equal(0, api.RevokeCalls);
        Assert.Empty(cleanup.Pending);
        Assert.False(service.IsLoggedIn);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("https://ebs.example.com", true)]
    [InlineData("  https://ebs.example.com/  ", true)]
    [InlineData("http://localhost:8787", true)]
    [InlineData("http://127.0.0.1:8787", true)]
    [InlineData("http://[::1]:8787", true)]
    [InlineData("http://ebs.example.com", false)]
    [InlineData("ebs.example.com", false)]
    [InlineData("ftp://ebs.example.com", false)]
    public void ValidateEbsBaseUrl_allows_https_and_loopback_only(string? text, bool valid)
    {
        Assert.Equal(valid, TwitchOAuthOptions.ValidateEbsBaseUrl(text) is null);
    }

    [Fact]
    public async Task LogoutAsync_revokes_the_token_with_the_EBS_and_clears_the_session()
    {
        var (settings, store) = NewStore();
        settings.Twitch.Token = "ebs-token-1";
        settings.Twitch.ChannelId = "1";
        settings.Twitch.Username = "CMDR";

        var api = new FakeEbsAuthApiClient();
        var service = new TwitchAuthService(settings, store, Options, api, new FakeBrowserLauncher(), new FakeCallbackListener(new Dictionary<string, string>()));

        await service.LogoutAsync();

        Assert.Equal(1, api.RevokeCalls);
        Assert.Equal("ebs-token-1", api.LastRevokedToken);
        Assert.False(service.IsLoggedIn);
        Assert.Null(settings.Twitch.Token);
        Assert.Null(settings.Twitch.Username);
    }

    [Fact]
    public async Task LogoutAsync_clears_local_state_even_if_the_EBS_revoke_call_throws()
    {
        var (settings, store) = NewStore();
        settings.Twitch.Token = "ebs-token-1";
        settings.Twitch.ChannelId = "1";

        var service = new TwitchAuthService(settings, store, Options, new ThrowingRevokeClient(), new FakeBrowserLauncher(), new FakeCallbackListener(new Dictionary<string, string>()));

        await service.LogoutAsync();

        Assert.False(service.IsLoggedIn);
        Assert.Null(settings.Twitch.Token);
    }

    [Fact]
    public async Task LogoutAsync_queues_a_revoke_the_EBS_did_not_acknowledge()
    {
        var (settings, store) = NewStore();
        settings.Twitch.Token = "ebs-token-1";
        settings.Twitch.ChannelId = "1";
        using var cleanup = new EbsCleanupQueue(settings, store, new FakeStreamStateApiClient(), new ThrowingRevokeClient());

        var service = new TwitchAuthService(settings, store, Options, new ThrowingRevokeClient(), new FakeBrowserLauncher(),
            new FakeCallbackListener(new Dictionary<string, string>()), cleanup);

        await service.LogoutAsync();

        // Signed out locally, but the token still works on the EBS and the card is still up there:
        // the revoke has to be retried, with the token that is no longer anywhere else.
        Assert.False(service.IsLoggedIn);
        var pending = Assert.Single(new SettingsStore(SettingsPath).Load().Twitch.PendingCleanups);
        Assert.Equal(EbsCleanupKind.Revoke, pending.Kind);
        Assert.Equal(Options.RevokeEndpoint, pending.Endpoint);
        Assert.Equal("ebs-token-1", pending.Token);
    }

    private sealed class ThrowingRevokeClient : IEbsAuthApiClient
    {
        public Task<EbsTokenResponse> ExchangeCodeAsync(string tokenEndpoint, string code, string codeVerifier, string redirectUri, CancellationToken ct = default) =>
            throw new InvalidOperationException("not used");

        public Task RevokeAsync(string revokeEndpoint, string token, CancellationToken ct = default) =>
            throw new HttpRequestException("EBS unreachable");
    }
}
