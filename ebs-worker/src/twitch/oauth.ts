import {
  TWITCH_REVOKE_ENDPOINT,
  TWITCH_TOKEN_ENDPOINT,
  TWITCH_USERS_ENDPOINT,
  type Config,
} from '../config';

const TIMEOUT_MS = 15_000;

export interface TwitchTokenResponse {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
}

export interface TwitchUser {
  id: string;
  login: string;
  displayName: string;
}

/** Thrown when Twitch rejects a token/user request the EBS makes on the broadcaster's behalf. */
export class TwitchOAuthError extends Error {
  constructor(
    message: string,
    readonly status: number | null = null,
  ) {
    super(message);
  }

  /**
   * True only when Twitch said the grant itself is dead: a 400/401 for the refresh token (Invalid
   * refresh token, invalid_grant), meaning the commander revoked access or it expired. Everything else
   * is transient and the grant must be left alone: a 5xx, a 429, a timeout or an unparseable body says
   * nothing about the grant, and neither does "invalid client", which is THIS service's misconfiguration
   * (a rotated secret) and would otherwise log every broadcaster out at once.
   */
  get isGrantRejection(): boolean {
    return (this.status === 400 || this.status === 401) && !/invalid client/i.test(this.message);
  }
}

async function postToken(config: Config, form: Record<string, string>): Promise<TwitchTokenResponse> {
  const response = await fetch(TWITCH_TOKEN_ENDPOINT, {
    method: 'POST',
    body: new URLSearchParams(form),
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  const text = await response.text();
  if (!response.ok) throw new TwitchOAuthError(`Twitch rejected the token request: HTTP ${response.status} — ${text}`, response.status);

  let parsed: { access_token?: string; refresh_token?: string; expires_in?: number };
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    throw new TwitchOAuthError(`Unparseable Twitch token response: ${error instanceof Error ? error.message : error}`);
  }
  if (!parsed.access_token?.trim()) throw new TwitchOAuthError('Twitch returned an empty token response.');
  return { accessToken: parsed.access_token, refreshToken: parsed.refresh_token ?? '', expiresIn: parsed.expires_in ?? 0 };
}

export function exchangeAuthorizationCode(config: Config, code: string, redirectUri: string): Promise<TwitchTokenResponse> {
  return postToken(config, {
    client_id: config.clientId,
    client_secret: config.clientSecret,
    code,
    grant_type: 'authorization_code',
    redirect_uri: redirectUri,
  });
}

export function refreshToken(config: Config, refresh: string): Promise<TwitchTokenResponse> {
  return postToken(config, {
    client_id: config.clientId,
    client_secret: config.clientSecret,
    grant_type: 'refresh_token',
    refresh_token: refresh,
  });
}

/** The user the access token belongs to, or null when Twitch returned none. */
export async function getUser(config: Config, accessToken: string): Promise<TwitchUser | null> {
  const response = await fetch(TWITCH_USERS_ENDPOINT, {
    headers: { authorization: `Bearer ${accessToken}`, 'client-id': config.clientId },
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  const text = await response.text();
  if (!response.ok) throw new TwitchOAuthError(`Twitch rejected the user lookup: HTTP ${response.status} — ${text}`, response.status);

  try {
    const data = (JSON.parse(text) as { data?: { id?: string; login?: string; display_name?: string }[] }).data;
    const first = data?.[0];
    return first ? { id: first.id ?? '', login: first.login ?? '', displayName: first.display_name ?? '' } : null;
  } catch (error) {
    throw new TwitchOAuthError(`Unparseable Twitch user response: ${error instanceof Error ? error.message : error}`);
  }
}

/** Best-effort: a revoke failing (e.g. an already-revoked token) must not block clearing local state. */
export async function revokeToken(config: Config, token: string): Promise<void> {
  const response = await fetch(TWITCH_REVOKE_ENDPOINT, {
    method: 'POST',
    body: new URLSearchParams({ client_id: config.clientId, token }),
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  await response.body?.cancel();
}
