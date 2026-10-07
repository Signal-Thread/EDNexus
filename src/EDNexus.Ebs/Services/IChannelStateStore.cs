using System.Text.Json;

namespace EDNexus.Ebs.Services;

/// <summary>
/// Caches the most recent state payload broadcast for each channel, so
/// <c>GET /api/initial-state</c> can serve the extension frontend on load without waiting for the
/// next PubSub event.
/// </summary>
public interface IChannelStateStore
{
    /// <summary>Records the latest state payload published for a channel.</summary>
    void Set(string channelId, JsonElement state);

    /// <summary>Attempts to retrieve the last known state payload for a channel.</summary>
    bool TryGet(string channelId, out JsonElement state);

    /// <summary>Forgets a channel's stored state, so <c>GET /api/initial-state</c> answers 404 again. No-op if none.</summary>
    void Remove(string channelId);

    /// <summary>
    /// Deletes every snapshot past the configured maximum age and returns how many went. Reads already
    /// treat such a snapshot as absent, so this only reclaims space — it is run on a timer
    /// (<see cref="ChannelStatePruneBackgroundService"/>), not on the request path. No-op when no maximum age is set.
    /// </summary>
    int PruneExpired();
}
