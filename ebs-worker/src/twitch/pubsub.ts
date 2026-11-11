import { TWITCH_PUBSUB_URL } from '../config';
import type { Deps } from '../context';
import { isCredentialRefusal, markCredentialsAccepted, markCredentialsRejected } from '../db/status';
import { createExternalServiceToken } from './jwt';

export type PubSubResult = 'published' | 'credentials_rejected' | 'failed';

/** Bounds how long one slow Helix call can hold a request. */
const TIMEOUT_MS = 10_000;

/** The Client-Id sent to Helix PubSub: the extension's id (the JWT is signed with its secret). */
export function pubSubClientId(deps: Deps): string {
  return deps.config.extensionId || deps.config.clientId;
}

/**
 * Relays a message to every viewer of the broadcaster's channel via "Send Extension PubSub Message".
 * `message` is the already-serialised JSON string Twitch requires. A network failure or timeout is a
 * `failed` result, not an exception: the caller decides what to tell the client.
 */
export async function broadcast(deps: Deps, broadcasterId: string, message: string): Promise<PubSubResult> {
  let response: Response;
  try {
    response = await fetch(TWITCH_PUBSUB_URL, {
      method: 'POST',
      headers: {
        authorization: `Bearer ${await createExternalServiceToken(deps.config, broadcasterId, deps.now())}`,
        'client-id': pubSubClientId(deps),
        'content-type': 'application/json',
      },
      body: JSON.stringify({ target: ['broadcast'], broadcaster_id: broadcasterId, message }),
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
  } catch (error) {
    console.warn(`Twitch PubSub broadcast for channel ${broadcasterId} could not be sent: ${error instanceof Error ? error.message : error}`);
    return 'failed';
  }

  if (!response.ok) {
    console.warn(`Twitch PubSub broadcast for channel ${broadcasterId} failed with ${response.status}: ${await response.text().catch(() => '')}`);
    // Only a refusal of the signed token says anything about the credentials; a 400 is the payload and
    // a 5xx is Twitch, and neither should flip the status.
    if (isCredentialRefusal(response.status)) {
      await markCredentialsRejected(deps, response.status);
      return 'credentials_rejected';
    }
    return 'failed';
  }

  // A real broadcast is the authority: it clears a rejection the credential check recorded.
  await markCredentialsAccepted(deps);
  return 'published';
}
