using System.Text.Json;
using EDNexus.Core.Trade;
using EliteDangerous.Edsm;

namespace EDNexus.Core.Exobio;

/// <summary>
/// Looks up community points of interest (the EDAstro Galactic Exploration Catalog) for a system or a
/// position — e.g. notable biological sites worth a detour.
/// <para>
/// This is a third-party network source, so it is <b>opt-in</b>: nothing is requested unless
/// <c>isEnabled</c> returns true, and a disabled service returns empty results (and drops its
/// in-memory copy). The whole catalogue (a few hundred entries) is fetched once, trimmed of
/// descriptions and images, cached through the shared <see cref="IResponseCache"/> and held in memory
/// for the life of the service, so repeat lookups cost no requests.
/// </para>
/// <para>
/// Outages: a failed (or rejected) fetch is remembered for <see cref="FailureBackoff"/>; during that
/// window no request is made and the last good copy (even an expired one, held in memory) or an empty
/// result is served, so a flaky link cannot burn EDAstro's rate limit (100 requests per 15 minutes) or
/// stall callers. A fresh reply with fewer than half the entries of the copy it would replace is
/// treated as a bad reply and ignored.
/// </para>
/// <para>
/// Failures never throw. The one exception is <see cref="OperationCanceledException"/>, raised when
/// the caller's token is cancelled while waiting for or performing the fetch; callers that pass a
/// token must expect it.
/// </para>
/// <para>
/// <b>Licence and attribution:</b> POI text and links are CC BY-NC-SA 3.0. Wherever they are shown, display
/// <see cref="AttributionText"/> with a link to <see cref="AttributionUrl"/> and note that entries were
/// shortened. Whether that licence is compatible with how EDNexus is distributed is an open owner
/// decision (see Exobio/Data/SOURCES.md); do not surface this data in a release before it is made.
/// </para>
/// </summary>
public sealed class ExobiologyPoiService
{
    /// <summary>Cache key for the trimmed catalogue.</summary>
    internal const string CacheKey = "edastro|gec|all|v1";

    /// <summary>The credit line to show wherever POI data is displayed.</summary>
    public const string AttributionText = "Galactic Exploration Catalog by EDAstro (CC BY-NC-SA 3.0); entries shortened";

    /// <summary>Where <see cref="AttributionText"/> should link.</summary>
    public const string AttributionUrl = "https://edastro.com/gec/";

    /// <summary>How long after a failed fetch the network is left alone.</summary>
    public static readonly TimeSpan DefaultFailureBackoff = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly EdAstroClient _client;
    private readonly IResponseCache? _cache;
    private readonly Func<bool> _isEnabled;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<GecPoi>? _memory;
    private volatile bool _memoryIsStale;
    private long _nextAttemptTicks;   // UTC ticks; 0 = no back-off

    /// <summary>How long a failed fetch suppresses further requests.</summary>
    public TimeSpan FailureBackoff { get; init; } = DefaultFailureBackoff;

    /// <param name="client">The EDAstro client.</param>
    /// <param name="isEnabled">The opt-in switch, read on every call.</param>
    /// <param name="cache">Optional on-disk cache shared with the other third-party lookups.</param>
    /// <param name="time">Clock for the back-off (tests); defaults to the system clock.</param>
    public ExobiologyPoiService(EdAstroClient client, Func<bool> isEnabled, IResponseCache? cache = null, TimeProvider? time = null)
    {
        _client = client;
        _isEnabled = isEnabled;
        _cache = cache;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// POIs in the system with this 64-bit system address (usually zero or one). An address that is
    /// zero or negative (unknown system) matches nothing.
    /// </summary>
    public async Task<IReadOnlyList<GecPoi>> GetForSystemAsync(long systemAddress, string? type = null, CancellationToken ct = default)
    {
        if (systemAddress <= 0) return Array.Empty<GecPoi>();
        var all = await LoadAsync(ct).ConfigureAwait(false);
        return all.Where(p => p.SystemId64 == systemAddress && TypeMatches(p, type)).ToList();
    }

    /// <summary>
    /// POIs within <paramref name="radiusLy"/> of <paramref name="origin"/>, nearest first. Only POIs
    /// with coordinates can match. A negative or NaN radius, or an origin with a non-finite
    /// coordinate, matches nothing; <see cref="double.PositiveInfinity"/> matches every POI with coordinates.
    /// </summary>
    public async Task<IReadOnlyList<(GecPoi Poi, double DistanceLy)>> GetNearbyAsync(
        EdsmCoords origin, double radiusLy, string? type = null, CancellationToken ct = default)
    {
        if (double.IsNaN(radiusLy) || radiusLy < 0 ||
            !double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || !double.IsFinite(origin.Z))
            return Array.Empty<(GecPoi, double)>();
        var all = await LoadAsync(ct).ConfigureAwait(false);
        return all
            .Where(p => p.Coords is not null && TypeMatches(p, type))
            .Select(p => (Poi: p, DistanceLy: origin.DistanceTo(p.Coords!.Value)))
            .Where(x => x.DistanceLy <= radiusLy)
            .OrderBy(x => x.DistanceLy)
            .ToList();
    }

    private static bool TypeMatches(GecPoi p, string? type)
        => string.IsNullOrEmpty(type) || string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase);

    private bool InBackoff() => _time.GetUtcNow().UtcTicks < Interlocked.Read(ref _nextAttemptTicks);

    private async Task<IReadOnlyList<GecPoi>> LoadAsync(CancellationToken ct)
    {
        if (!_isEnabled())
        {
            _memory = null;   // release the catalogue while switched off
            _memoryIsStale = false;
            Interlocked.Exchange(ref _nextAttemptTicks, 0);
            return Array.Empty<GecPoi>();
        }

        // Fast path: a good copy, or any copy while backing off after a failure.
        if (_memory is { } m && (!_memoryIsStale || InBackoff())) return m;
        if (_memory is null && InBackoff()) return Array.Empty<GecPoi>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_memory is { } again && (!_memoryIsStale || InBackoff())) return again;
            if (_memory is null && InBackoff()) return Array.Empty<GecPoi>();

            if (_memory is null && _cache.GetTyped<List<GecPoi>>(CacheKey, Json) is { Count: > 0 } cached)
            {
                _memoryIsStale = false;
                return _memory = cached;
            }

            // The copy a new reply must not shrink drastically relative to: what we hold, else the expired cache entry.
            var previous = _memory ?? ReadStale();

            var result = await _client.GetPointsOfInterestAsync(ct).ConfigureAwait(false);
            if (result.IsOk && result.Value is { Count: > 0 } fresh &&
                (previous is null || fresh.Count * 2 >= previous.Count))
            {
                try { _cache?.Put(CacheKey, JsonSerializer.Serialize(fresh, Json)); } catch { /* best effort */ }
                Interlocked.Exchange(ref _nextAttemptTicks, 0);
                _memoryIsStale = false;
                return _memory = fresh;
            }

            // Failed, empty, or suspiciously small: keep serving the last good copy (memoised, so lookups
            // don't re-read or re-parse it) and leave the network alone for a while.
            Interlocked.Exchange(ref _nextAttemptTicks, (_time.GetUtcNow() + FailureBackoff).UtcTicks);
            if (previous is not null)
            {
                _memoryIsStale = true;
                return _memory = previous;
            }
            return Array.Empty<GecPoi>();
        }
        finally { _gate.Release(); }
    }

    private IReadOnlyList<GecPoi>? ReadStale()
    {
        try
        {
            if (_cache?.GetStale(CacheKey) is { } stale &&
                JsonSerializer.Deserialize<List<GecPoi>>(stale, Json) is { Count: > 0 } old)
                return old;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { }
        return null;
    }
}
