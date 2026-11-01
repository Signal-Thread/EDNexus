using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EliteDangerous.Edsm;

/// <summary>
/// Queries the EDSM read APIs (system position, nearby systems, and a system's bodies) and parses
/// the replies into plain EDSM-shaped records. This is pure transport: it does not decide which
/// system to look up or how to present it — that policy belongs to the caller. Following the Spansh
/// client's convention it never throws for network/HTTP problems; failures surface as
/// <see cref="EdsmResult{T}.Failure"/>, and an unknown system as an OK result with a null value.
/// A single instance is safe to reuse across queries.
/// </summary>
/// <remarks>
/// EDSM answers "system not found" with an empty JSON array where a hit would be an object, so every
/// parser treats a non-object (or empty) reply as "no data" rather than an error.
/// </remarks>
public sealed class EdsmClient : IDisposable
{
    private readonly EdsmClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public EdsmClient(EdsmClientOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(Sanitize(_options.SoftwareName), Sanitize(_options.SoftwareVersion)));
    }

    /// <summary>
    /// Look up a single system's confirmed galactic position. An OK result with a null value means
    /// EDSM has no such system (or no confirmed coordinates for it).
    /// </summary>
    public async Task<EdsmResult<EdsmSystem>> GetSystemAsync(string systemName, CancellationToken ct = default)
    {
        var url = $"{Base}/api-v1/system?systemName={Uri.EscapeDataString(systemName)}&showCoordinates=1";
        return await GetAsync(url, ct, body =>
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && ReadString(doc.RootElement, "name") is { } name
                ? EdsmResult<EdsmSystem>.Ok(new EdsmSystem(name, ReadCoords(doc.RootElement)))
                : EdsmResult<EdsmSystem>.Ok(null);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Find systems within <paramref name="radiusLy"/> of <paramref name="systemName"/>, nearest
    /// first. Radius is clamped to EDSM's 100 ly ceiling. An OK result with an empty list means the
    /// reference system is unknown or genuinely isolated.
    /// </summary>
    public async Task<EdsmResult<IReadOnlyList<EdsmSystem>>> GetNearbySystemsAsync(
        string systemName, double radiusLy, CancellationToken ct = default)
    {
        // Math.Clamp lets NaN straight through (every comparison with it is false), which would put "NaN"
        // in the query string; an unknown radius is treated as zero.
        var radius = (double.IsNaN(radiusLy) ? 0 : Math.Clamp(radiusLy, 0, 100)).ToString(CultureInfo.InvariantCulture);
        var url = $"{Base}/api-v1/sphere-systems?systemName={Uri.EscapeDataString(systemName)}" +
                  $"&radius={radius}&showCoordinates=1";
        return await GetAsync(url, ct, body =>
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return EdsmResult<IReadOnlyList<EdsmSystem>>.Ok(Array.Empty<EdsmSystem>());

            var systems = new List<EdsmSystem>();
            foreach (var el in doc.RootElement.EnumerateArray())
                if (ReadString(el, "name") is { } name)
                    systems.Add(new EdsmSystem(name, ReadCoords(el), ReadDouble(el, "distance")));

            systems.Sort((a, b) => (a.DistanceLy ?? 0).CompareTo(b.DistanceLy ?? 0));
            return EdsmResult<IReadOnlyList<EdsmSystem>>.Ok(systems);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// List the bodies EDSM knows for a system. An OK result with a null value means the system is
    /// unknown to EDSM; an empty <see cref="EdsmSystemBodies.Bodies"/> means it is known but unmapped.
    /// </summary>
    public async Task<EdsmResult<EdsmSystemBodies>> GetBodiesAsync(string systemName, CancellationToken ct = default)
    {
        var url = $"{Base}/api-system-v1/bodies?systemName={Uri.EscapeDataString(systemName)}";
        return await GetAsync(url, ct, body =>
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadString(root, "name") is not { } name)
                return EdsmResult<EdsmSystemBodies>.Ok(null);

            var bodies = new List<EdsmBody>();
            if (root.TryGetProperty("bodies", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var b in arr.EnumerateArray())
                    bodies.Add(new EdsmBody(
                        Name: ReadString(b, "name") ?? "Unknown",
                        Type: ReadString(b, "type") ?? "",
                        SubType: ReadString(b, "subType"),
                        IsLandable: b.TryGetProperty("isLandable", out var l) && l.ValueKind is JsonValueKind.True,
                        DistanceToArrivalLs: ReadNullableDouble(b, "distanceToArrival")));

            return EdsmResult<EdsmSystemBodies>.Ok(new EdsmSystemBodies(name, bodies));
        }).ConfigureAwait(false);
    }

    private string Base => _options.BaseUrl.TrimEnd('/');

    /// <summary>Shared GET + parse plumbing: never throws, mapping every failure onto a Failure result.</summary>
    private async Task<EdsmResult<T>> GetAsync<T>(string url, CancellationToken ct, Func<string, EdsmResult<T>> parse)
        where T : class
    {
        try
        {
            using var response = await GetWithRetryAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return EdsmResult<T>.Failure($"HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return parse(body);
        }
        catch (JsonException ex) { return EdsmResult<T>.Failure("unparseable response: " + ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return EdsmResult<T>.Failure(ex.Message); }   // incl. an HttpClient timeout
    }

    /// <summary>
    /// GET with a single retry when the server says it is busy (429/503): waits the server's
    /// <c>Retry-After</c> (or <see cref="EdsmClientOptions.TransientRetryDelay"/>) if that is within
    /// <see cref="EdsmClientOptions.MaxRetryAfter"/>, and otherwise hands the busy response straight back.
    /// </summary>
    private async Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            return response;

        var wait = ReadRetryAfter(response) ?? _options.TransientRetryDelay;
        if (wait > _options.MaxRetryAfter) return response;

        response.Dispose();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        return await _http.GetAsync(url, ct).ConfigureAwait(false);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta >= TimeSpan.Zero ? delta : null;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static EdsmCoords? ReadCoords(JsonElement e)
    {
        if (!e.TryGetProperty("coords", out var c) || c.ValueKind != JsonValueKind.Object) return null;

        // All three axes or nothing: defaulting a missing one to 0 would put the system at a bogus
        // position (a partial reply would read as Sol-adjacent and be cached for weeks).
        return ReadNullableDouble(c, "x") is { } x && ReadNullableDouble(c, "y") is { } y && ReadNullableDouble(c, "z") is { } z
            ? new EdsmCoords(x, y, z)
            : null;
    }

    private static string? ReadString(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double ReadDouble(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private static double? ReadNullableDouble(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    /// <summary>User-Agent product tokens can't contain whitespace or separators; collapse them.</summary>
    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "app" : cleaned;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
