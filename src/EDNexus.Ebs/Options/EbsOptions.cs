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

    /// <summary>Maximum number of state updates accepted per broadcaster channel per window.</summary>
    public int UpdateStateRateLimit { get; set; } = 1;

    /// <summary>The rate limit window, in seconds, applied to <see cref="UpdateStateRateLimit"/>.</summary>
    public int UpdateStateRateLimitWindowSeconds { get; set; } = 2;

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
