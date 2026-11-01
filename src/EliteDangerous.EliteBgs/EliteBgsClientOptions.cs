namespace EliteDangerous.EliteBgs;

/// <summary>
/// Static configuration for an <see cref="EliteBgsClient"/> — the sending-application identity used
/// for the <c>User-Agent</c>, the API base address, caching and rate-limit behaviour.
/// </summary>
public sealed class EliteBgsClientOptions
{
    /// <summary>A unique, stable name for the calling application (e.g. "EDNexus"), sent as the User-Agent product.</summary>
    public required string SoftwareName { get; init; }

    /// <summary>The calling application's version, sent as the User-Agent product version.</summary>
    public required string SoftwareVersion { get; init; }

    /// <summary>The EliteBGS base address (no trailing slash needed). Overridable for tests.</summary>
    /// <remarks>Must be https unless it points at a loopback host; the client constructor throws otherwise.</remarks>
    public string BaseUrl { get; init; } = "https://elitebgs.app";

    /// <summary>
    /// Consent gate, evaluated before every call. When it returns false the client makes no request
    /// and returns a failure result. Null means always allowed; the host wires this to its opt-in setting.
    /// </summary>
    public Func<bool>? IsEnabled { get; init; }

    /// <summary>How long a successful lookup is served from memory. BGS data moves on the tick, so this is modest.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The most entries held in the in-memory cache before the oldest are evicted.</summary>
    public int MaxCacheEntries { get; init; } = 256;

    /// <summary>
    /// The longest <c>Retry-After</c> the client will wait out before retrying a 429/503 once. A longer
    /// request is not waited for: the client enters a cool-down instead.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait before the single retry of a 429/503 that carried no <c>Retry-After</c>.</summary>
    public TimeSpan TransientRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// After a rate-limit rejection that was not retried, no further requests are sent for this long
    /// (or the server's <c>Retry-After</c>, if longer) so a busy API is not hammered.
    /// </summary>
    public TimeSpan RateLimitCooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound on any cool-down, however large a (third-party controlled) <c>Retry-After</c> is.</summary>
    public TimeSpan MaxCooldown { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The largest response body the client will read; bigger replies fail with "response too large".</summary>
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>The longest (trimmed) faction/system name that will be sent; longer names fail without a request.</summary>
    public int MaxNameLength { get; init; } = 100;

    /// <summary>Clock used for cache expiry and cool-down (monotonic timestamps), Retry-After dates and retry delays; overridable for tests.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}
