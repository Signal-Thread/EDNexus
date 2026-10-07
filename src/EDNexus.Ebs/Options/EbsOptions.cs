namespace EDNexus.Ebs.Options;

/// <summary>General service configuration for the Extension Backend Service itself.</summary>
public sealed class EbsOptions
{
    /// <summary>Configuration section name this type binds to.</summary>
    public const string SectionName = "Ebs";

    /// <summary>
    /// The HTTP port Kestrel listens on when no <c>ASPNETCORE_URLS</c>/<c>Kestrel</c> configuration
    /// is supplied. Defaults to 8787.
    /// </summary>
    public int Port { get; set; } = 8787;

    /// <summary>
    /// The maximum size, in bytes, of a serialized state payload the EBS will accept and forward to
    /// Twitch PubSub. Twitch enforces a hard 5 KiB limit on the PubSub message; we default to a
    /// slightly smaller value to leave headroom for the JSON envelope Twitch adds.
    /// </summary>
    public int MaxStatePayloadBytes { get; set; } = 5000;

    /// <summary>Twitch's hard ceiling on a PubSub message; <see cref="MaxStatePayloadBytes"/> may not exceed it.</summary>
    public const int TwitchPubSubMaxMessageBytes = 5120;

    /// <summary>
    /// Largest request body, in bytes, that <c>/api/update-state</c> and the <c>/oauth/*</c> endpoints
    /// read. A state update is at most <see cref="MaxStatePayloadBytes"/> plus a thin envelope, so
    /// anything near this is not a real client — it is refused (<c>413</c>) before it is buffered.
    /// </summary>
    public int MaxRequestBodyBytes { get; set; } = 16 * 1024;

    /// <summary>Maximum number of state updates accepted per broadcaster channel per window.</summary>
    public int UpdateStateRateLimit { get; set; } = 1;

    /// <summary>The rate limit window, in seconds, applied to <see cref="UpdateStateRateLimit"/>.</summary>
    public int UpdateStateRateLimitWindowSeconds { get; set; } = 2;

    /// <summary>
    /// Per-IP limit on the <c>/oauth/*</c> endpoints (all four share one budget). A normal login is
    /// three requests, so the default 30 per window is generous for a person and cheap for an attacker to exhaust.
    /// </summary>
    public int OAuthRateLimit { get; set; } = 30;

    /// <summary>The window, in seconds, of <see cref="OAuthRateLimit"/>.</summary>
    public int OAuthRateLimitWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Per-client-IP limit on <c>GET /api/initial-state</c>, which every viewer's browser calls when
    /// the extension loads. Keyed on the forwarded client address (see <see cref="TrustedProxyNetworks"/>),
    /// so it is per viewer, not per proxy.
    /// </summary>
    public int InitialStateRateLimit { get; set; } = 60;

    /// <summary>The window, in seconds, of <see cref="InitialStateRateLimit"/>.</summary>
    public int InitialStateRateLimitWindowSeconds { get; set; } = 10;

    /// <summary>
    /// <c>max-age</c> (seconds) of the public <c>Cache-Control</c> on a <c>200</c> from
    /// <c>GET /api/initial-state</c>, so a CDN/proxy/browser can absorb a burst of viewers. Live updates
    /// travel by PubSub, so a few seconds of staleness is invisible; it also bounds how long a card
    /// the broadcaster just switched off can still be served from a cache. Zero disables caching.
    /// </summary>
    public int InitialStateCacheSeconds { get; set; } = 5;

    /// <summary>
    /// Exact IP addresses of reverse proxies whose <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c>
    /// headers are trusted. When this and <see cref="TrustedProxyNetworks"/> are both empty the
    /// defaults apply: loopback and the private ranges (10/8, 172.16/12, 192.168/16, fc00::/7), which
    /// covers a proxy container on the same Docker network. Only one hop is trusted.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>
    /// CIDR ranges (e.g. <c>172.18.0.0/16</c>) of reverse proxies whose forwarded headers are trusted.
    /// See <see cref="TrustedProxies"/> for the default when both are empty.
    /// </summary>
    public string[] TrustedProxyNetworks { get; set; } = [];

    /// <summary>The ranges trusted as a proxy when neither <see cref="TrustedProxies"/> nor <see cref="TrustedProxyNetworks"/> is configured.</summary>
    public static readonly string[] DefaultTrustedProxyNetworks =
        ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];

    /// <summary>How often, in minutes, expired channel snapshots are deleted from the database.</summary>
    public int ChannelStatePruneIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// How long a pending OAuth session (between the desktop hitting <c>/oauth/authorize</c> and
    /// Twitch redirecting back to <c>/oauth/callback</c>) stays valid. Bounds how long the
    /// commander has to complete the Twitch consent page.
    /// </summary>
    public int OAuthSessionTtlMinutes { get; set; } = 10;

    /// <summary>
    /// How long the one-time authorization code minted by <c>/oauth/callback</c> stays valid before
    /// the desktop must exchange it (with the PKCE verifier) at <c>/oauth/token</c>.
    /// </summary>
    public int OAuthCodeTtlSeconds { get; set; } = 60;

    /// <summary>
    /// How often the background Twitch token refresh loop wakes up to check every broadcaster's
    /// underlying Twitch grant.
    /// </summary>
    public int TwitchTokenRefreshIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// A broadcaster's underlying Twitch access token is refreshed once it has less than this long
    /// left before it expires, so the background loop stays ahead of expiry between its own polls.
    /// </summary>
    public int TwitchTokenRefreshBufferMinutes { get; set; } = 60;

    /// <summary>
    /// Extra exact origins (beyond any <c>https://*.ext-twitch.tv</c> host, which is always allowed)
    /// permitted to call <c>GET /api/initial-state/{channelId}</c> from a browser — e.g. the Twitch
    /// Developer Rig (typically <c>https://localhost:8080</c>) during local extension development.
    /// </summary>
    public string[] AdditionalAllowedFrontendOrigins { get; set; } = [];

    /// <summary>
    /// Where broadcaster tokens and channel state live. <see cref="EbsStorageProvider.Sqlite"/> (the
    /// default) survives crashes and restarts; <see cref="EbsStorageProvider.InMemory"/> is for
    /// tests and throwaway local runs only.
    /// </summary>
    public EbsStorageProvider StorageProvider { get; set; } = EbsStorageProvider.Sqlite;

    /// <summary>
    /// Oldest channel snapshot <c>GET /api/initial-state</c> will serve, in hours. Snapshots are
    /// durable, so a clear the desktop never managed to send would otherwise keep a card public
    /// forever; past this age it is treated as gone and pruned. The app republishes on every change
    /// and refreshes an unchanged card every few hours, so only a card whose app has stopped
    /// publishing is affected. Zero or less disables the limit.
    /// </summary>
    public int ChannelStateMaxAgeHours { get; set; } = 24;

    /// <summary>
    /// Smallest non-zero <see cref="ChannelStateMaxAgeHours"/> the service starts with: two of the
    /// desktop app's refresh periods (<c>TwitchStreamCardService.DefaultRefreshInterval</c>, 6 hours),
    /// so one missed refresh does not take a live card down. The app talks to EBS instances it
    /// cannot read the configuration of, so the constraint is enforced here, not there.
    /// </summary>
    public const int MinChannelStateMaxAgeHours = 12;

    /// <summary><see cref="ChannelStateMaxAgeHours"/> as a span, or null when the limit is disabled.</summary>
    public TimeSpan? ChannelStateMaxAge =>
        ChannelStateMaxAgeHours > 0 ? TimeSpan.FromHours(ChannelStateMaxAgeHours) : null;

    /// <summary>
    /// Directory holding the SQLite database (<c>ebs.db</c>). Relative paths resolve against the
    /// content root. Must be on a persistent volume in a container deployment.
    /// </summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Directory holding the ASP.NET Core Data Protection key ring that encrypts Twitch tokens at
    /// rest. Defaults to <c>{DataDirectory}/keys</c>; point it at a separate volume or secret mount so
    /// a copy of the database alone can't be decrypted. Losing these keys invalidates every stored
    /// Twitch grant (broadcasters must log in again).
    /// </summary>
    public string? DataProtectionKeysDirectory { get; set; }

    /// <summary>The default key ring location, <c>{DataDirectory}/keys</c>, whether or not it is in use.</summary>
    public string ResolveDefaultDataProtectionKeysDirectory(string contentRootPath) =>
        Path.Combine(ResolveDataDirectory(contentRootPath), "keys");

    /// <summary>Absolute path of <see cref="DataDirectory"/>, resolved against <paramref name="contentRootPath"/>.</summary>
    public string ResolveDataDirectory(string contentRootPath) => Path.GetFullPath(DataDirectory, contentRootPath);

    /// <summary>Absolute path of the Data Protection key ring directory, resolved against <paramref name="contentRootPath"/>.</summary>
    public string ResolveDataProtectionKeysDirectory(string contentRootPath) =>
        string.IsNullOrWhiteSpace(DataProtectionKeysDirectory)
            ? ResolveDefaultDataProtectionKeysDirectory(contentRootPath)
            : Path.GetFullPath(DataProtectionKeysDirectory, contentRootPath);
}

/// <summary>Backing store for the EBS's durable state. See <see cref="EbsOptions.StorageProvider"/>.</summary>
public enum EbsStorageProvider
{
    /// <summary>A single SQLite file under <see cref="EbsOptions.DataDirectory"/>; survives restarts.</summary>
    Sqlite,

    /// <summary>Process-local only; everything is lost on restart.</summary>
    InMemory,
}
