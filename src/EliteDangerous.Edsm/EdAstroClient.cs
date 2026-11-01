using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EliteDangerous.Edsm;

/// <summary>
/// Static configuration for an <see cref="EdAstroClient"/>.
/// </summary>
public sealed class EdAstroClientOptions
{
    /// <summary>A unique, stable name for the calling application (e.g. "EDNexus"), sent as the User-Agent product.</summary>
    public required string SoftwareName { get; init; }

    /// <summary>The calling application's version, sent as the User-Agent product version.</summary>
    public required string SoftwareVersion { get; init; }

    /// <summary>
    /// The EDAstro base address. Must be an absolute <c>https</c> URL (checked when the client is
    /// constructed). Overridable for tests; redirects to any other host are refused.
    /// </summary>
    public string BaseUrl { get; init; } = "https://edastro.com";

    /// <summary>
    /// The largest response body the client will read, in bytes. The real catalogue is ~2 MB; anything
    /// bigger than this is refused rather than buffered and parsed.
    /// </summary>
    public long MaxResponseBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>The longest <c>Retry-After</c> the client will wait out before retrying a 429/503 once.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait before the single retry of a 429/503 that carried no <c>Retry-After</c>.</summary>
    public TimeSpan TransientRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// A Galactic Exploration Catalog point of interest (EDAstro). <see cref="Type"/> is the catalogue
/// category (e.g. "Organic", "Nebulae"). All strings are third-party content that the client has
/// stripped of control characters and truncated; <see cref="Url"/> is always an absolute http(s) URL
/// or null. Content is community authored under CC BY-NC-SA 3.0 — attribute it where shown.
/// </summary>
/// <param name="Id">The catalogue id, or 0 when the entry carried none.</param>
/// <param name="SystemId64">The 64-bit system address; null when absent or not positive.</param>
public sealed record GecPoi(
    long Id,
    string Name,
    string Type,
    string? Region,
    string? SystemName,
    long? SystemId64,
    EdsmCoords? Coords,
    string? Summary,
    double? Rating,
    string? Url);

/// <summary>
/// Reads the EDAstro Galactic Exploration Catalog (GEC). EDAstro serves the catalogue in EDSM's
/// galactic-mapping JSON shape, but it is a different host with different terms, so it has its own
/// client rather than living on <see cref="EdsmClient"/>. Like that client it never throws for
/// network/HTTP/parse problems (they become <see cref="EdsmResult{T}.Failure"/>); the one exception
/// is <see cref="OperationCanceledException"/> when the caller's token is cancelled.
/// </summary>
/// <remarks>
/// Everything in the reply is untrusted: the body size is bounded, strings are length-capped and
/// stripped of control/format characters, links must be http(s), and redirects off the configured
/// host are refused. EDAstro rate-limits the API (100 requests per 15 minutes per its documentation;
/// the live reply also carries <c>RateLimit-*</c> headers), so callers should cache.
/// </remarks>
public sealed class EdAstroClient : IDisposable
{
    internal const int MaxNameLength = 200;
    internal const int MaxTypeLength = 60;
    internal const int MaxRegionLength = 100;
    internal const int MaxSystemNameLength = 100;
    internal const int MaxSummaryLength = 300;
    internal const int MaxUrlLength = 300;

    private readonly EdAstroClientOptions _options;
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <param name="options">Client configuration; <see cref="EdAstroClientOptions.BaseUrl"/> must be absolute https.</param>
    /// <param name="http">
    /// An HTTP client to use (tests). When omitted the client creates its own with automatic redirects
    /// switched off. A supplied client's redirects cannot be disabled, so the final host is checked instead.
    /// </param>
    /// <exception cref="ArgumentException"><see cref="EdAstroClientOptions.BaseUrl"/> is not an absolute https URL.</exception>
    public EdAstroClient(EdAstroClientOptions options, HttpClient? http = null)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(baseUri.Host) || !string.IsNullOrEmpty(baseUri.UserInfo))
            throw new ArgumentException("BaseUrl must be an absolute https URL without credentials.", nameof(options));

        _options = options;
        _baseUri = baseUri;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(Sanitize(options.SoftwareName), Sanitize(options.SoftwareVersion)));
    }

    /// <summary>
    /// Fetch every GEC point of interest (<c>/gec/json/all</c>, ~2 MB). Descriptions and images are
    /// skipped while parsing. Entries without a name or that are not objects are dropped, duplicates
    /// (same positive id, or same name and system address when there is no id) keep the first, and a
    /// reply that is not an array is treated as "no data". Throws only <see cref="OperationCanceledException"/>
    /// when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<EdsmResult<IReadOnlyList<GecPoi>>> GetPointsOfInterestAsync(CancellationToken ct = default)
    {
        var url = new Uri(_baseUri, "/gec/json/all");
        try
        {
            using var response = await GetWithRetryAsync(url, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
                return EdsmResult<IReadOnlyList<GecPoi>>.Failure($"HTTP {(int)response.StatusCode} (redirects are not followed)");
            if (!response.IsSuccessStatusCode)
                return EdsmResult<IReadOnlyList<GecPoi>>.Failure($"HTTP {(int)response.StatusCode}");

            // Covers a supplied HttpClient that followed a redirect on its own.
            var final = response.RequestMessage?.RequestUri;
            if (final is not null && (final.Scheme != Uri.UriSchemeHttps || !string.Equals(final.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase)))
                return EdsmResult<IReadOnlyList<GecPoi>>.Failure("redirected to another host");

            if (response.Content.Headers.ContentLength is { } declared && declared > _options.MaxResponseBytes)
                return EdsmResult<IReadOnlyList<GecPoi>>.Failure("response too large");

            var body = await ReadBoundedAsync(response.Content, _options.MaxResponseBytes, ct).ConfigureAwait(false);
            if (body is null) return EdsmResult<IReadOnlyList<GecPoi>>.Failure("response too large");

            return EdsmResult<IReadOnlyList<GecPoi>>.Ok(Parse(body));
        }
        catch (JsonException ex) { return EdsmResult<IReadOnlyList<GecPoi>>.Failure("unparseable response: " + ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return EdsmResult<IReadOnlyList<GecPoi>>.Failure(ex.Message); }   // incl. an HttpClient timeout
    }

    private static List<GecPoi> Parse(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var pois = new List<GecPoi>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return pois;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object || Clean(ReadString(el, "name"), MaxNameLength) is not { Length: > 0 } name) continue;

            var id = ReadNullableLong(el, "id") is { } i && i > 0 ? i : 0;
            var id64 = ReadNullableLong(el, "id64") is { } s && s > 0 ? s : (long?)null;
            var key = id > 0
                ? "i" + id.ToString(CultureInfo.InvariantCulture)
                : "n" + name.ToUpperInvariant() + "|" + id64?.ToString(CultureInfo.InvariantCulture);
            if (!seen.Add(key)) continue;

            pois.Add(new GecPoi(
                Id: id,
                Name: name,
                Type: Clean(ReadString(el, "type"), MaxTypeLength) ?? "",
                Region: Clean(ReadString(el, "region"), MaxRegionLength),
                SystemName: Clean(ReadString(el, "galMapSearch"), MaxSystemNameLength),
                SystemId64: id64,
                Coords: ReadCoordArray(el, "coordinates"),
                Summary: Clean(ReadString(el, "summary"), MaxSummaryLength),
                Rating: ReadNullableDouble(el, "rating"),
                Url: SafeUrl(ReadString(el, "poiUrl"))));
        }
        return pois;
    }

    /// <summary>Returns the body, or null when it exceeds <paramref name="max"/> bytes (without buffering the excess).</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, long max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > max) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>Only absolute http/https links without embedded credentials, of sane length; anything else is dropped.</summary>
    internal static string? SafeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxUrlLength) return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var s = uri.AbsoluteUri;
        return s.Length <= MaxUrlLength ? s : null;
    }

    /// <summary>Trim, drop control and format (e.g. bidi override) characters, and cap the length; empty becomes null.</summary>
    internal static string? Clean(string? raw, int maxLength)
    {
        if (raw is null) return null;
        var sb = new System.Text.StringBuilder(Math.Min(raw.Length, maxLength));
        foreach (var c in raw)
        {
            var cat = char.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned) continue;
            sb.Append(c);
        }
        var s = sb.ToString().Trim();
        if (s.Length > maxLength)
        {
            var cut = maxLength;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;   // don't split a surrogate pair
            s = s[..cut].TrimEnd();
        }
        return s.Length == 0 ? null : s;
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(Uri url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            return response;

        var wait = ReadRetryAfter(response) ?? _options.TransientRetryDelay;
        if (wait > _options.MaxRetryAfter) return response;

        response.Dispose();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        return await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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

    private static EdsmCoords? ReadCoordArray(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 3) return null;
        var v = new double[3];
        var i = 0;
        foreach (var n in a.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out v[i++])) return null;
        }
        return new EdsmCoords(v[0], v[1], v[2]);
    }

    private static string? ReadString(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? ReadNullableLong(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static double? ReadNullableDouble(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

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
