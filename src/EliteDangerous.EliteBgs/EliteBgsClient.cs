using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EliteDangerous.EliteBgs;

/// <summary>
/// Queries the EliteBGS v5 read API (factions and systems) and parses the replies into plain records.
/// Pure transport plus a small in-memory cache: it does not decide when to look anything up. It never
/// throws for network/HTTP/parse problems (failures are <see cref="EliteBgsResult{T}.Failure"/>), it
/// honours an optional consent gate before any request (a gate that throws counts as "disabled"),
/// retries a 429/503 once, and backs off for a clamped cool-down after an un-retried 429/503.
/// Concurrent lookups of the same key share one request. A single instance is safe to reuse.
/// </summary>
/// <remarks>
/// <para>
/// <b>Response shapes are unverified against the live API.</b> The field names used here
/// (<c>docs</c>, <c>name</c>, <c>faction_presence</c>, <c>system_name</c>, <c>influence</c>,
/// <c>active_states</c>, <c>factions[].faction_details.faction_presence</c>, ...) were written from
/// memory of the EliteBGS v5 API and have not been checked against a live reply; the unit tests assert
/// the same guessed JSON, so they prove the parser is self-consistent, not that it matches EliteBGS.
/// Treat the shapes as provisional until a live capture is recorded.
/// </para>
/// <para>
/// EliteBGS replies with a paginated envelope (<c>{ "docs": [...] }</c>). Name queries can match more
/// than one document, so the client picks the exact (case-insensitive) name match from the first page
/// only. A first page with named candidates but no exact match is returned as Ok(null) but is NOT
/// cached (the real match may be on a later page). A non-empty <c>docs</c> where no element has a
/// <c>name</c> is reported as a Failure ("unrecognised response shape") and not cached, so a renamed
/// field is not mistaken for "no such faction". Every other field is read defensively.
/// </para>
/// </remarks>
public sealed class EliteBgsClient : IDisposable
{
    private readonly EliteBgsClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;
    private readonly ProductInfoHeaderValue _userAgent;
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly Dictionary<string, Task> _inflight = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Cooldown? _cooldown;

    private sealed record CacheEntry(object? Value, long StoredAt);
    private sealed record Cooldown(long Start, TimeSpan Duration);

    /// <summary>Create a client. Throws <see cref="ArgumentException"/> if <c>BaseUrl</c> is not https (or loopback).</summary>
    public EliteBgsClient(EliteBgsClientOptions options, HttpClient? http = null)
    {
        _options = options;
        _baseUrl = ValidateBaseUrl(options.BaseUrl);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _userAgent = new ProductInfoHeaderValue(Sanitize(options.SoftwareName), Sanitize(options.SoftwareVersion));
    }

    private static string ValidateBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new ArgumentException("BaseUrl must be an absolute https URL (http is allowed only for loopback).", nameof(baseUrl));
        return baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Look up a faction by exact name, including every system it is present in with influence and
    /// states. An OK result with a null value means EliteBGS has no such faction (or, if it listed
    /// near-matches only, that the exact one was not on the first page).
    /// </summary>
    public Task<EliteBgsResult<BgsFaction>> GetFactionAsync(string factionName, CancellationToken ct = default)
        => LookupAsync("faction", factionName, "/api/ebgs/v5/factions", ParseFaction, ct);

    /// <summary>
    /// Look up a system by exact name, including its controlling faction and every faction present.
    /// An OK result with a null value means EliteBGS has no such system.
    /// </summary>
    public Task<EliteBgsResult<BgsSystem>> GetSystemAsync(string systemName, CancellationToken ct = default)
        => LookupAsync("system", systemName, "/api/ebgs/v5/systems", ParseSystem, ct);

    /// <summary>Drop every cached lookup (e.g. after the weekly tick, or when the user turns the source off).</summary>
    public void ClearCache()
    {
        lock (_gate) _cache.Clear();
    }

    private bool IsAllowed()
    {
        try { return _options.IsEnabled?.Invoke() ?? true; }
        catch { return false; }   // a throwing consent gate is treated as "disabled"
    }

    private async Task<EliteBgsResult<T>> LookupAsync<T>(
        string kind, string name, string path, Func<JsonElement, T?> parseDoc, CancellationToken ct)
        where T : class
    {
        ct.ThrowIfCancellationRequested();
        if (!IsAllowed())
            return EliteBgsResult<T>.Failure("EliteBGS lookups are disabled");
        if (string.IsNullOrWhiteSpace(name))
            return EliteBgsResult<T>.Ok(null);

        string trimmed;
        try { trimmed = name.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return EliteBgsResult<T>.Failure("invalid name"); }
        if (trimmed.Length > Math.Max(1, _options.MaxNameLength))
            return EliteBgsResult<T>.Failure("name too long");

        var key = kind + "|" + trimmed.ToLowerInvariant();
        TaskCompletionSource<EliteBgsResult<T>>? tcs = null;
        Task<EliteBgsResult<T>> shared;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit)
                && _options.Clock.GetElapsedTime(hit.StoredAt) < _options.CacheTtl)
                return EliteBgsResult<T>.Ok(hit.Value as T, fromCache: true);

            if (CooldownRemaining() is { } wait)
                return EliteBgsResult<T>.Failure(
                    $"backing off; retry in {Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s", rateLimited: true);

            if (_inflight.TryGetValue(key, out var existing))
                shared = (Task<EliteBgsResult<T>>)existing;
            else
            {
                tcs = new TaskCompletionSource<EliteBgsResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
                shared = tcs.Task;
                _inflight[key] = shared;
            }
        }

        if (tcs is not null)
        {
            var owner = tcs;
            var url = $"{_baseUrl}{path}?name={Uri.EscapeDataString(trimmed)}";
            _ = Task.Run(async () =>
            {
                try { owner.SetResult(await FetchAsync(key, url, trimmed, parseDoc, _lifetime.Token).ConfigureAwait(false)); }
                catch (Exception ex) { owner.SetResult(EliteBgsResult<T>.Failure(ex.Message)); }
                finally { lock (_gate) _inflight.Remove(key); }
            });
        }

        // The shared request is not tied to any one caller's token; each caller just stops waiting.
        return await shared.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<EliteBgsResult<T>> FetchAsync<T>(
        string key, string url, string name, Func<JsonElement, T?> parseDoc, CancellationToken ct)
        where T : class
    {
        try
        {
            using var response = await GetWithRetryAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                StartCooldown(ReadRetryAfter(response));
                return EliteBgsResult<T>.Failure("HTTP 429", rateLimited: true);
            }
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable)
            {
                StartCooldown(ReadRetryAfter(response));
                return EliteBgsResult<T>.Failure("HTTP 503");
            }
            if (!response.IsSuccessStatusCode)
                return EliteBgsResult<T>.Failure($"HTTP {(int)response.StatusCode}");

            var bytes = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
            if (bytes is null) return EliteBgsResult<T>.Failure("response too large");

            using var doc = JsonDocument.Parse(bytes);
            switch (FindDoc(doc.RootElement, name, out var el))
            {
                case Match.Found:
                    var value = parseDoc(el);
                    Store(key, value);
                    return EliteBgsResult<T>.Ok(value);
                case Match.Candidates:
                    return EliteBgsResult<T>.Ok(null);   // not cached: the exact match may be on a later page
                case Match.Unrecognised:
                    return EliteBgsResult<T>.Failure("unrecognised response shape");
                default:
                    Store(key, null);
                    return EliteBgsResult<T>.Ok(null);
            }
        }
        catch (JsonException ex) { return EliteBgsResult<T>.Failure("unparseable response: " + ex.Message); }
        catch (Exception ex) { return EliteBgsResult<T>.Failure(ex.Message); }   // incl. an HttpClient timeout
    }

    /// <summary>Read the body up to <see cref="EliteBgsClientOptions.MaxResponseBytes"/>; null when it is larger.</summary>
    private async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var max = Math.Max(1024, _options.MaxResponseBytes);
        if (response.Content.Headers.ContentLength is { } len && len > max) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > max) return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private void Store(string key, object? value)
    {
        if (_options.CacheTtl <= TimeSpan.Zero) return;
        var clock = _options.Clock;
        var max = Math.Max(1, _options.MaxCacheEntries);
        lock (_gate)
        {
            if (!_cache.ContainsKey(key) && _cache.Count >= max)
            {
                foreach (var stale in _cache.Where(kv => clock.GetElapsedTime(kv.Value.StoredAt) >= _options.CacheTtl).Select(kv => kv.Key).ToList())
                    _cache.Remove(stale);
                if (_cache.Count >= max)
                {
                    var oldest = _cache.OrderBy(kv => kv.Value.StoredAt).Select(kv => kv.Key).FirstOrDefault();
                    if (oldest is not null) _cache.Remove(oldest);
                }
            }
            _cache[key] = new CacheEntry(value, clock.GetTimestamp());
        }
    }

    private TimeSpan? CooldownRemaining()
    {
        var c = Volatile.Read(ref _cooldown);
        if (c is null) return null;
        var remaining = c.Duration - _options.Clock.GetElapsedTime(c.Start);
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    private void StartCooldown(TimeSpan? retryAfter)
    {
        var max = _options.MaxCooldown > TimeSpan.Zero ? _options.MaxCooldown : TimeSpan.FromMinutes(15);
        var wait = retryAfter is { } r && r > _options.RateLimitCooldown ? r : _options.RateLimitCooldown;
        if (wait > max) wait = max;
        Volatile.Write(ref _cooldown, new Cooldown(_options.Clock.GetTimestamp(), wait));
    }

    /// <summary>
    /// GET with a single retry when the server says it is busy (429/503): waits the server's
    /// <c>Retry-After</c> (or <see cref="EliteBgsClientOptions.TransientRetryDelay"/>) if that is within
    /// <see cref="EliteBgsClientOptions.MaxRetryAfter"/>, and otherwise hands the busy response back.
    /// </summary>
    private async Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken ct)
    {
        var response = await SendAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            return response;

        var wait = ReadRetryAfter(response) ?? _options.TransientRetryDelay;
        if (wait > _options.MaxRetryAfter) return response;

        response.Dispose();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _options.Clock, ct).ConfigureAwait(false);
        return await SendAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>One GET with the User-Agent set on the request (never on a shared client's defaults).</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(_userAgent);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta >= TimeSpan.Zero ? delta : null;
        if (header?.Date is { } date)
        {
            var wait = date - _options.Clock.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    // ---- parsing ----------------------------------------------------------------------------

    private enum Match { None, Found, Candidates, Unrecognised }

    /// <summary>
    /// Pick the document whose name matches exactly (tolerating a bare array or single object).
    /// <see cref="Match.Candidates"/>: named documents but none exact. <see cref="Match.Unrecognised"/>:
    /// a non-empty array in which nothing has a string <c>name</c>.
    /// </summary>
    private static Match FindDoc(JsonElement root, string name, out JsonElement found)
    {
        found = default;
        IEnumerable<JsonElement> candidates = root.ValueKind switch
        {
            JsonValueKind.Object when root.TryGetProperty("docs", out var d) && d.ValueKind == JsonValueKind.Array => d.EnumerateArray(),
            JsonValueKind.Object when ReadString(root, "name") is not null => new[] { root },
            JsonValueKind.Array => root.EnumerateArray(),
            _ => Array.Empty<JsonElement>(),
        };
        var any = false;
        var anyNamed = false;
        foreach (var c in candidates)
        {
            any = true;
            if (ReadString(c, "name") is not { } n) continue;
            anyNamed = true;
            if (NamesEqual(n, name)) { found = c; return Match.Found; }
        }
        return !any ? Match.None : anyNamed ? Match.Candidates : Match.Unrecognised;
    }

    private static bool NamesEqual(string a, string b)
    {
        try { a = a.Normalize(NormalizationForm.FormC); } catch (ArgumentException) { }
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static BgsFaction? ParseFaction(JsonElement e)
    {
        if (ReadString(e, "name") is not { } name) return null;

        var presence = new List<BgsFactionPresence>();
        foreach (var p in ReadArray(e, "faction_presence"))
            if (ReadString(p, "system_name") is { } system)
                presence.Add(new BgsFactionPresence(
                    system,
                    ReadString(p, "state"),
                    ReadInfluence(p),
                    ReadString(p, "happiness"),
                    ReadStates(p, "active_states"),
                    ReadStates(p, "pending_states"),
                    ReadStates(p, "recovering_states"),
                    ReadConflicts(p),
                    ReadDate(p, "updated_at")));

        return new BgsFaction(name, ReadString(e, "allegiance"), ReadString(e, "government"), ReadDate(e, "updated_at"), presence.AsReadOnly());
    }

    private static BgsSystem? ParseSystem(JsonElement e)
    {
        if (ReadString(e, "name") is not { } name) return null;

        var factions = new List<BgsSystemFaction>();
        foreach (var f in ReadArray(e, "factions"))
        {
            if (ReadString(f, "name") is not { } fname) continue;
            // v5 nests the standing under faction_details.faction_presence; older shapes put it inline.
            var standing = f;
            if (f.TryGetProperty("faction_details", out var fd) && fd.ValueKind == JsonValueKind.Object
                && fd.TryGetProperty("faction_presence", out var fp) && fp.ValueKind == JsonValueKind.Object)
                standing = fp;
            factions.Add(new BgsSystemFaction(
                fname,
                ReadString(standing, "state"),
                ReadInfluence(standing),
                ReadStates(standing, "active_states"),
                ReadStates(standing, "pending_states"),
                ReadStates(standing, "recovering_states")));
        }
        // Stable order: highest influence first (unknown last), then by name.
        var ordered = factions.OrderByDescending(f => f.Influence ?? -1).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();

        return new BgsSystem(
            name,
            ReadString(e, "controlling_minor_faction") ?? ReadString(e, "controlling_faction"),
            ReadString(e, "allegiance"),
            ReadString(e, "government"),
            ReadString(e, "security"),
            ReadLong(e, "population"),
            ReadDate(e, "updated_at"),
            ordered.AsReadOnly(),
            ReadConflicts(e));
    }

    private static IReadOnlyList<BgsState> ReadStates(JsonElement e, string prop)
    {
        var list = new List<BgsState>();
        foreach (var s in ReadArray(e, prop))
        {
            // Entries are normally { "state": "boom", "trend": 0 }, but tolerate a bare string.
            if (s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } str) list.Add(new BgsState(str));
            else if (s.ValueKind == JsonValueKind.Object && ReadString(s, "state") is { } st)
                list.Add(new BgsState(st, ReadInt(s, "trend")));
        }
        return list.AsReadOnly();
    }

    private static IReadOnlyList<BgsConflict> ReadConflicts(JsonElement e)
    {
        var list = new List<BgsConflict>();
        foreach (var c in ReadArray(e, "conflicts"))
        {
            if (c.ValueKind != JsonValueKind.Object || ReadString(c, "type") is not { } type) continue;
            list.Add(new BgsConflict(type, ReadString(c, "status"), ReadString(c, "opponent_name"),
                ReadString(c, "stake"), ReadInt(c, "days_won")));
        }
        return list.AsReadOnly();
    }

    /// <summary>
    /// Influence as a 0..1 fraction (0.4231 = 42.31%), the single unit this client assumes. Negative
    /// values are dropped and anything above 1 is clamped to 1; there is deliberately no percentage
    /// heuristic, since a bare 0.8 or 1 would be ambiguous. UNVERIFIED against the live API: if
    /// EliteBGS reports percentages this must change.
    /// </summary>
    private static double? ReadInfluence(JsonElement e)
    {
        var v = ReadDouble(e, "influence");
        if (v is null || v < 0) return null;
        return Math.Min(v.Value, 1.0);
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string? ReadString(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString() is { Length: > 0 } s ? s : null;

    private static double? ReadDouble(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) ? d : null;

    private static long? ReadLong(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var l) ? l : null;

    private static int? ReadInt(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i) ? i : null;

    private static DateTimeOffset? ReadDate(JsonElement e, string prop)
        => ReadString(e, prop) is { } s
           && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>User-Agent product tokens can't contain whitespace or separators; collapse them.</summary>
    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "app" : cleaned;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        if (_ownsHttp) _http.Dispose();
    }
}
