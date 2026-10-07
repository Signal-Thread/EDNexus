namespace EliteDangerous.Galnet;

/// <summary>
/// Static configuration for a <see cref="GalnetClient"/> — the sending-application identity used for
/// the <c>User-Agent</c>, and the feed address.
/// </summary>
public sealed class GalnetClientOptions
{
    /// <summary>A unique, stable name for the calling application (e.g. "EDNexus"), sent as the User-Agent product.</summary>
    public required string SoftwareName { get; init; }

    /// <summary>The calling application's version, sent as the User-Agent product version.</summary>
    public required string SoftwareVersion { get; init; }

    /// <summary>
    /// The Galnet RSS address. Overridable for tests and for the localised feeds. Deliberately the
    /// CMS-backed feed rather than <c>community.elitedangerous.com/galnet-rss</c>: that one stamps
    /// every item in a fetch with the same build timestamp instead of the article's own publish time,
    /// which is why every headline used to show today's date regardless of when it actually ran.
    /// </summary>
    public string FeedUrl { get; init; } = "https://cms.zaonce.net/en-GB/rss.xml";

    /// <summary>
    /// The largest feed body (in bytes) the client will read. The live feed is a few hundred KB; this
    /// stops a misbehaving or hostile endpoint from making the app buffer gigabytes (the
    /// <see cref="HttpClient"/> default limit is 2 GB).
    /// </summary>
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;
}
