import { base64Decode, base64UrlEncode, hmacSha256 } from '../crypto';
import type { Config } from '../config';

const encoder = new TextEncoder();

/** The `user_id` claim used when TWITCH_OWNER_USER_ID is not configured: the placeholder the service always shipped with. */
export const FALLBACK_USER_ID = 'ednexus_ebs';

/**
 * Signs the EBS's own OUTBOUND "external" role JWT (HS256, keyed by the base64 Extension Secret) for the
 * Helix PubSub call. Twitch's extension secrets are HMAC keys, so no JWT library is needed for one algorithm.
 */
export async function createExternalServiceToken(
  config: Pick<Config, 'extensionSecret' | 'ownerUserId'>,
  channelId: string,
  now: Date,
  lifetimeSeconds = 180,
): Promise<string> {
  if (!channelId.trim()) throw new Error('channelId must not be empty.');
  const iat = Math.floor(now.getTime() / 1000);
  const header = { alg: 'HS256', typ: 'JWT' };
  const payload = {
    exp: iat + Math.max(30, lifetimeSeconds),
    iat,
    user_id: config.ownerUserId ?? FALLBACK_USER_ID,
    role: 'external',
    channel_id: channelId,
    pubsub_perms: { send: ['broadcast'] },
  };
  const signingInput = `${base64UrlEncode(encoder.encode(JSON.stringify(header)))}.${base64UrlEncode(encoder.encode(JSON.stringify(payload)))}`;
  const signature = await hmacSha256(base64Decode(config.extensionSecret), encoder.encode(signingInput));
  return `${signingInput}.${base64UrlEncode(signature)}`;
}
