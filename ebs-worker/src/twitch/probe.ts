import { TWITCH_PUBSUB_URL } from '../config';
import type { Deps } from '../context';
import { isCredentialRefusal, markCredentialsAccepted, markCredentialsRejected } from '../db/status';
import { createExternalServiceToken } from './jwt';
import { pubSubClientId } from './pubsub';

export type ProbeResult = 'inconclusive' | 'accepted' | 'rejected';

/** Not a target Twitch knows (broadcast, global, whisper-…), so it is refused whatever else is true. */
export const INVALID_TARGET = '__ednexus_credential_check__';

/** What Twitch says (as a 403) to a verified token that asks for a target it has no permission for. */
export const TARGET_REFUSED_MESSAGE = 'Permission denied to use target';

/**
 * Checks the PubSub credentials by sending the real endpoint a request signed exactly like a broadcast
 * but aimed at a target Twitch cannot deliver to, so nothing ever reaches a viewer.
 *
 * Twitch validates the body first (400), then verifies the token (403 "JWT could not be verified" for a
 * wrong secret, extension id or owner id), then checks the token's permissions against the target. So a
 * valid token meets the bogus target with 403 "Permission denied to use target", and a refused one with
 * 403 "JWT could not be verified". `accepted` therefore means "not refused"; only a real broadcast
 * proves the credentials work, which is why a real broadcast overrides this verdict. A status that says
 * nothing about the credentials (network error, 5xx, 429) is `inconclusive` and changes nothing.
 */
export async function checkCredentials(deps: Deps): Promise<ProbeResult> {
  // Any channel id will do for a request refused on its target; the owner's is the most natural, and "0" cannot be a real one.
  const channelId = deps.config.ownerUserId ?? '0';

  let response: Response;
  try {
    response = await fetch(TWITCH_PUBSUB_URL, {
      method: 'POST',
      headers: {
        authorization: `Bearer ${await createExternalServiceToken(deps.config, channelId, deps.now())}`,
        'client-id': pubSubClientId(deps),
        'content-type': 'application/json',
      },
      body: JSON.stringify({ broadcaster_id: channelId, target: [INVALID_TARGET], message: '{}' }),
      signal: AbortSignal.timeout(10_000),
    });
  } catch (error) {
    // Cannot reach Twitch (or cannot even sign: a malformed secret): says nothing about whether Twitch accepts the credentials.
    console.warn(`The Twitch credential check could not be completed; it proves nothing either way: ${error instanceof Error ? error.message : error}`);
    return 'inconclusive';
  }

  const text = await response.text().catch(() => '');

  if (isCredentialRefusal(response.status)) {
    if (response.status === 403 && text.toLowerCase().includes(TARGET_REFUSED_MESSAGE.toLowerCase())) return accepted(deps);

    await markCredentialsRejected(deps, response.status);
    console.error(
      `Twitch REJECTED the EBS's PubSub credentials (${response.status}: ${text}). Live updates will NOT reach viewers until this is fixed. ` +
        `Check TWITCH_EXTENSION_SECRET (this extension's own base64 secret), TWITCH_EXTENSION_ID (this extension's Client ID) ` +
        `and TWITCH_OWNER_USER_ID (the numeric Twitch user id of the extension's owner).`,
    );
    return 'rejected';
  }

  // 400: the credentials got past verification and only the deliberately invalid target was refused.
  if (response.status === 400) return accepted(deps);
  if (response.ok) {
    console.warn(`The Twitch credential check was accepted outright (${response.status}); it should have been refused for its invalid target.`);
    return accepted(deps);
  }

  console.warn(`The Twitch credential check got ${response.status}, which says nothing about the credentials; leaving the status unchanged.`);
  return 'inconclusive';
}

async function accepted(deps: Deps): Promise<ProbeResult> {
  await markCredentialsAccepted(deps);
  return 'accepted';
}
