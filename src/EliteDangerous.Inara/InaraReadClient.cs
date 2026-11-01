using System.Text;
using System.Text.Json;

namespace EliteDangerous.Inara;

/// <summary>Tuning for <see cref="InaraReadClient"/>: cache lifetimes and the local request limiter.</summary>
public sealed class InaraReadOptions
{
    /// <summary>Minimum gap between live requests. Inara allows about two per minute per API key.</summary>
    public TimeSpan MinRequestInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Cooldown after a rate-limit refusal that carried no Retry-After.</summary>
    public TimeSpan RateLimitCooldown { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ProfileTtl { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ServicesTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan EngineersTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan CommunityGoalsTtl { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Upper bound on cached entries; the oldest are evicted first.</summary>
    public int MaxCacheEntries { get; init; } = 256;
}

/// <summary>
/// Read-side (query) client for the Inara API. Reuses <see cref="InaraClientOptions"/> and
/// <see cref="InaraIdentity"/> (the same API key) and is opt-in: every call first checks the
/// <c>isEnabled</c> consent gate and a non-empty key. Results are cached per query (stale data is
/// served if a refresh fails), live requests are throttled locally and after a 429, and no method
/// ever throws — failures come back as an <see cref="InaraReadResult{T}"/> status.
/// </summary>
public sealed class InaraReadClient : IDisposable
{
    /// <summary>Inara event names used for the queries. Only the first and last are confirmed in Inara's public docs; the others are best-effort and isolated here so they are easy to correct.</summary>
    public static class EventNames
    {
        public const string CommanderProfile = "getCommanderProfile";
        public const string NearestServices = "getNearestServices";
        public const string Engineers = "getEngineers";
        public const string CommunityGoals = "getCommunityGoalsRecent";
    }

    private sealed record CacheEntry(object Value, DateTimeOffset Stored, DateTimeOffset Expires);

    private readonly InaraClientOptions _options;
    private readonly InaraReadOptions _read;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<bool> _isEnabled;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    /// <param name="isEnabled">The consent gate (e.g. the user's Inara opt-in). Null means always enabled.</param>
    public InaraReadClient(InaraClientOptions options, HttpClient? http = null, Func<bool>? isEnabled = null, InaraReadOptions? readOptions = null, TimeProvider? time = null)
    {
        _options = options;
        _read = readOptions ?? new InaraReadOptions();
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _isEnabled = isEnabled ?? (() => true);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Fetches a commander's profile; <paramref name="searchName"/> defaults to the identity's own commander.</summary>
    public Task<InaraReadResult<InaraCommanderProfile>> GetCommanderProfileAsync(InaraIdentity identity, string? searchName = null, CancellationToken ct = default)
        => QueryAsync(identity, EventNames.CommanderProfile, new Dictionary<string, object?> { ["searchName"] = searchName ?? identity.CommanderName },
            _read.ProfileTtl, d => d.ValueKind == JsonValueKind.Object ? InaraReadParsers.Profile(d) : null, ct);

    /// <summary>Finds services of <paramref name="kind"/> nearest to <paramref name="referenceSystem"/>.</summary>
    public Task<InaraReadResult<IReadOnlyList<InaraNearbyService>>> GetNearestServicesAsync(InaraIdentity identity, string referenceSystem, InaraServiceKind kind, CancellationToken ct = default)
        => QueryListAsync(identity, EventNames.NearestServices,
            new Dictionary<string, object?> { ["searchName"] = referenceSystem, ["serviceType"] = ServiceCode(kind) },
            _read.ServicesTtl, InaraReadParsers.Service, ct);

    /// <summary>Lists engineers with unlock requirements and blueprint material lists.</summary>
    public Task<InaraReadResult<IReadOnlyList<InaraEngineer>>> GetEngineersAsync(InaraIdentity identity, CancellationToken ct = default)
        => QueryListAsync(identity, EventNames.Engineers, null, _read.EngineersTtl, InaraReadParsers.Engineer, ct);

    /// <summary>Lists recent/active community goals.</summary>
    public Task<InaraReadResult<IReadOnlyList<InaraCommunityGoal>>> GetCommunityGoalsAsync(InaraIdentity identity, CancellationToken ct = default)
        => QueryListAsync(identity, EventNames.CommunityGoals, null, _read.CommunityGoalsTtl, InaraReadParsers.Goal, ct);

    private static string ServiceCode(InaraServiceKind k) => k switch
    {
        InaraServiceKind.Engineer => "engineer",
        InaraServiceKind.MaterialTrader => "materialtrader",
        InaraServiceKind.TechnologyBroker => "technologybroker",
        InaraServiceKind.InterstellarFactors => "interstellarfactors",
        InaraServiceKind.Shipyard => "shipyard",
        _ => "outfitting",
    };

    private async Task<InaraReadResult<IReadOnlyList<T>>> QueryListAsync<T>(InaraIdentity identity, string eventName, object? data, TimeSpan ttl, Func<JsonElement, T> parse, CancellationToken ct)
    {
        var r = await QueryAsync<IReadOnlyList<T>>(identity, eventName, data, ttl, d =>
        {
            var items = d.ValueKind switch
            {
                JsonValueKind.Array => d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object),
                JsonValueKind.Object => new[] { d },
                _ => Enumerable.Empty<JsonElement>(),
            };
            var list = new List<T>();
            foreach (var i in items)
            {
                try { list.Add(parse(i)); } catch (Exception) { /* skip a malformed row, keep the rest */ }
            }
            return list;
        }, ct).ConfigureAwait(false);
        return r;
    }

    private async Task<InaraReadResult<T>> QueryAsync<T>(InaraIdentity identity, string eventName, object? data, TimeSpan ttl, Func<JsonElement, T?> parse, CancellationToken ct) where T : class
    {
        try
        {
            if (!_isEnabled() || string.IsNullOrWhiteSpace(identity.ApiKey))
                return new(InaraReadStatus.Disabled, null, Message: "Inara is not enabled or has no API key");

            var now = _time.GetUtcNow();
            var key = eventName + "|" + identity.CommanderName + "|" + JsonSerializer.Serialize(data);
            CacheEntry? cached;
            lock (_gate)
            {
                _cache.TryGetValue(key, out cached);
                if (cached is not null && cached.Expires > now && cached.Value is T fresh)
                    return new(InaraReadStatus.Ok, fresh, FromCache: true);
                if (now < _nextAllowed)
                    return Fallback<T>(cached, InaraReadStatus.RateLimited, "rate limited locally", _nextAllowed - now);
                _nextAllowed = now + _read.MinRequestInterval; // reserve the slot before the await
            }

            var body = BuildBody(identity, eventName, data, now);
            string text;
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(_options.Endpoint, content, ct).ConfigureAwait(false);
                text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                if (code == 429)
                {
                    var wait = resp.Headers.RetryAfter is { } ra ? ra.Delta ?? (ra.Date - now) : null;
                    return Limited(cached, wait);
                }
                if (!resp.IsSuccessStatusCode)
                    return Fallback<T>(cached, code is 401 or 403 ? InaraReadStatus.Rejected : InaraReadStatus.Error, $"HTTP {code}", null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fallback<T>(cached, InaraReadStatus.Error, ex.Message, null);
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var headerStatus = Status(root.TryGetProperty("header", out var h) ? h : default, out var headerText);
            if (headerStatus is not (>= 200 and < 300))
            {
                if (headerText is not null && System.Text.RegularExpressions.Regex.IsMatch(headerText, @"too many|rate[\s-]?limit|limit.{0,20}exceed|exceed.{0,20}limit", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return Limited(cached, null);
                return Fallback<T>(cached, headerStatus is 400 or 401 or 403 ? InaraReadStatus.Rejected : InaraReadStatus.Error, headerText ?? $"status {headerStatus}", null);
            }

            if (!root.TryGetProperty("events", out var evs) || evs.ValueKind != JsonValueKind.Array || evs.GetArrayLength() == 0 || evs[0].ValueKind != JsonValueKind.Object)
                return Fallback<T>(cached, InaraReadStatus.Error, "no event in reply", null);
            var ev = evs[0];
            var evStatus = Status(ev, out var evText);
            if (evStatus == 204)
                return new(InaraReadStatus.NotFound, null, Message: evText);
            if (evStatus is not (>= 200 and < 300))
                return Fallback<T>(cached, evStatus is 400 or 401 or 403 ? InaraReadStatus.Rejected : InaraReadStatus.Error, evText ?? $"event status {evStatus}", null);

            T? value = ev.TryGetProperty("eventData", out var ed) ? parse(ed) : null;
            if (value is null)
                return new(InaraReadStatus.NotFound, null, Message: "no data");

            lock (_gate)
            {
                _cache[key] = new CacheEntry(value, now, now + ttl);
                while (_cache.Count > Math.Max(1, _read.MaxCacheEntries))
                    _cache.Remove(_cache.MinBy(kv => kv.Value.Stored).Key);
            }
            return new(InaraReadStatus.Ok, value);
        }
        catch (OperationCanceledException)
        {
            return new(InaraReadStatus.Error, null, Message: "cancelled");
        }
        catch (Exception ex)
        {
            return new(InaraReadStatus.Error, null, Message: ex.Message);
        }

        InaraReadResult<T> Limited(CacheEntry? c, TimeSpan? wait)
        {
            var w = wait > TimeSpan.Zero ? wait.Value : _read.RateLimitCooldown;
            lock (_gate) _nextAllowed = _time.GetUtcNow() + w;
            return Fallback<T>(c, InaraReadStatus.RateLimited, "rate limited by Inara", w);
        }
    }

    private static InaraReadResult<T> Fallback<T>(CacheEntry? cached, InaraReadStatus status, string message, TimeSpan? retryAfter) where T : class
        => cached?.Value is T stale
            ? new(InaraReadStatus.Ok, stale, FromCache: true, IsStale: true, Message: message, RetryAfter: retryAfter)
            : new(status, null, Message: message, RetryAfter: retryAfter);

    private static int Status(JsonElement e, out string? text)
    {
        text = InaraJson.Str(e, "eventStatusText");
        return (int)(InaraJson.Lng(e, "eventStatus") ?? 0);
    }

    private string BuildBody(InaraIdentity identity, string eventName, object? data, DateTimeOffset now)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["header"] = new Dictionary<string, object?>
            {
                ["appName"] = _options.AppName,
                ["appVersion"] = _options.AppVersion,
                ["isBeingDeveloped"] = _options.IsBeingDeveloped,
                ["APIkey"] = identity.ApiKey,
                ["commanderName"] = identity.CommanderName,
                ["commanderFrontierID"] = identity.CommanderFrontierID,
            },
            ["events"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["eventName"] = eventName,
                    ["eventTimestamp"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["eventData"] = data ?? new Dictionary<string, object?>(),
                },
            },
        });

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
