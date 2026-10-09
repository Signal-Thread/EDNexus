using EDNexus.Ebs.Options;
using EDNexus.Ebs.Security;
using Microsoft.Extensions.Options;

namespace EDNexus.Ebs.Services;

/// <summary>
/// Keeps every broadcaster's underlying Twitch grant alive without them ever having to re-auth: on a
/// fixed interval, walks every issued <see cref="BroadcasterToken"/> and refreshes the Twitch access
/// token for any whose expiry is within <see cref="EbsOptions.TwitchTokenRefreshBufferMinutes"/>. If
/// Twitch rejects the refresh token (a 400/401: the commander revoked access, or it expired), the
/// token is marked invalid so <c>/api/update-state</c> starts rejecting it and the desktop client
/// knows to prompt the commander to log in again — this service never throws or crashes the host on
/// a single broadcaster's failure.
/// </summary>
public sealed class TwitchTokenRefreshBackgroundService : BackgroundService
{
    private readonly IBroadcasterTokenStore _store;
    private readonly ITwitchOAuthClient _twitch;
    private readonly EbsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TwitchTokenRefreshBackgroundService> _logger;

    public TwitchTokenRefreshBackgroundService(
        IBroadcasterTokenStore store,
        ITwitchOAuthClient twitch,
        IOptions<EbsOptions> options,
        TimeProvider timeProvider,
        ILogger<TwitchTokenRefreshBackgroundService> logger)
    {
        _store = store;
        _twitch = twitch;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.TwitchTokenRefreshIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshDueTokensAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single bad cycle (e.g. transient Twitch outage) should not take the loop down.
                _logger.LogError(ex, "Unhandled error while refreshing Twitch tokens.");
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs a single refresh pass. Exposed internally so tests can drive it deterministically without waiting on the loop's delay.</summary>
    internal async Task RefreshDueTokensAsync(CancellationToken ct)
    {
        var buffer = TimeSpan.FromMinutes(Math.Max(0, _options.TwitchTokenRefreshBufferMinutes));

        foreach (var record in _store.GetAllTokens())
        {
            if (!record.IsTwitchGrantValid)
                continue;

            // Per record, not per pass: a pass over many broadcasters (each a network call) can take
            // long enough that a single timestamp would skew the expiry the next token is judged by.
            var now = _timeProvider.GetUtcNow();
            if (record.TwitchExpiresAtUtc - now > buffer)
                continue;

            try
            {
                var refreshed = await _twitch.RefreshTokenAsync(record.TwitchRefreshToken, ct).ConfigureAwait(false);
                var expiresAt = _timeProvider.GetUtcNow().AddSeconds(refreshed.ExpiresIn);
                _store.UpdateTwitchTokens(
                    record.ChannelId,
                    refreshed.AccessToken,
                    string.IsNullOrWhiteSpace(refreshed.RefreshToken) ? record.TwitchRefreshToken : refreshed.RefreshToken,
                    expiresAt);
            }
            catch (TwitchOAuthException ex) when (ex.IsGrantRejection)
            {
                // Twitch explicitly rejected the refresh token (400/401: revoked/expired grant) — it really is gone.
                _logger.LogWarning(
                    ex,
                    "Failed to refresh the Twitch grant for channel {ChannelId}; marking it invalid until the broadcaster re-authenticates.",
                    record.ChannelId);
                _store.MarkTwitchGrantInvalid(record.ChannelId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // real shutdown — let it propagate, don't swallow it as a per-broadcaster failure.
            }
            catch (Exception ex)
            {
                // A transient failure refreshing THIS broadcaster — network blip, DNS, timeout, socket
                // reset, or a TwitchOAuthException that is not a grant rejection (Twitch 5xx/429, an
                // unparseable body, or "invalid client" from a misconfigured secret) — must neither abort
                // the foreach nor log the broadcaster out. The grant is left valid so it is simply
                // retried next cycle; only a 400/401 for the refresh token marks it invalid.
                _logger.LogWarning(
                    ex,
                    "Transient error refreshing the Twitch grant for channel {ChannelId}; will retry next cycle.",
                    record.ChannelId);
            }
        }
    }
}
