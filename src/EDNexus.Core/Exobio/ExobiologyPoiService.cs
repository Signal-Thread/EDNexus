using System.Text.Json;
using EDNexus.Core.Trade;
using EliteDangerous.Edsm;

namespace EDNexus.Core.Exobio;

/// <summary>
/// Looks up community points of interest (the EDAstro Galactic Exploration Catalog, served in EDSM's
/// galactic-mapping shape) for a system or a position — e.g. notable biological sites worth a detour.
/// <para>
/// This is a third-party network source, so it is <b>opt-in</b>: nothing is requested unless
/// <c>isEnabled</c> returns true, and a disabled service returns empty results. The whole catalogue
/// (a few hundred entries) is fetched once, trimmed of descriptions and images, cached through the
/// shared <see cref="IResponseCache"/> and held in memory, so repeat lookups cost no requests (the
/// EDAstro API is rate limited). Failures never throw: the last good (even stale) copy is used, else
/// the result is empty. POI text is community content under CC BY-NC-SA 3.0 — attribute it in the UI.
/// </para>
/// </summary>
public sealed class ExobiologyPoiService
{
    /// <summary>Cache key for the trimmed catalogue.</summary>
    internal const string CacheKey = "edastro|gec|all|v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly EdsmClient _client;
    private readonly IResponseCache? _cache;
    private readonly Func<bool> _isEnabled;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<GecPoi>? _memory;

    /// <param name="client">The EDSM/EDAstro client (uses its GEC endpoint).</param>
    /// <param name="isEnabled">The opt-in switch, read on every call.</param>
    /// <param name="cache">Optional on-disk cache shared with the other third-party lookups.</param>
    public ExobiologyPoiService(EdsmClient client, Func<bool> isEnabled, IResponseCache? cache = null)
    {
        _client = client;
        _isEnabled = isEnabled;
        _cache = cache;
    }

    /// <summary>POIs in the system with this 64-bit system address (usually zero or one).</summary>
    public async Task<IReadOnlyList<GecPoi>> GetForSystemAsync(long systemAddress, string? type = null, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct).ConfigureAwait(false);
        return all.Where(p => p.SystemId64 == systemAddress && TypeMatches(p, type)).ToList();
    }

    /// <summary>POIs within <paramref name="radiusLy"/> of <paramref name="origin"/>, nearest first.</summary>
    public async Task<IReadOnlyList<(GecPoi Poi, double DistanceLy)>> GetNearbyAsync(
        EdsmCoords origin, double radiusLy, string? type = null, CancellationToken ct = default)
    {
        if (double.IsNaN(radiusLy) || radiusLy < 0) return Array.Empty<(GecPoi, double)>();
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

    private async Task<IReadOnlyList<GecPoi>> LoadAsync(CancellationToken ct)
    {
        if (!_isEnabled()) return Array.Empty<GecPoi>();
        if (_memory is { } m) return m;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_memory is { } again) return again;

            if (_cache.GetTyped<List<GecPoi>>(CacheKey, Json) is { Count: > 0 } cached)
                return _memory = cached;

            var result = await _client.GetGecPoisAsync(ct).ConfigureAwait(false);
            if (result.IsOk && result.Value is { Count: > 0 } fresh)
            {
                try { _cache?.Put(CacheKey, JsonSerializer.Serialize(fresh, Json)); } catch { /* best effort */ }
                return _memory = fresh;
            }

            // Failed or empty: fall back to an expired copy rather than nothing, but don't pin it in
            // memory so the next call retries.
            try
            {
                if (_cache?.GetStale(CacheKey) is { } stale &&
                    JsonSerializer.Deserialize<List<GecPoi>>(stale, Json) is { Count: > 0 } old)
                    return old;
            }
            catch (JsonException) { }
            return Array.Empty<GecPoi>();
        }
        finally { _gate.Release(); }
    }
}
