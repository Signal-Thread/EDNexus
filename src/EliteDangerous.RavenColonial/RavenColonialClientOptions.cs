namespace EliteDangerous.RavenColonial;

/// <summary>
/// Static configuration for a <see cref="RavenColonialClient"/> — the sending-application identity
/// used for the <c>User-Agent</c>, and the API base address.
/// </summary>
public sealed class RavenColonialClientOptions
{
    /// <summary>A unique, stable name for the calling application (e.g. "EDNexus"), sent as the User-Agent product.</summary>
    public required string SoftwareName { get; init; }

    /// <summary>The calling application's version, sent as the User-Agent product version.</summary>
    public required string SoftwareVersion { get; init; }

    /// <summary>
    /// The API base address (no trailing slash needed). This is the service ravencolonial.com's own
    /// web app calls; the site itself is a static front end and serves its index page for every
    /// unknown path, so pointing at it instead would silently return HTML for every request.
    /// </summary>
    public string BaseUrl { get; init; } =
        "https://ravencolonial100-awcbdvabgze4c5cq.canadacentral-01.azurewebsites.net";

    /// <summary>
    /// The longest <c>Retry-After</c> the client will wait out before retrying a 429/503 once. A longer
    /// request is not waited for: the call fails promptly and the caller can try again later.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait before the single retry of a 429/503 that carried no <c>Retry-After</c>.</summary>
    public TimeSpan TransientRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}
