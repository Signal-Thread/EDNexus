using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EliteDangerous.EliteBgs;

/// <summary>
/// Queries the EliteBGS v5 read API (factions and systems) and parses the replies into plain records.
/// Pure transport plus a small in-memory cache: it does not decide when to look anything up. It never
/// throws for network/HTTP/parse problems (failures are <see cref="EliteBgsResult{T}.Failure"/>), it
/// honours an optional consent gate before any request, retries a 429/503 once, and backs off for a
/// cool-down after an un-retried rate-limit. A single instance is safe to reuse across queries.
/// </summary>
/// <remarks>
/// EliteBGS replies with a paginated envelope (<c>{ "docs": [...] }</c>). Name queries can match more
/// than one document, so the client picks the exact (case-insensitive) name match. Every field is read
/// defensively; missing or mistyped fields become null/empty rather than errors.
/// </remarks>
public sealed class EliteBgsClient : IDisposable
{
    private readonly EliteBgsClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private long _cooldownUntilTicks;   // UTC ticks; 0 = not cooling down

    private sealed record CacheEntry(object? Value, DateTimeOffset ExpiresAt, DateTimeOffset StoredAt);

    public EliteBgsClient(EliteBgsClientOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(Sanitize(_options.SoftwareName), Sanitize(_options.SoftwareVersion)));
    }

    /// <summary>
    /// Look up a faction by exact name, including every system it is present in with influence and
    /// states. An OK result with a null value means EliteBGS has no such faction.
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
    public void ClearCache() => _cache.Clear();

    private async Task<EliteBgsResult<T>> LookupAsync<T>(
        string kind, string name, string path, Func<JsonElement, T?> parseDoc, CancellationToken ct)
        where T : class
    {
        if (_options.IsEnabled is { } gate && !gate())
            return EliteBgsResult<T>.Failure("EliteBGS lookups are disabled");
        if (string.IsNullOrWhiteSpace(name))
            return EliteBgsResult<T>.Ok(null);

        var trimmed = name.Trim();
        var key = kind + "|" + trimmed.ToLowerInvariant();
        var now = _options.Clock.GetUtcNow();
        if (_cache.TryGetValue(key, out var hit) && hit.ExpiresAt > now)
            return EliteBgsResult<T>.Ok(hit.Value as T, fromCache: true);

        if (CooldownRemaining(now) is { } wait)
            return EliteBgsResult<T>.Failure(
                $"rate limited; retry in {Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s", rateLimited: true);

        var url = $"{Base}{path}?name={Uri.EscapeDataString(trimmed)}";
        try
        {
            using var response = await GetWithRetryAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                StartCooldown(ReadRetryAfter(response));
                return EliteBgsResult<T>.Failure("HTTP 429", rateLimited: true);
            }
            if (!response.IsSuccessStatusCode)
                return EliteBgsResult<T>.Failure($"HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var value = FindDoc(doc.RootElement, trimmed) is { } el ? parseDoc(el) : null;

            Store(key, value);
            return EliteBgsResult<T>.Ok(value);
        }
        catch (JsonException ex) { return EliteBgsResult<T>.Failure("unparseable response: " + ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return EliteBgsResult<T>.Failure(ex.Message); }   // incl. an HttpClient timeout
    }

    private void Store(string key, object? value)
    {
        if (_options.CacheTtl <= TimeSpan.Zero) return;
        var now = _options.Clock.GetUtcNow();
        var max = Math.Max(1, _options.MaxCacheEntries);
        if (_cache.Count >= max)
        {
            foreach (var stale in _cache.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
                _cache.TryRemove(stale, out _);
            if (_cache.Count >= max)
            {
                var oldest = _cache.OrderBy(kv => kv.Value.StoredAt).Select(kv => kv.Key).FirstOrDefault();
                if (oldest is not null) _cache.TryRemove(oldest, out _);
            }
        }
        _cache[key] = new CacheEntry(value, now + _options.CacheTtl, now);
    }

    private TimeSpan? CooldownRemaining(DateTimeOffset now)
    {
        var until = Interlocked.Read(ref _cooldownUntilTicks);
        if (until == 0) return null;
        var remaining = new DateTimeOffset(until, TimeSpan.Zero) - now;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    private void StartCooldown(TimeSpan? retryAfter)
    {
        var wait = retryAfter is { } r && r > _options.RateLimitCooldown ? r : _options.RateLimitCooldown;
        Interlocked.Exchange(ref _cooldownUntilTicks, (_options.Clock.GetUtcNow() + wait).UtcTicks);
    }

    /// <summary>
    /// GET with a single retry when the server says it is busy (429/503): waits the server's
    /// <c>Retry-After</c> (or <see cref="EliteBgsClientOptions.TransientRetryDelay"/>) if that is within
    /// <see cref="EliteBgsClientOptions.MaxRetryAfter"/>, and otherwise hands the busy response back.
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

    // ---- parsing ----------------------------------------------------------------------------

    /// <summary>Pick the document whose name matches exactly; tolerate a bare array or single object.</summary>
    private static JsonElement? FindDoc(JsonElement root, string name)
    {
        IEnumerable<JsonElement> candidates = root.ValueKind switch
        {
            JsonValueKind.Object when root.TryGetProperty("docs", out var d) && d.ValueKind == JsonValueKind.Array => d.EnumerateArray(),
            JsonValueKind.Object => new[] { root },
            JsonValueKind.Array => root.EnumerateArray(),
            _ => Array.Empty<JsonElement>(),
        };
        foreach (var c in candidates)
            if (c.ValueKind == JsonValueKind.Object
                && string.Equals(ReadString(c, "name"), name, StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
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

        return new BgsFaction(name, ReadString(e, "allegiance"), ReadString(e, "government"), ReadDate(e, "updated_at"), presence);
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
        factions.Sort((a, b) => (b.Influence ?? -1).CompareTo(a.Influence ?? -1));

        return new BgsSystem(
            name,
            ReadString(e, "controlling_minor_faction") ?? ReadString(e, "controlling_faction"),
            ReadString(e, "allegiance"),
            ReadString(e, "government"),
            ReadString(e, "security"),
            ReadLong(e, "population"),
            ReadDate(e, "updated_at"),
            factions,
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
        return list;
    }

    private static IReadOnlyList<BgsConflict> ReadConflicts(JsonElement e)
    {
        var list = new List<BgsConflict>();
        foreach (var c in ReadArray(e, "conflicts"))
        {
            if (c.ValueKind != JsonValueKind.Object || ReadString(c, "type") is not { } type) continue;
            list.Add(new BgsConflict(type, ReadString(c, "status") ?? "", ReadString(c, "opponent_name"),
                ReadString(c, "stake"), ReadInt(c, "days_won")));
        }
        return list;
    }

    /// <summary>Influence is reported as a 0..1 fraction; a value above 1 is taken to be a percentage.</summary>
    private static double? ReadInfluence(JsonElement e)
    {
        var v = ReadDouble(e, "influence");
        if (v is null || double.IsNaN(v.Value) || v < 0) return null;
        return v > 1 ? v / 100 : v;
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

    private string Base => _options.BaseUrl.TrimEnd('/');

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
