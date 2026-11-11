import type { Deps } from './context';
import { removeState } from './db/state';
import { broadcast } from './twitch/pubsub';

/**
 * Broadcast to viewers already watching so their open card hides immediately. The extension recognises
 * `offline`; `v` matches the snapshot schema so older frontends don't reject it as a newer version.
 */
export const OFFLINE_MESSAGE = JSON.stringify({ v: 1, offline: true });

/** Set on a failed clear's 502 when the snapshot was removed and only the offline broadcast failed. */
export const SNAPSHOT_REMOVED_HEADER = 'X-EDNexus-Snapshot-Removed';

/**
 * Takes a channel's card off the air: shared by DELETE /api/update-state (the broadcaster switched the
 * card off) and POST /oauth/revoke (they signed out). The snapshot is removed whatever PubSub does,
 * since it is what leaks to future viewers; `viewersTold` says whether viewers already watching were
 * told, so the caller can decide whether that failure is worth a retry.
 */
export async function clearChannel(deps: Deps, channelId: string): Promise<{ removed: boolean; viewersTold: boolean }> {
  const removed = await removeState(deps, channelId);
  const viewersTold = (await broadcast(deps, channelId, OFFLINE_MESSAGE)) === 'published';
  return { removed, viewersTold };
}
