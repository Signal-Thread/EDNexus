using System.Text.Json;
using EDNexus.Core.Trade;
using EliteDangerous.Edsm;

namespace EDNexus.Core.Navigation;

/// <summary>
/// The engine-side <see cref="ISystemLookup"/> adapter over the reusable <see cref="EdsmClient"/>.
/// It maps EDSM-shaped records to the engine's <see cref="SystemInfo"/> and caches system positions
/// on disk (they never change) so repeat lookups — e.g. re-plotting from the same system — skip the
/// network, reusing the same <see cref="IResponseCache"/> the trade search and route plotter use.
/// </summary>
public sealed class EdsmSystemLookup : ISystemLookup
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly EdsmClient _client;
    private readonly IResponseCache? _cache;

    public string SourceName => "EDSM";

    public EdsmSystemLookup(EdsmClient client, IResponseCache? cache = null)
    {
        _client = client;
        _cache = cache;
    }

    public async Task<SystemInfo?> GetSystemAsync(string systemName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(systemName)) return null;

        var key = "edsm|system|" + systemName.Trim().ToLowerInvariant();
        if (_cache.GetTyped<SystemInfo>(key, Json) is { } cached)
            return cached;

        var result = await _client.GetSystemAsync(systemName, ct).ConfigureAwait(false);
        if (!result.IsOk || result.Value is null) return null;

        var info = Map(result.Value);
        // A system EDSM knows without coordinates may gain them later: do not pin that for the whole TTL.
        if (info.Coords is not null) _cache?.Put(key, JsonSerializer.Serialize(info, Json));
        return info;
    }

    public async Task<IReadOnlyList<SystemInfo>> GetNearbyAsync(
        string systemName, double radiusLy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(systemName)) return Array.Empty<SystemInfo>();

        var result = await _client.GetNearbySystemsAsync(systemName, radiusLy, ct).ConfigureAwait(false);
        if (!result.IsOk || result.Value is null) return Array.Empty<SystemInfo>();
        return result.Value.Select(Map).ToList();
    }

    public async Task<double?> DistanceBetweenAsync(string from, string to, CancellationToken ct = default)
    {
        // If the first lookup came up empty (unknown system, or a failed/rate-limited call), there is no
        // distance to compute: skip the second request instead of spending it on a certain null.
        var a = await GetSystemAsync(from, ct).ConfigureAwait(false);
        if (a?.Coords is not { } ca) return null;
        var b = await GetSystemAsync(to, ct).ConfigureAwait(false);
        if (b?.Coords is not { } cb) return null;

        double dx = ca.X - cb.X, dy = ca.Y - cb.Y, dz = ca.Z - cb.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static SystemInfo Map(EdsmSystem s) =>
        new(s.Name, s.Coords is { } c ? new SystemCoords(c.X, c.Y, c.Z) : null, s.DistanceLy);
}
