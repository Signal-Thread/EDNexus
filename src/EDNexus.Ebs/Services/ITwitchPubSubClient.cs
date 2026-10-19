using System.Text.Json;

namespace EDNexus.Ebs.Services;

/// <summary>How a single PubSub broadcast ended.</summary>
public enum PubSubBroadcastResult
{
    /// <summary>Twitch accepted the message (HTTP 204).</summary>
    Published,

    /// <summary>
    /// Twitch refused the credentials the call was signed with (<c>401</c>/<c>403</c>): a server
    /// misconfiguration that no retry fixes. Reported per call so a caller never has to infer it from
    /// shared state, which can be left over from an earlier call.
    /// </summary>
    CredentialsRejected,

    /// <summary>Anything else: a bad payload, a Twitch outage, a rate limit.</summary>
    Failed,
}

/// <summary>Broadcasts state payloads to viewers of a channel via Twitch's Extensions PubSub API.</summary>
public interface ITwitchPubSubClient
{
    /// <summary>
    /// Sends <paramref name="state"/> to every viewer of <paramref name="broadcasterId"/> currently
    /// listening on the extension's "broadcast" PubSub target.
    /// </summary>
    /// <returns>How this call ended; <see cref="PubSubBroadcastResult.Published"/> when Twitch accepted the message (HTTP 204).</returns>
    Task<PubSubBroadcastResult> BroadcastAsync(string broadcasterId, JsonElement state, CancellationToken cancellationToken);
}
