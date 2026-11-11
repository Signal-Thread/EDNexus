import { decryptText, encryptText, randomToken, SHORT_LIVED_BYTES, DecryptionError } from '../crypto';
import { iso, type Deps } from '../context';

/** Most sessions / codes held at once. Far above any real login rate; the per-IP limit on /oauth/* keeps one caller from filling it. */
export const MAX_PENDING_ENTRIES = 5000;

export interface PendingSession {
  desktopRedirectUri: string;
  desktopState: string;
  codeChallenge: string;
}

export interface PendingBroadcasterAuth {
  channelId: string;
  username: string;
  twitchAccessToken: string;
  twitchRefreshToken: string;
  twitchExpiresAt: string;
  codeChallenge: string;
  redirectUri: string;
}

type Kind = 'session' | 'code';

async function insert(deps: Deps, kind: Kind, payload: (id: string) => Promise<string>, ttlMs: number): Promise<string> {
  const id = randomToken(SHORT_LIVED_BYTES);
  const now = deps.now();
  await deps.db.batch([
    deps.db.prepare('DELETE FROM oauth_pending WHERE expires_at < ?').bind(iso(now)),
    deps.db
      .prepare('INSERT INTO oauth_pending (id, kind, payload, expires_at) VALUES (?, ?, ?, ?)')
      .bind(id, kind, await payload(id), iso(new Date(now.getTime() + ttlMs))),
    // The cap: when a burst fills a kind, drop whatever is closest to expiring rather than grow without bound.
    deps.db
      .prepare(
        `DELETE FROM oauth_pending WHERE id IN (
           SELECT id FROM oauth_pending WHERE kind = ? ORDER BY expires_at DESC LIMIT -1 OFFSET ?)`,
      )
      .bind(kind, MAX_PENDING_ENTRIES),
  ]);
  return id;
}

/** Deletes and returns an unexpired entry: single use, atomically, so a replayed id finds nothing. */
async function consume(deps: Deps, kind: Kind, id: string): Promise<string | null> {
  const row = await deps.db
    .prepare('DELETE FROM oauth_pending WHERE id = ? AND kind = ? RETURNING payload, expires_at')
    .bind(id, kind)
    .first<{ payload: string; expires_at: string }>();
  if (!row || Date.parse(row.expires_at) < deps.now().getTime()) return null;
  return row.payload;
}

export async function createSession(deps: Deps, session: PendingSession, ttlMs: number): Promise<string> {
  return insert(deps, 'session', async () => JSON.stringify(session), ttlMs);
}

export async function consumeSession(deps: Deps, sessionId: string): Promise<PendingSession | null> {
  const payload = await consume(deps, 'session', sessionId);
  return payload ? (JSON.parse(payload) as PendingSession) : null;
}

/** The code's payload carries Twitch tokens, so it is stored encrypted, bound to the code's own id. */
export async function createAuthorizationCode(deps: Deps, auth: PendingBroadcasterAuth, ttlMs: number): Promise<string> {
  return insert(deps, 'code', (id) => encryptText(deps.config.tokenEncryptionKey, JSON.stringify(auth), id), ttlMs);
}

export async function consumeAuthorizationCode(deps: Deps, code: string): Promise<PendingBroadcasterAuth | null> {
  const payload = await consume(deps, 'code', code);
  if (!payload) return null;
  try {
    return JSON.parse(await decryptText(deps.config.tokenEncryptionKey, payload, code)) as PendingBroadcasterAuth;
  } catch (error) {
    if (error instanceof DecryptionError) return null;
    throw error;
  }
}

export async function pruneExpiredPending(deps: Deps): Promise<number> {
  const result = await deps.db.prepare('DELETE FROM oauth_pending WHERE expires_at < ?').bind(iso(deps.now())).run();
  return result.meta.changes ?? 0;
}
