using System.Text.Json;

namespace EDNexus.Ebs.Services;

/// <summary>
/// Takes a channel's card off the air: shared by <c>DELETE /api/update-state</c> (the broadcaster
/// switched the card off) and <c>POST /oauth/revoke</c> (they signed out). Leaving the last snapshot
/// in place would keep serving "Docked at …" to anyone hitting the unauthenticated
/// <c>/api/initial-state</c> after the broadcaster had asked for it to stop.
/// </summary>
public static class ChannelStateClearing
{
    /// <summary>
    /// Broadcast to viewers already watching so their open card hides immediately. The extension
    /// recognises <c>offline</c>; <c>v</c> matches the snapshot schema so older frontends don't
    /// reject it as a newer, unsupported version.
    /// </summary>
    /// <summary>
    /// Set on a failed clear's <c>502</c> when the snapshot was removed and only the offline
    /// broadcast failed. A proxy in front of an unreachable EBS answers <c>502</c> too, so the status
    /// alone does not tell the desktop app its card is already gone.
    /// </summary>
    public const string SnapshotRemovedHeader = "X-EDNexus-Snapshot-Removed";

    public static readonly JsonElement OfflineMessage = JsonSerializer.SerializeToElement(new { v = 1, offline = true });

    /// <summary>
    /// Forgets the stored snapshot, then tells live viewers. The snapshot is removed whatever
    /// PubSub does, since it is what leaks to future viewers; the result says whether viewers
    /// already watching were told, so the caller can decide whether that failure is worth a retry.
    /// </summary>
    /// <returns>True when the offline broadcast was delivered.</returns>
    public static async Task<bool> ClearAsync(string channelId, IChannelStateStore stateStore, ITwitchPubSubClient pubSubClient, CancellationToken ct)
    {
        stateStore.Remove(channelId);
        try { return await pubSubClient.BroadcastAsync(channelId, OfflineMessage, ct).ConfigureAwait(false) == PubSubBroadcastResult.Published; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }
}
