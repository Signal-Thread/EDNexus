namespace EliteDangerous.Eddn;

/// <summary>
/// Static configuration for an <see cref="EddnUploader"/> — the bits that identify the sending
/// application and never change between messages. The per-commander / per-event values
/// (uploaderID, game version, current system) live on <see cref="EddnState"/> instead.
/// </summary>
public sealed class EddnClientOptions
{
    /// <summary>A unique, stable name for the uploading application (e.g. "EDNexus").</summary>
    public required string SoftwareName { get; init; }

    /// <summary>The uploading application's version. Bump when message content changes.</summary>
    public required string SoftwareVersion { get; init; }

    /// <summary>The EDDN upload endpoint. Overridable for tests; defaults to the live relay.</summary>
    public string UploadEndpoint { get; init; } = "https://eddn.edcd.io:4430/upload/";

    /// <summary>When true, gzip the request body and set <c>Content-Encoding: gzip</c>.</summary>
    public bool UseGzip { get; init; }

    /// <summary>
    /// How long to wait before the single retry of a transient upload failure. Defaults to the
    /// EDDN-recommended minimum of one minute; tests can shorten it.
    /// </summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The most uploads held in memory waiting to be sent. When the relay is unreachable the queue
    /// would otherwise grow without bound and deliver hours-stale data; once full, the oldest queued
    /// message is dropped (and reported through <see cref="EddnUploader.Completed"/>).
    /// </summary>
    public int MaxQueueLength { get; init; } = 200;

    /// <summary>
    /// A queued message older than this when its turn comes is dropped instead of sent — EDDN data is
    /// only valuable while it is fresh, so a message that waited out an outage is not worth uploading.
    /// </summary>
    public TimeSpan MaxMessageAge { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The longest wait honoured from a relay <c>Retry-After</c> header on a 429/503.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// On dispose, how long the uploader lets already-queued messages drain before cancelling any
    /// in-flight send or retry wait. Keeps shutdown prompt even during an outage.
    /// </summary>
    public TimeSpan DisposeGrace { get; init; } = TimeSpan.FromSeconds(1.5);
}
