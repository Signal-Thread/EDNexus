using EDNexus.Ebs.Options;
using EDNexus.Ebs.Security;
using EDNexus.Ebs.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EDNexus.Ebs.Tests;

public class TwitchTokenRefreshBackgroundServiceTests
{
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeTwitchOAuthClient : ITwitchOAuthClient
    {
        public Func<string, TwitchTokenResponse>? OnRefresh { get; set; }
        public int RefreshCalls { get; private set; }

        public Task<TwitchTokenResponse> ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TwitchTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
        {
            RefreshCalls++;
            var result = OnRefresh?.Invoke(refreshToken) ?? throw new InvalidOperationException("OnRefresh not configured");
            return Task.FromResult(result);
        }

        public Task<TwitchUser?> GetUserAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeTokenAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static TwitchTokenRefreshBackgroundService CreateService(
        IBroadcasterTokenStore store,
        ITwitchOAuthClient twitch,
        FakeTimeProvider time,
        EbsOptions? options = null) =>
        new(store, twitch, Microsoft.Extensions.Options.Options.Create(options ?? new EbsOptions()), time, NullLogger<TwitchTokenRefreshBackgroundService>.Instance);

    [Fact]
    public async Task RefreshDueTokensAsync_refreshes_a_token_nearing_expiry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBroadcasterTokenStore(time);
        var record = store.IssueToken("channel-1", "CMDR", "old-access", "old-refresh", time.Now.AddMinutes(10));

        var twitch = new FakeTwitchOAuthClient
        {
            OnRefresh = refreshToken =>
            {
                Assert.Equal("old-refresh", refreshToken);
                return new TwitchTokenResponse { AccessToken = "new-access", RefreshToken = "new-refresh", ExpiresIn = 14400 };
            },
        };
        var options = new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 }; // 10 minutes left is within the 60-minute buffer
        var service = CreateService(store, twitch, time, options);

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.Equal(1, twitch.RefreshCalls);
        Assert.True(store.TryGetByToken(record.Token, out var updated));
        Assert.Equal("new-access", updated.TwitchAccessToken);
        Assert.Equal("new-refresh", updated.TwitchRefreshToken);
        Assert.True(updated.IsTwitchGrantValid);
    }

    [Fact]
    public async Task RefreshDueTokensAsync_picks_up_persisted_grants_after_a_restart_and_writes_the_refresh_back_durably()
    {
        using var data = new TempEbsDataDirectory();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var token = data.CreateTokenStore(time).IssueToken("channel-1", "CMDR", "old-access", "old-refresh", time.Now.AddMinutes(10)).Token;

        var twitch = new FakeTwitchOAuthClient
        {
            OnRefresh = refreshToken =>
            {
                Assert.Equal("old-refresh", refreshToken); // decrypted from disk by the restarted store
                return new TwitchTokenResponse { AccessToken = "new-access", RefreshToken = "new-refresh", ExpiresIn = 14400 };
            },
        };
        var service = CreateService(data.CreateTokenStore(time), twitch, time, new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 });

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.Equal(1, twitch.RefreshCalls);
        Assert.True(data.CreateTokenStore(time).TryGetByToken(token, out var updated));
        Assert.Equal("new-access", updated.TwitchAccessToken);
        Assert.Equal("new-refresh", updated.TwitchRefreshToken);
        Assert.Equal(time.Now.AddSeconds(14400), updated.TwitchExpiresAtUtc);
    }

    [Fact]
    public async Task RefreshDueTokensAsync_does_not_refresh_a_token_that_is_not_yet_within_the_buffer()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBroadcasterTokenStore(time);
        store.IssueToken("channel-1", "CMDR", "access", "refresh", time.Now.AddHours(5));

        var twitch = new FakeTwitchOAuthClient();
        var options = new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 };
        var service = CreateService(store, twitch, time, options);

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.Equal(0, twitch.RefreshCalls);
    }

    [Fact]
    public async Task RefreshDueTokensAsync_marks_the_grant_invalid_when_Twitch_rejects_the_refresh()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBroadcasterTokenStore(time);
        var record = store.IssueToken("channel-1", "CMDR", "access", "revoked-refresh", time.Now.AddMinutes(1));

        var twitch = new FakeTwitchOAuthClient { OnRefresh = _ => throw new TwitchOAuthException("invalid refresh token", 400) };
        var options = new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 };
        var service = CreateService(store, twitch, time, options);

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.True(store.TryGetByToken(record.Token, out var updated));
        Assert.False(updated.IsTwitchGrantValid);
    }

    [Fact]
    public async Task RefreshDueTokensAsync_skips_tokens_already_marked_invalid()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBroadcasterTokenStore(time);
        store.IssueToken("channel-1", "CMDR", "access", "refresh", time.Now.AddMinutes(1));
        store.MarkTwitchGrantInvalid("channel-1");

        var twitch = new FakeTwitchOAuthClient();
        var options = new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 };
        var service = CreateService(store, twitch, time, options);

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.Equal(0, twitch.RefreshCalls);
    }

    [Fact]
    public async Task RefreshDueTokensAsync_continues_past_one_channels_failure_to_refresh_the_rest()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBroadcasterTokenStore(time);
        store.IssueToken("channel-fails", "A", "access", "bad-refresh", time.Now.AddMinutes(1));
        var goodRecord = store.IssueToken("channel-ok", "B", "access", "good-refresh", time.Now.AddMinutes(1));

        var twitch = new FakeTwitchOAuthClient
        {
            OnRefresh = refreshToken => refreshToken == "bad-refresh"
                ? throw new TwitchOAuthException("nope", 400)
                : new TwitchTokenResponse { AccessToken = "new-access", RefreshToken = "new-refresh", ExpiresIn = 3600 },
        };
        var options = new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 };
        var service = CreateService(store, twitch, time, options);

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.Equal(2, twitch.RefreshCalls);
        Assert.True(store.TryGetByToken(goodRecord.Token, out var updated));
        Assert.True(updated.IsTwitchGrantValid);
        Assert.Equal("new-access", updated.TwitchAccessToken);
    }

    [Fact]
    public async Task RefreshDueTokensAsync_a_transient_transport_failure_on_one_channel_does_not_abort_the_rest_of_the_pass()
    {
        // Regression test: only TwitchOAuthException (an explicit Twitch rejection) used to be caught
        // per-record. Any other exception — HttpRequestException, TaskCanceledException, a socket
        // reset — propagated out of the foreach entirely, silently skipping every broadcaster later
        // in enumeration order for that cycle, not just the one that actually failed.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBroadcasterTokenStore(time);
        var flakyRecord = store.IssueToken("channel-flaky", "A", "access", "flaky-refresh", time.Now.AddMinutes(1));
        var laterRecord = store.IssueToken("channel-later", "B", "access", "fine-refresh", time.Now.AddMinutes(1));

        var twitch = new FakeTwitchOAuthClient
        {
            OnRefresh = refreshToken => refreshToken == "flaky-refresh"
                ? throw new HttpRequestException("connection reset")
                : new TwitchTokenResponse { AccessToken = "new-access", RefreshToken = "new-refresh", ExpiresIn = 3600 },
        };
        var options = new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 };
        var service = CreateService(store, twitch, time, options);

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.Equal(2, twitch.RefreshCalls); // both were attempted — the flaky one didn't abort the loop

        Assert.True(store.TryGetByToken(flakyRecord.Token, out var flaky));
        Assert.True(flaky.IsTwitchGrantValid); // a transient failure must not be treated as a Twitch rejection
        Assert.Equal("access", flaky.TwitchAccessToken); // unchanged — will simply retry next cycle

        Assert.True(store.TryGetByToken(laterRecord.Token, out var later));
        Assert.True(later.IsTwitchGrantValid);
        Assert.Equal("new-access", later.TwitchAccessToken); // still refreshed despite the earlier failure
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(null)] // an unparseable / empty body carries no status at all
    public async Task RefreshDueTokensAsync_leaves_the_grant_valid_when_Twitch_fails_transiently(int? status)
    {
        // A Twitch outage or rate limit says nothing about the grant. It used to be treated as "grant
        // gone", which logged out every broadcaster that came due during the outage.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBroadcasterTokenStore(time);
        var record = store.IssueToken("channel-1", "CMDR", "access", "refresh", time.Now.AddMinutes(1));
        var twitch = new FakeTwitchOAuthClient { OnRefresh = _ => throw new TwitchOAuthException("Twitch is having a bad day", status) };
        var service = CreateService(store, twitch, time, new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 });

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.Equal(1, twitch.RefreshCalls);
        Assert.True(store.TryGetByToken(record.Token, out var updated));
        Assert.True(updated.IsTwitchGrantValid);
        Assert.Equal("refresh", updated.TwitchRefreshToken);
    }

    [Theory]
    [InlineData(400, "Invalid refresh token")]
    [InlineData(401, "invalid_grant")]
    public async Task RefreshDueTokensAsync_marks_the_grant_invalid_on_a_400_or_401_for_the_refresh_token(int status, string message)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBroadcasterTokenStore(time);
        var record = store.IssueToken("channel-1", "CMDR", "access", "refresh", time.Now.AddMinutes(1));
        var twitch = new FakeTwitchOAuthClient { OnRefresh = _ => throw new TwitchOAuthException(message, status) };
        var service = CreateService(store, twitch, time, new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 });

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.True(store.TryGetByToken(record.Token, out var updated));
        Assert.False(updated.IsTwitchGrantValid);
    }

    [Fact]
    public async Task RefreshDueTokensAsync_does_not_log_everyone_out_when_the_client_secret_is_wrong()
    {
        // "invalid client secret" is a 400 too, but it is this service's misconfiguration (a rotated
        // secret), not the broadcaster's grant: invalidating here would log out every broadcaster.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBroadcasterTokenStore(time);
        var record = store.IssueToken("channel-1", "CMDR", "access", "refresh", time.Now.AddMinutes(1));
        var twitch = new FakeTwitchOAuthClient
        {
            OnRefresh = _ => throw new TwitchOAuthException("""Twitch rejected the token request: HTTP 400 - {"status":400,"message":"invalid client secret"}""", 400),
        };
        var service = CreateService(store, twitch, time, new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 });

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.True(store.TryGetByToken(record.Token, out var updated));
        Assert.True(updated.IsTwitchGrantValid);
    }

    [Fact]
    public async Task RefreshDueTokensAsync_dates_the_new_expiry_from_when_Twitch_answered_not_from_the_start_of_the_pass()
    {
        // The pass can be slow (one network call per broadcaster), so the clock is read per record.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryBroadcasterTokenStore(time);
        var record = store.IssueToken("channel-1", "A", "access", "refresh", time.Now.AddMinutes(1));
        var twitch = new FakeTwitchOAuthClient
        {
            OnRefresh = _ =>
            {
                time.Now += TimeSpan.FromMinutes(5); // the call took five minutes
                return new TwitchTokenResponse { AccessToken = "new-access", RefreshToken = "r", ExpiresIn = 3600 };
            },
        };
        var service = CreateService(store, twitch, time, new EbsOptions { TwitchTokenRefreshBufferMinutes = 60 });

        await service.RefreshDueTokensAsync(CancellationToken.None);

        Assert.True(store.TryGetByToken(record.Token, out var updated));
        Assert.Equal(time.Now.AddSeconds(3600), updated.TwitchExpiresAtUtc);
    }
}
