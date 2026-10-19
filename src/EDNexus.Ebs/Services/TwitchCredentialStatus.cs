namespace EDNexus.Ebs.Services;

/// <summary>What the EBS last learned about whether Twitch accepts the credentials it signs PubSub calls with.</summary>
public enum TwitchCredentialState
{
    /// <summary>Nothing learned yet: the startup check has not run or could not tell.</summary>
    Unknown = 0,

    /// <summary>Twitch accepted the credentials (a real broadcast succeeded, or the startup check was not refused).</summary>
    Accepted,

    /// <summary>
    /// Twitch refused the credentials (<c>401</c>/<c>403</c>, typically "JWT could not be verified"):
    /// a wrong <c>Twitch__ExtensionSecret</c>, a <c>Twitch__ExtensionId</c> that is not the extension's,
    /// or a wrong <c>Twitch__OwnerUserId</c>. Every broadcast fails until it is fixed, so viewers
    /// already watching never receive a live update, though new viewers still get the stored card.
    /// </summary>
    Rejected,
}

/// <summary>
/// The EBS's running answer to "does Twitch accept our PubSub credentials?", shared by the startup
/// check, every real broadcast, <c>/healthz</c> and the <c>/api/update-state</c> response.
/// </summary>
/// <remarks>
/// A real broadcast is the authority: it uses exactly the credentials that matter, so a success
/// clears a rejection recorded by the startup check and a refusal sets one. Thread-safe: it is
/// written from request threads and the background check and read from both.
/// </remarks>
public sealed class TwitchCredentialStatus
{
    /// <summary>
    /// Response header on a <c>/api/update-state</c> failure caused by Twitch refusing the EBS's
    /// credentials. A header, because a proxy in front of the EBS can replace the body of a 502; the
    /// desktop app reads it to tell a server misconfiguration from a transient outage. Mirrored by
    /// <c>EDNexus.Core.Twitch.StreamStateApiClient.PubSubRejectedHeader</c>.
    /// </summary>
    public const string RejectedHeader = "X-EDNexus-Pubsub-Rejected";

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private TwitchCredentialState _state;
    private int? _httpStatus;
    private DateTimeOffset? _at;

    public TwitchCredentialStatus(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>A consistent snapshot of the state, the HTTP status behind it, and when it was recorded.</summary>
    public (TwitchCredentialState State, int? HttpStatus, DateTimeOffset? At) Current
    {
        get { lock (_gate) return (_state, _httpStatus, _at); }
    }

    public TwitchCredentialState State => Current.State;

    /// <summary>Records that Twitch accepted the credentials.</summary>
    public void MarkAccepted()
    {
        lock (_gate)
        {
            _state = TwitchCredentialState.Accepted;
            _httpStatus = null;
            _at = _time.GetUtcNow();
        }
    }

    /// <summary>Records that Twitch refused the credentials with <paramref name="httpStatus"/> (401 or 403).</summary>
    public void MarkRejected(int httpStatus)
    {
        lock (_gate)
        {
            _state = TwitchCredentialState.Rejected;
            _httpStatus = httpStatus;
            _at = _time.GetUtcNow();
        }
    }

    /// <summary>True for the statuses Twitch uses to refuse a signed token: the credentials, not the payload or Twitch itself.</summary>
    public static bool IsCredentialRefusal(int httpStatus) => httpStatus is 401 or 403;
}
