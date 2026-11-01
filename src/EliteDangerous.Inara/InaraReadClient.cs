using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EliteDangerous.Inara;

/// <summary>Tuning for <see cref="InaraReadClient"/>: cache lifetimes, the local limiter and safety bounds.</summary>
public sealed class InaraReadOptions
{
    /// <summary>Minimum gap between live requests per API key. Defaults to the shared <see cref="InaraRateLimits.MinRequestInterval"/>.</summary>
    public TimeSpan MinRequestInterval { get; init; } = InaraRateLimits.MinRequestInterval;
    /// <summary>Cooldown after a rate-limit refusal that carried no Retry-After.</summary>
    public TimeSpan RateLimitCooldown { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Upper bound on any cooldown, including a server-supplied Retry-After.</summary>
    public TimeSpan MaxCooldown { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan ProfileTtl { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ServicesTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan EngineersTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan CommunityGoalsTtl { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>
    /// Absolute maximum age at which a cached entry may still be served when a refresh fails. When null,
    /// it is <see cref="MaxStaleTtlMultiple"/> times the entry's TTL. Past it the failure is returned instead.
    /// </summary>
    public TimeSpan? MaxStale { get; init; }
    /// <summary>Stale tolerance as a multiple of the entry's TTL, used when <see cref="MaxStale"/> is null.</summary>
    public int MaxStaleTtlMultiple { get; init; } = 4;
    /// <summary>Upper bound on cached entries; the oldest are evicted first.</summary>
    public int MaxCacheEntries { get; init; } = 256;
    /// <summary>Largest response body accepted, in bytes.</summary>
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
    /// <summary>Per-request timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Read-side (query) client for the Inara API. Reuses <see cref="InaraClientOptions"/> and
/// <see cref="InaraIdentity"/> (the same API key) and is opt-in: every call first checks the required
/// <c>isEnabled</c> consent gate and a non-empty key; with the gate off no request is made and no cached
/// data is served (the cache is cleared). Results are cached per query, stale data is served (up to a
/// maximum age) if a refresh fails or parses badly, identical concurrent queries share one live request,
/// and live requests are throttled per API key locally and after a 429. No method ever throws; failures
/// come back as an <see cref="InaraReadResult{T}"/> status.
/// </summary>
/// <remarks>
/// <b>Unverified guesses.</b> Only a subset of the event names is confirmed in Inara's public docs.
/// The response field names, the <c>serviceType</c> codes and the system <c>searchName</c> request field
/// used here are best-effort guesses, isolated in this file and <c>InaraReadModels.cs</c> so they are easy
/// to correct. A reply that does not match is rejected as an error (never reported as empty or zeroed
/// data, and never cached). Validate against a real reply before building features on these results.
/// The supplied <see cref="HttpClient"/>, if any, should not follow redirects, since the request body
/// carries the API key; the client it creates itself does not.
/// </remarks>
public sealed class InaraReadClient : IDisposable
{
    /// <summary>Inara event names used for the queries. Only some are confirmed in Inara's public docs; the response field names are unverified too. Isolated here so they are easy to correct.</summary>
    public static class EventNames
    {
        public const string CommanderProfile = "getCommanderProfile";
        public const string NearestServices = "getNearestServices";
        public const string Engineers = "getEngineers";
        public const string CommunityGoals = "getCommunityGoalsRecent";
    }

    private const int MaxNameLength = 128;
    private const int MaxMessageLength = 200;
    private const int MaxSlots = 64;

    private sealed record CacheEntry(object Value, TimeSpan Stored, DateTimeOffset StoredWall, TimeSpan Expires, TimeSpan MaxAge);

    private readonly InaraClientOptions _options;
    private readonly InaraReadOptions _read;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<bool> _isEnabled;
    private readonly TimeProvider _time;
    private readonly long _epoch;
    private readonly CancellationTokenSource _disposed = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly Dictionary<string, Task> _inflight = new();
    private readonly Dictionary<string, TimeSpan> _nextAllowed = new(); // per hashed API key, monotonic

    /// <param name="options">Application identity and endpoint. The endpoint must be https (http only for a loopback host).</param>
    /// <param name="isEnabled">The consent gate (e.g. the user's Inara opt-in). Required; there is deliberately no default so it cannot fail open.</param>
    /// <exception cref="ArgumentException">The endpoint is not an absolute https (or loopback http) URI.</exception>
    public InaraReadClient(InaraClientOptions options, Func<bool> isEnabled, HttpClient? http = null, InaraReadOptions? readOptions = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isEnabled);
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new ArgumentException("Inara endpoint must be an absolute https URL (http only for loopback).", nameof(options));

        _options = options;
        _read = readOptions ?? new InaraReadOptions();
        _isEnabled = isEnabled;
        _time = time ?? TimeProvider.System;
        _epoch = _time.GetTimestamp();
        _ownsHttp = http is null;
        // The body carries the API key, so the client we own never follows a redirect to somewhere else.
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Fetches a commander's profile; <paramref name="searchName"/> defaults to the identity's own commander.</summary>
    /// <remarks>Unverified: the response field names are best-effort guesses (see <see cref="InaraReadClient"/>). A blank name is rejected locally.</remarks>
    public Task<InaraReadResult<InaraCommanderProfile>> GetCommanderProfileAsync(InaraIdentity identity, string? searchName = null, CancellationToken ct = default)
    {
        var name = Clean(searchName ?? identity.CommanderName);
        if (name is null) return Task.FromResult(Invalid<InaraCommanderProfile>("search name is blank or too long"));
        return QueryAsync(identity, EventNames.CommanderProfile, new SortedDictionary<string, string> { ["searchName"] = name },
            _read.ProfileTtl, InaraReadParsers.Profile, ct);
    }

    /// <summary>Finds services of <paramref name="kind"/> nearest to <paramref name="referenceSystem"/>.</summary>
    /// <remarks>Unverified: the response field names, the <c>serviceType</c> codes and the <c>searchName</c> request field are best-effort guesses (see <see cref="InaraReadClient"/>). A blank system is rejected locally.</remarks>
    public Task<InaraReadResult<IReadOnlyList<InaraNearbyService>>> GetNearestServicesAsync(InaraIdentity identity, string referenceSystem, InaraServiceKind kind, CancellationToken ct = default)
    {
        var system = Clean(referenceSystem);
        if (system is null) return Task.FromResult(Invalid<IReadOnlyList<InaraNearbyService>>("reference system is blank or too long"));
        if (ServiceCode(kind) is not { } code) return Task.FromResult(Invalid<IReadOnlyList<InaraNearbyService>>("unsupported service kind"));
        return QueryListAsync(identity, EventNames.NearestServices,
            new SortedDictionary<string, string> { ["searchName"] = system, ["serviceType"] = code },
            _read.ServicesTtl, InaraReadParsers.Service, ct);
    }

    /// <summary>Lists engineers with unlock requirements and blueprint material lists.</summary>
    /// <remarks>Unverified: the event shape and response field names are best-effort guesses (see <see cref="InaraReadClient"/>); an unrecognised reply is an error, not an empty list.</remarks>
    public Task<InaraReadResult<IReadOnlyList<InaraEngineer>>> GetEngineersAsync(InaraIdentity identity, CancellationToken ct = default)
        => QueryListAsync(identity, EventNames.Engineers, null, _read.EngineersTtl, InaraReadParsers.Engineer, ct);

    /// <summary>Lists recent/active community goals.</summary>
    /// <remarks>Unverified: the response field names are best-effort guesses (see <see cref="InaraReadClient"/>). Cached data older than <see cref="InaraReadOptions.MaxStale"/> is never served.</remarks>
    public Task<InaraReadResult<IReadOnlyList<InaraCommunityGoal>>> GetCommunityGoalsAsync(InaraIdentity identity, CancellationToken ct = default)
        => QueryListAsync(identity, EventNames.CommunityGoals, null, _read.CommunityGoalsTtl, InaraReadParsers.Goal, ct);

    private static string? ServiceCode(InaraServiceKind k) => k switch
    {
        InaraServiceKind.Engineer => "engineer",
        InaraServiceKind.MaterialTrader => "materialtrader",
        InaraServiceKind.TechnologyBroker => "technologybroker",
        InaraServiceKind.InterstellarFactors => "interstellarfactors",
        InaraServiceKind.Shipyard => "shipyard",
        InaraServiceKind.Outfitting => "outfitting",
        _ => null,
    };

    private static string? Clean(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) || t.Length > MaxNameLength ? null : t;
    }

    private static InaraReadResult<T> Invalid<T>(string message) => new(InaraReadStatus.Error, default, Message: message);

    private Task<InaraReadResult<IReadOnlyList<T>>> QueryListAsync<T>(InaraIdentity identity, string eventName, SortedDictionary<string, string>? data, TimeSpan ttl, Func<JsonElement, T?> row, CancellationToken ct) where T : class
        => QueryAsync<IReadOnlyList<T>>(identity, eventName, data, ttl, d =>
        {
            IEnumerable<JsonElement> items;
            switch (d.ValueKind)
            {
                case JsonValueKind.Array: items = d.EnumerateArray(); break;
                case JsonValueKind.Object: items = new[] { d }; break;
                case JsonValueKind.Null or JsonValueKind.Undefined: return null; // no data
                default: throw new FormatException("unexpected eventData type");
            }

            var list = new List<T>();
            var total = 0;
            foreach (var i in items)
            {
                total++;
                if (i.ValueKind != JsonValueKind.Object) continue;
                try { if (row(i) is { } r) list.Add(r); } catch (Exception) { /* skip a malformed row, keep the rest */ }
            }
            // Rows were present but none were recognised: the shape is not what we guessed. Fail, don't cache.
            if (total > 0 && list.Count == 0) throw new FormatException($"none of {total} rows were recognised");
            return list;
        }, ct);

    private TimeSpan Mono() => _time.GetElapsedTime(_epoch);

    private static string KeyHash(string apiKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    private async Task<InaraReadResult<T>> QueryAsync<T>(InaraIdentity identity, string eventName, SortedDictionary<string, string>? data, TimeSpan ttl, Func<JsonElement, T?> parse, CancellationToken ct) where T : class
    {
        try
        {
            if (!_isEnabled() || string.IsNullOrWhiteSpace(identity.ApiKey))
            {
                lock (_gate) _cache.Clear(); // consent withdrawn: hold no data from the user's Inara account
                return new(InaraReadStatus.Disabled, null, Message: "Inara is not enabled or has no API key");
            }
            if (Clean(identity.CommanderName) is not { } commander)
                return Invalid<T>("commander name is blank or too long");

            var slot = KeyHash(identity.ApiKey);
            // Case/whitespace-normalised; includes the hashed key so a rotated key never shares entries. Never logged.
            var key = string.Join('|', eventName, slot, commander.ToLowerInvariant(),
                data is null ? "" : string.Join('&', data.Select(kv => kv.Key + "=" + kv.Value.ToLowerInvariant())));

            Task<InaraReadResult<T>> task;
            TaskCompletionSource<InaraReadResult<T>>? lead = null;
            CacheEntry? cached;
            lock (_gate)
            {
                var now = Mono();
                _cache.TryGetValue(key, out cached);
                if (cached is not null && now - cached.Stored > cached.MaxAge)
                {
                    _cache.Remove(key);
                    cached = null;
                }
                if (cached is not null && cached.Expires > now && cached.Value is T fresh)
                    return Hit(fresh, cached, now, stale: false, message: null, retryAfter: null);

                if (_inflight.TryGetValue(key, out var existing))
                {
                    task = (Task<InaraReadResult<T>>)existing; // coalesce onto the live request
                }
                else
                {
                    if (_nextAllowed.TryGetValue(slot, out var next) && now < next)
                        return Fallback<T>(cached, InaraReadStatus.RateLimited, "rate limited locally", next - now);
                    // Reserve the slot before the await. It is deliberately not released on failure or
                    // cancellation: a request may have reached Inara, so staying conservative is safer.
                    _nextAllowed[slot] = now + _read.MinRequestInterval;
                    PruneSlots(now);
                    lead = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    task = lead.Task;
                    _inflight[key] = task;
                }
            }

            if (lead is not null)
            {
                // The fetch is not tied to this caller's token, so one caller cancelling can't fail the others.
                var wall = _time.GetUtcNow();
                _ = RunAsync(lead, key, () => FetchAsync(identity, slot, key, eventName, data, ttl, parse, cached, wall));
            }
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(InaraReadStatus.Cancelled, null, Message: "cancelled");
        }
        catch (Exception ex)
        {
            return new(InaraReadStatus.Error, null, Message: Sanitise(ex.Message));
        }
    }

    private async Task RunAsync<T>(TaskCompletionSource<InaraReadResult<T>> tcs, string key, Func<Task<InaraReadResult<T>>> fetch)
    {
        InaraReadResult<T> result;
        try { result = await fetch().ConfigureAwait(false); }
        catch (Exception ex) { result = new(InaraReadStatus.Error, default, Message: Sanitise(ex.Message)); }
        lock (_gate) _inflight.Remove(key);
        tcs.TrySetResult(result);
    }

    private async Task<InaraReadResult<T>> FetchAsync<T>(InaraIdentity identity, string slot, string key, string eventName, SortedDictionary<string, string>? data,
        TimeSpan ttl, Func<JsonElement, T?> parse, CacheEntry? cached, DateTimeOffset wall) where T : class
    {
        string text;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
            cts.CancelAfter(_read.RequestTimeout);
            using var content = new StringContent(BuildBody(identity, eventName, data, wall), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_options.Endpoint, content, cts.Token).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var retryAfter = RetryAfterOf(resp, wall);
            if (code == 429)
                return Limited<T>(cached, slot, retryAfter);
            if (!resp.IsSuccessStatusCode)
            {
                if (retryAfter is { } ra) ExtendCooldown(slot, Clamp(ra)); // 5xx Retry-After is honoured too
                var status = code is >= 400 and < 500 and not 408 ? InaraReadStatus.Rejected : InaraReadStatus.Error;
                return Fallback<T>(cached, status, $"HTTP {code}", retryAfter is null ? null : Clamp(retryAfter.Value));
            }
            if (await ReadBoundedAsync(resp, cts.Token).ConfigureAwait(false) is not { } body)
                return Fallback<T>(cached, InaraReadStatus.Error, "response too large", null);
            text = body;
        }
        catch (OperationCanceledException)
        {
            return _disposed.IsCancellationRequested
                ? new(InaraReadStatus.Cancelled, null, Message: "client disposed")
                : Fallback<T>(cached, InaraReadStatus.Error, "request timed out", null);
        }
        catch (Exception ex)
        {
            return Fallback<T>(cached, InaraReadStatus.Error, Sanitise(ex.Message) ?? "request failed", null);
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fallback<T>(cached, InaraReadStatus.Error, "unparseable response: not a JSON object", null);

            var headerStatus = Status(root.TryGetProperty("header", out var h) ? h : default, out var headerText);
            if (headerStatus is not (>= 200 and < 300))
            {
                if (InaraRateLimits.IsRateLimitWording(headerText))
                    return Limited<T>(cached, slot, null);
                return Fallback<T>(cached, headerStatus is 400 or 401 or 403 ? InaraReadStatus.Rejected : InaraReadStatus.Error, headerText ?? $"status {headerStatus}", null);
            }

            if (!root.TryGetProperty("events", out var evs) || evs.ValueKind != JsonValueKind.Array || evs.GetArrayLength() == 0 || evs[0].ValueKind != JsonValueKind.Object)
                return Fallback<T>(cached, InaraReadStatus.Error, "no event in reply", null);
            var ev = evs[0];
            var evStatus = Status(ev, out var evText);
            if (evStatus == 204)
                return NotFound<T>(key, evText);
            if (evStatus is not (>= 200 and < 300))
            {
                if (InaraRateLimits.IsRateLimitWording(evText))
                    return Limited<T>(cached, slot, null);
                return Fallback<T>(cached, evStatus is 400 or 401 or 403 ? InaraReadStatus.Rejected : InaraReadStatus.Error, evText ?? $"event status {evStatus}", null);
            }

            var value = ev.TryGetProperty("eventData", out var ed) ? parse(ed) : null;
            if (value is null)
                return NotFound<T>(key, "no data");

            var stored = Mono();
            lock (_gate)
            {
                // Don't let a slow older response overwrite a newer entry.
                if (!_cache.TryGetValue(key, out var prior) || prior.Stored <= stored)
                    _cache[key] = new CacheEntry(value, stored, wall, stored + ttl, _read.MaxStale ?? ttl * Math.Max(1, _read.MaxStaleTtlMultiple));
                while (_cache.Count > Math.Max(1, _read.MaxCacheEntries))
                    _cache.Remove(_cache.MinBy(kv => kv.Value.Stored).Key);
            }
            return new(InaraReadStatus.Ok, value, Age: TimeSpan.Zero, FetchedAt: wall);
        }
        catch (Exception ex)
        {
            // Unparseable body or an unrecognised shape: serve the stale value if there is one; never cache this.
            return Fallback<T>(cached, InaraReadStatus.Error, "unusable response: " + Sanitise(ex.Message), null);
        }
    }

    private InaraReadResult<T> NotFound<T>(string key, string? message)
    {
        lock (_gate) _cache.Remove(key); // the data is gone upstream; don't let a fallback resurrect it
        return new(InaraReadStatus.NotFound, default, Message: Sanitise(message));
    }

    private InaraReadResult<T> Limited<T>(CacheEntry? cached, string slot, TimeSpan? wait) where T : class
    {
        var w = Clamp(wait > TimeSpan.Zero ? wait.Value : _read.RateLimitCooldown);
        ExtendCooldown(slot, w);
        return Fallback<T>(cached, InaraReadStatus.RateLimited, "rate limited by Inara", w);
    }

    private TimeSpan Clamp(TimeSpan w) => w < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : w > _read.MaxCooldown ? _read.MaxCooldown : w;

    private void ExtendCooldown(string slot, TimeSpan w)
    {
        lock (_gate)
        {
            var until = Mono() + w;
            if (!_nextAllowed.TryGetValue(slot, out var cur) || until > cur) _nextAllowed[slot] = until;
        }
    }

    private void PruneSlots(TimeSpan now)
    {
        if (_nextAllowed.Count <= MaxSlots) return;
        foreach (var k in _nextAllowed.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _nextAllowed.Remove(k);
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage resp, DateTimeOffset wall)
        => resp.Headers.RetryAfter is { } ra && (ra.Delta ?? (ra.Date - wall)) is { } d && d > TimeSpan.Zero ? d : null;

    private async Task<string?> ReadBoundedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var max = Math.Max(1024, _read.MaxResponseBytes);
        if (resp.Content.Headers.ContentLength > max) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > max) return null;
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private InaraReadResult<T> Hit<T>(T value, CacheEntry e, TimeSpan now, bool stale, string? message, TimeSpan? retryAfter) where T : class
        => new(InaraReadStatus.Ok, value, FromCache: true, IsStale: stale, Message: message, RetryAfter: retryAfter, Age: now - e.Stored, FetchedAt: e.StoredWall);

    /// <summary>Serves the cached value as stale when it is young enough, otherwise reports the failure.</summary>
    private InaraReadResult<T> Fallback<T>(CacheEntry? cached, InaraReadStatus status, string message, TimeSpan? retryAfter) where T : class
    {
        var now = Mono();
        return cached is not null && cached.Value is T stale && now - cached.Stored <= cached.MaxAge
            ? Hit(stale, cached, now, stale: true, message: message, retryAfter: retryAfter)
            : new(status, null, Message: message, RetryAfter: retryAfter);
    }

    private static string? Sanitise(string? s)
    {
        if (s is null) return null;
        var sb = new StringBuilder(Math.Min(s.Length, MaxMessageLength));
        foreach (var c in s)
        {
            if (sb.Length >= MaxMessageLength) break;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }

    private static int Status(JsonElement e, out string? text)
    {
        text = Sanitise(InaraJson.Str(e, "eventStatusText"));
        return InaraJson.Int(e, "eventStatus") ?? 0;
    }

    private string BuildBody(InaraIdentity identity, string eventName, SortedDictionary<string, string>? data, DateTimeOffset now)
    {
        var header = new Dictionary<string, object?>
        {
            ["appName"] = _options.AppName,
            ["appVersion"] = _options.AppVersion,
            ["isBeingDeveloped"] = _options.IsBeingDeveloped,
            ["APIkey"] = identity.ApiKey,
            ["commanderName"] = identity.CommanderName,
        };
        if (identity.CommanderFrontierID is not null) header["commanderFrontierID"] = identity.CommanderFrontierID; // omit rather than send an explicit null

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["header"] = header,
            ["events"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["eventName"] = eventName,
                    ["eventTimestamp"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["eventData"] = data ?? new SortedDictionary<string, string>(),
                },
            },
        });
    }

    /// <summary>Cancels any in-flight request (those callers get <see cref="InaraReadStatus.Cancelled"/>) and releases the owned HTTP client.</summary>
    public void Dispose()
    {
        try { _disposed.Cancel(); } catch (ObjectDisposedException) { }
        if (_ownsHttp) _http.Dispose();
    }
}
