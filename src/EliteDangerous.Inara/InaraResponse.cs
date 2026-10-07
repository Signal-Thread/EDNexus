namespace EliteDangerous.Inara;

/// <summary>
/// The parsed reply from Inara. Inara answers with an overall <see cref="Status"/> in the header and
/// one status per submitted event. Codes: 200 = OK, 202 = warning (accepted), 204 = soft error,
/// 400 = error (e.g. invalid API key), 401/403 = auth problems.
/// </summary>
public sealed class InaraResponse
{
    /// <summary>Overall header <c>eventStatus</c>. 0 when the request never reached Inara (transport error).</summary>
    public int Status { get; init; }

    /// <summary>Human-readable header status text, if Inara supplied one.</summary>
    public string? StatusText { get; init; }

    /// <summary>Per-event statuses, in the order the events were submitted.</summary>
    public IReadOnlyList<InaraEventStatus> Events { get; init; } = Array.Empty<InaraEventStatus>();

    /// <summary>True when the batch was accepted (2xx family).</summary>
    public bool IsOk => Status is >= 200 and < 300;

    /// <summary>
    /// True for a non-transient failure the caller should stop retrying — chiefly a bad API key
    /// (400/401/403). Transport failures (Status 0) and warnings are not hard errors.
    /// </summary>
    public bool IsHardError => Status is 400 or 401 or 403;

    /// <summary>
    /// True when the request never got a usable answer from Inara (network failure, timeout, HTTP 5xx or
    /// 429, unparseable body): the batch was not recorded and is worth retrying later.
    /// </summary>
    public bool IsTransient => Status == 0;

    /// <summary>How long the server asked us to wait before retrying (<c>Retry-After</c> on 429/503), if it said.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Convenience for a purely transport-level failure (never reached Inara).</summary>
    public static InaraResponse TransportError(string message, TimeSpan? retryAfter = null)
        => new() { Status = 0, StatusText = message, RetryAfter = retryAfter };
}

/// <summary>The status Inara returned for a single submitted event.</summary>
public sealed record InaraEventStatus(int Status, string? StatusText);
