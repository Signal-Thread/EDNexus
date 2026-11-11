import type { Env } from './env';

/** Twitch's hard ceiling on a PubSub message. */
export const TWITCH_PUBSUB_MAX_MESSAGE_BYTES = 5120;
/** Smallest non-zero snapshot lifetime: older desktop builds refresh an unchanged card only every 6 hours. */
export const MIN_CHANNEL_STATE_MAX_AGE_HOURS = 12;
export const MIN_EXTENSION_SECRET_BYTES = 16;

export const TWITCH_AUTHORIZATION_ENDPOINT = 'https://id.twitch.tv/oauth2/authorize';
export const TWITCH_TOKEN_ENDPOINT = 'https://id.twitch.tv/oauth2/token';
export const TWITCH_REVOKE_ENDPOINT = 'https://id.twitch.tv/oauth2/revoke';
export const TWITCH_USERS_ENDPOINT = 'https://api.twitch.tv/helix/users';
export const TWITCH_PUBSUB_URL = 'https://api.twitch.tv/helix/extensions/pubsub';
export const PROJECT_SITE_URL = 'https://signal-thread.github.io/EDNexus/';

export interface Config {
  clientId: string;
  clientSecret: string;
  extensionId: string;
  extensionSecret: string;
  ownerUserId: string | null;
  tokenEncryptionKey: string;
  oauthRedirectUri: string;
  oauthScopes: string[];
  maxStatePayloadBytes: number;
  maxRequestBodyBytes: number;
  updateStateRateLimit: number;
  updateStateRateLimitWindowSeconds: number;
  initialStateCacheSeconds: number;
  oauthSessionTtlMinutes: number;
  oauthCodeTtlSeconds: number;
  tokenRefreshBufferMinutes: number;
  /** 0 disables the limit. */
  channelStateMaxAgeHours: number;
  additionalAllowedFrontendOrigins: Set<string>;
}

export type ConfigResult = { ok: true; config: Config } | { ok: false; failures: string[] };

/**
 * Reads and validates the Worker's configuration. A Worker has no startup, so this runs on the first
 * request (and every cron) and reports every problem at once, as the ASP.NET build's options validators did.
 */
export function loadConfig(env: Env): ConfigResult {
  const failures: string[] = [];
  const text = (name: keyof Env): string => {
    const value = env[name];
    return typeof value === 'string' ? value.trim() : '';
  };
  const integer = (name: keyof Env, fallback: number): number => {
    const raw = text(name);
    if (raw === '') return fallback;
    if (!/^-?\d+$/.test(raw)) {
      failures.push(`${name} must be a whole number (got "${raw}").`);
      return fallback;
    }
    return Number.parseInt(raw, 10);
  };

  const clientId = text('TWITCH_CLIENT_ID');
  const clientSecret = text('TWITCH_CLIENT_SECRET');
  const extensionId = text('TWITCH_EXTENSION_ID');
  const extensionSecret = text('TWITCH_EXTENSION_SECRET');
  const tokenEncryptionKey = text('TOKEN_ENCRYPTION_KEY');

  if (!clientId) failures.push('TWITCH_CLIENT_ID must be set.');
  if (!clientSecret) failures.push('TWITCH_CLIENT_SECRET must be set (the OAuth login cannot work without it).');
  if (!extensionId) failures.push('TWITCH_EXTENSION_ID must be set (it is the Client-Id of the PubSub call; often the same value as TWITCH_CLIENT_ID).');

  if (!extensionSecret) {
    failures.push('TWITCH_EXTENSION_SECRET must be set to a non-empty, valid base64 string.');
  } else {
    const bytes = tryBase64(extensionSecret);
    if (!bytes) failures.push('TWITCH_EXTENSION_SECRET must be set to a non-empty, valid base64 string.');
    else if (bytes.length < MIN_EXTENSION_SECRET_BYTES)
      failures.push(`TWITCH_EXTENSION_SECRET decodes to only ${bytes.length} bytes; expected at least ${MIN_EXTENSION_SECRET_BYTES} (Twitch issues 32).`);
  }

  if (!tokenEncryptionKey) {
    failures.push('TOKEN_ENCRYPTION_KEY must be set to the base64 of 32 random bytes.');
  } else if (tryBase64(tokenEncryptionKey)?.length !== 32) {
    failures.push('TOKEN_ENCRYPTION_KEY must be the base64 of exactly 32 bytes.');
  }

  const oauthRedirectUri = text('TWITCH_OAUTH_REDIRECT_URI') || 'https://ednexus.signal-and-thread.com/oauth/callback';
  if (!isAcceptableRedirectUri(oauthRedirectUri))
    failures.push('TWITCH_OAUTH_REDIRECT_URI must be an absolute https URL (http is allowed only for localhost), exactly as registered with Twitch.');

  const ownerUserId = text('TWITCH_OWNER_USER_ID');
  if (ownerUserId && !/^\d+$/.test(ownerUserId)) failures.push('TWITCH_OWNER_USER_ID, when set, must be the numeric Twitch user id of the extension owner.');

  const scopes = [
    ...new Set(
      (text('TWITCH_OAUTH_SCOPES') || 'user:read:email')
        .split(/[\s,]+/)
        .filter(Boolean)
        .map((s) => s.toLowerCase()),
    ),
  ];

  const maxStatePayloadBytes = integer('MAX_STATE_PAYLOAD_BYTES', 5000);
  const maxRequestBodyBytes = integer('MAX_REQUEST_BODY_BYTES', 16 * 1024);
  const channelStateMaxAgeHours = integer('CHANNEL_STATE_MAX_AGE_HOURS', 24);

  if (maxStatePayloadBytes < 1 || maxStatePayloadBytes > TWITCH_PUBSUB_MAX_MESSAGE_BYTES)
    failures.push(`MAX_STATE_PAYLOAD_BYTES must be between 1 and ${TWITCH_PUBSUB_MAX_MESSAGE_BYTES} (Twitch drops a PubSub message over 5 KiB).`);
  if (maxRequestBodyBytes < 1024) failures.push('MAX_REQUEST_BODY_BYTES must be at least 1024.');
  else if (maxRequestBodyBytes < maxStatePayloadBytes + 64)
    failures.push('MAX_REQUEST_BODY_BYTES must leave room for a full MAX_STATE_PAYLOAD_BYTES state plus the request envelope.');
  if (channelStateMaxAgeHours > 0 && channelStateMaxAgeHours < MIN_CHANNEL_STATE_MAX_AGE_HOURS)
    failures.push(
      `CHANNEL_STATE_MAX_AGE_HOURS must be 0 (no limit) or at least ${MIN_CHANNEL_STATE_MAX_AGE_HOURS}: a shorter limit could take down the live card of a desktop build that refreshes an unchanged card only every 6 hours.`,
    );

  const origins = new Set(
    text('ADDITIONAL_ALLOWED_FRONTEND_ORIGINS')
      .split(/[\s,]+/)
      .filter(Boolean)
      .map((o) => o.toLowerCase()),
  );

  const config: Config = {
    clientId,
    clientSecret,
    extensionId,
    extensionSecret,
    ownerUserId: ownerUserId || null,
    tokenEncryptionKey,
    oauthRedirectUri,
    oauthScopes: scopes,
    maxStatePayloadBytes,
    maxRequestBodyBytes,
    updateStateRateLimit: Math.max(1, integer('UPDATE_STATE_RATE_LIMIT', 1)),
    updateStateRateLimitWindowSeconds: Math.max(1, integer('UPDATE_STATE_RATE_LIMIT_WINDOW_SECONDS', 2)),
    initialStateCacheSeconds: integer('INITIAL_STATE_CACHE_SECONDS', 5),
    oauthSessionTtlMinutes: Math.max(1, integer('OAUTH_SESSION_TTL_MINUTES', 10)),
    oauthCodeTtlSeconds: Math.max(10, integer('OAUTH_CODE_TTL_SECONDS', 60)),
    tokenRefreshBufferMinutes: Math.max(0, integer('TWITCH_TOKEN_REFRESH_BUFFER_MINUTES', 60)),
    channelStateMaxAgeHours,
    additionalAllowedFrontendOrigins: origins,
  };

  return failures.length === 0 ? { ok: true, config } : { ok: false, failures };
}

function isAcceptableRedirectUri(value: string): boolean {
  try {
    const url = new URL(value);
    if (url.protocol === 'https:') return true;
    return url.protocol === 'http:' && (url.hostname === 'localhost' || url.hostname === '127.0.0.1' || url.hostname === '[::1]');
  } catch {
    return false;
  }
}

function tryBase64(value: string): Uint8Array | null {
  try {
    return Uint8Array.from(atob(value), (c) => c.charCodeAt(0));
  } catch {
    return null;
  }
}
