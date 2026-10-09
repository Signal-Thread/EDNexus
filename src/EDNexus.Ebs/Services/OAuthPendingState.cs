using System.Collections.Concurrent;
using EDNexus.Ebs.Security;

namespace EDNexus.Ebs.Services;

/// <summary>
/// The short-lived half of <see cref="IBroadcasterTokenStore"/>: pending authorize↔callback sessions
/// (minutes) and one-time authorization codes (seconds). Deliberately process-local in every store
/// implementation — a restart mid-login only means the commander clicks "Log in" again, whereas
/// persisting pending authorization codes would write Twitch refresh tokens to disk for a window
/// measured in seconds.
/// </summary>
/// <remarks>
/// An entry nobody redeems (an abandoned login, a probe of <c>/oauth/authorize</c>) would otherwise sit
/// here for the life of the process — and an unredeemed code holds a Twitch refresh token in memory.
/// So expired entries are swept on insert (at most every <see cref="SweepInterval"/>) and each map is
/// capped, evicting the entry closest to expiry when full.
/// </remarks>
internal sealed class OAuthPendingState
{
    /// <summary>Most sessions / codes held at once. Far above any real login rate; the per-IP limit on <c>/oauth/*</c> keeps one caller from filling it.</summary>
    internal const int DefaultMaxEntries = 5000;

    /// <summary>The shortest gap between two sweeps of expired entries.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, OAuthPendingSession> _sessions = new();
    private readonly ConcurrentDictionary<string, PendingBroadcasterAuth> _pendingAuth = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private readonly object _sweepGate = new();
    private DateTimeOffset _lastSweep;

    public OAuthPendingState(TimeProvider timeProvider, int maxEntries = DefaultMaxEntries)
    {
        _timeProvider = timeProvider;
        _maxEntries = Math.Max(1, maxEntries);
        _lastSweep = timeProvider.GetUtcNow();
    }

    /// <summary>Sessions currently held, expired or not (for tests).</summary>
    internal int SessionCount => _sessions.Count;

    /// <summary>Authorization codes currently held, expired or not (for tests).</summary>
    internal int CodeCount => _pendingAuth.Count;

    public string CreateSession(string desktopRedirectUri, string desktopState, string codeChallenge, TimeSpan ttl)
    {
        MakeRoom();
        var sessionId = OpaqueToken.Generate(OpaqueToken.ShortLivedByteLength);
        _sessions[sessionId] = new OAuthPendingSession(desktopRedirectUri, desktopState, codeChallenge, _timeProvider.GetUtcNow() + ttl);
        return sessionId;
    }

    public bool TryConsumeSession(string sessionId, out OAuthPendingSession session)
    {
        if (!_sessions.TryRemove(sessionId, out var found) || found.ExpiresAtUtc < _timeProvider.GetUtcNow())
        {
            session = null!;
            return false;
        }

        session = found;
        return true;
    }

    public string CreateAuthorizationCode(PendingBroadcasterAuth auth, TimeSpan ttl)
    {
        MakeRoom();
        var code = OpaqueToken.Generate(OpaqueToken.ShortLivedByteLength);
        _pendingAuth[code] = auth with { ExpiresAtUtc = _timeProvider.GetUtcNow() + ttl };
        return code;
    }

    public bool TryConsumeAuthorizationCode(string code, out PendingBroadcasterAuth auth)
    {
        if (!_pendingAuth.TryRemove(code, out var found) || found.ExpiresAtUtc < _timeProvider.GetUtcNow())
        {
            auth = null!;
            return false;
        }

        auth = found;
        return true;
    }

    /// <summary>Called before every insert: sweeps expired entries when one is due, then enforces the cap.</summary>
    private void MakeRoom()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_sweepGate)
        {
            if (now - _lastSweep >= SweepInterval)
            {
                _lastSweep = now;
                foreach (var pair in _sessions)
                    if (pair.Value.ExpiresAtUtc < now) _sessions.TryRemove(pair);
                foreach (var pair in _pendingAuth)
                    if (pair.Value.ExpiresAtUtc < now) _pendingAuth.TryRemove(pair);
            }

            // The sweep is throttled, so a burst can still reach the cap between sweeps: drop whatever is
            // closest to expiring rather than refuse a login or grow without bound.
            while (_sessions.Count >= _maxEntries && EvictSoonestToExpire(_sessions, s => s.ExpiresAtUtc)) { }
            while (_pendingAuth.Count >= _maxEntries && EvictSoonestToExpire(_pendingAuth, a => a.ExpiresAtUtc)) { }
        }
    }

    /// <returns>False when there was nothing to evict (a concurrent consume emptied the map).</returns>
    private static bool EvictSoonestToExpire<T>(ConcurrentDictionary<string, T> map, Func<T, DateTimeOffset> expiresAt)
    {
        string? soonestKey = null;
        var soonest = DateTimeOffset.MaxValue;
        foreach (var pair in map)
        {
            var expiry = expiresAt(pair.Value);
            if (soonestKey is null || expiry < soonest)
            {
                soonestKey = pair.Key;
                soonest = expiry;
            }
        }

        if (soonestKey is null) return false;
        map.TryRemove(soonestKey, out _);
        return true;
    }
}
