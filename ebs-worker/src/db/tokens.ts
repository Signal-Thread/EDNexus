import { DecryptionError, decryptText, encryptText, randomToken, sha256Hex, TOKEN_BYTES } from '../crypto';
import { iso, type Deps } from '../context';

export interface BroadcasterToken {
  channelId: string;
  username: string;
  twitchAccessToken: string;
  twitchRefreshToken: string;
  twitchExpiresAt: Date;
  /** False when Twitch revoked the grant, a refresh was rejected, or the stored ciphertext cannot be decrypted. */
  isTwitchGrantValid: boolean;
  createdAt: Date;
  lastRefreshedAt: Date | null;
}

interface Row {
  channel_id: string;
  username: string;
  twitch_access_token: string;
  twitch_refresh_token: string;
  twitch_expires_at: string;
  is_twitch_grant_valid: number;
  created_at: string;
  last_refreshed_at: string | null;
}

const COLUMNS =
  'channel_id, username, twitch_access_token, twitch_refresh_token, twitch_expires_at, is_twitch_grant_valid, created_at, last_refreshed_at';

/**
 * Issues a new long-lived bearer token for a broadcaster and stores it, hashed, with their Twitch grant
 * encrypted. channel_id is the primary key, so REPLACE drops any prior row (and with it the prior token's
 * hash) in one statement: signing in again invalidates the old token atomically.
 */
export async function issueToken(
  deps: Deps,
  channelId: string,
  username: string,
  twitchAccessToken: string,
  twitchRefreshToken: string,
  twitchExpiresAt: Date,
): Promise<{ token: string; channelId: string; username: string }> {
  const token = randomToken(TOKEN_BYTES);
  const key = deps.config.tokenEncryptionKey;
  await deps.db
    .prepare(
      `INSERT OR REPLACE INTO broadcaster_tokens
         (channel_id, token_hash, username, twitch_access_token, twitch_refresh_token, twitch_expires_at, is_twitch_grant_valid, created_at, last_refreshed_at)
       VALUES (?, ?, ?, ?, ?, ?, 1, ?, NULL)`,
    )
    .bind(
      channelId,
      await sha256Hex(token),
      username,
      await encryptText(key, twitchAccessToken, channelId),
      await encryptText(key, twitchRefreshToken, channelId),
      iso(twitchExpiresAt),
      iso(deps.now()),
    )
    .run();
  return { token, channelId, username };
}

export async function getByToken(deps: Deps, token: string): Promise<BroadcasterToken | null> {
  const row = await deps.db
    .prepare(`SELECT ${COLUMNS} FROM broadcaster_tokens WHERE token_hash = ?`)
    .bind(await sha256Hex(token))
    .first<Row>();
  return row ? readRow(deps, row) : null;
}

/** Grants whose access token expires at or before `cutoff`, soonest first, at most `limit` of them. */
export async function dueForRefresh(deps: Deps, cutoff: Date, limit: number): Promise<BroadcasterToken[]> {
  const { results } = await deps.db
    .prepare(
      `SELECT ${COLUMNS} FROM broadcaster_tokens
       WHERE is_twitch_grant_valid = 1 AND twitch_expires_at <= ?
       ORDER BY twitch_expires_at LIMIT ?`,
    )
    .bind(iso(cutoff), limit)
    .all<Row>();
  return Promise.all(results.map((row) => readRow(deps, row)));
}

export async function updateTwitchTokens(
  deps: Deps,
  channelId: string,
  twitchAccessToken: string,
  twitchRefreshToken: string,
  twitchExpiresAt: Date,
): Promise<void> {
  const key = deps.config.tokenEncryptionKey;
  await deps.db
    .prepare(
      `UPDATE broadcaster_tokens
       SET twitch_access_token = ?, twitch_refresh_token = ?, twitch_expires_at = ?, is_twitch_grant_valid = 1, last_refreshed_at = ?
       WHERE channel_id = ?`,
    )
    .bind(
      await encryptText(key, twitchAccessToken, channelId),
      await encryptText(key, twitchRefreshToken, channelId),
      iso(twitchExpiresAt),
      iso(deps.now()),
      channelId,
    )
    .run();
}

export async function markTwitchGrantInvalid(deps: Deps, channelId: string): Promise<void> {
  await deps.db.prepare('UPDATE broadcaster_tokens SET is_twitch_grant_valid = 0 WHERE channel_id = ?').bind(channelId).run();
}

/** Deletes the token with this value. True when one existed. */
export async function revoke(deps: Deps, token: string): Promise<boolean> {
  const result = await deps.db.prepare('DELETE FROM broadcaster_tokens WHERE token_hash = ?').bind(await sha256Hex(token)).run();
  return (result.meta.changes ?? 0) > 0;
}

async function readRow(deps: Deps, row: Row): Promise<BroadcasterToken> {
  let isGrantValid = row.is_twitch_grant_valid !== 0;
  let access = '';
  let refresh = '';
  try {
    access = await decryptText(deps.config.tokenEncryptionKey, row.twitch_access_token, row.channel_id);
    refresh = await decryptText(deps.config.tokenEncryptionKey, row.twitch_refresh_token, row.channel_id);
  } catch (error) {
    if (!(error instanceof DecryptionError)) throw error;
    // TOKEN_ENCRYPTION_KEY was changed or lost: the grant is unrecoverable. Surface it as invalid so
    // /api/update-state answers 401 and the desktop client prompts the commander to log in again.
    console.warn(`Could not decrypt the stored Twitch grant for channel ${row.channel_id}; treating it as invalid until the broadcaster re-authenticates.`);
    isGrantValid = false;
  }
  return {
    channelId: row.channel_id,
    username: row.username,
    twitchAccessToken: access,
    twitchRefreshToken: refresh,
    twitchExpiresAt: new Date(row.twitch_expires_at),
    isTwitchGrantValid: isGrantValid,
    createdAt: new Date(row.created_at),
    lastRefreshedAt: row.last_refreshed_at ? new Date(row.last_refreshed_at) : null,
  };
}
