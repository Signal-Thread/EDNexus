import { iso, type Deps } from '../context';

/** Stores the compact JSON of a channel's last published state. */
export async function setState(deps: Deps, channelId: string, stateJson: string): Promise<void> {
  await deps.db
    .prepare(
      `INSERT INTO channel_state (channel_id, state_json, updated_at) VALUES (?, ?, ?)
       ON CONFLICT(channel_id) DO UPDATE SET state_json = excluded.state_json, updated_at = excluded.updated_at`,
    )
    .bind(channelId, stateJson, iso(deps.now()))
    .run();
}

/**
 * The stored state JSON, or null when there is none or it is older than CHANNEL_STATE_MAX_AGE_HOURS.
 * Past the limit counts as gone even before the cron has deleted the row; an unparseable timestamp is
 * treated as expired (failing closed keeps an unreadable row from being served forever).
 */
export async function getState(deps: Deps, channelId: string): Promise<string | null> {
  const row = await deps.db
    .prepare('SELECT state_json, updated_at FROM channel_state WHERE channel_id = ?')
    .bind(channelId)
    .first<{ state_json: string; updated_at: string }>();
  if (!row) return null;

  const maxAgeHours = deps.config.channelStateMaxAgeHours;
  if (maxAgeHours > 0) {
    const updated = Date.parse(row.updated_at);
    if (Number.isNaN(updated) || deps.now().getTime() - updated > maxAgeHours * 3_600_000) return null;
  }
  return row.state_json;
}

/** Forgets the snapshot. True when a row was removed. */
export async function removeState(deps: Deps, channelId: string): Promise<boolean> {
  const result = await deps.db.prepare('DELETE FROM channel_state WHERE channel_id = ?').bind(channelId).run();
  return (result.meta.changes ?? 0) > 0;
}

/** Deletes snapshots older than the limit. Returns how many. */
export async function pruneExpiredState(deps: Deps): Promise<number> {
  const maxAgeHours = deps.config.channelStateMaxAgeHours;
  if (maxAgeHours <= 0) return 0;
  const cutoff = new Date(deps.now().getTime() - maxAgeHours * 3_600_000);
  const result = await deps.db.prepare('DELETE FROM channel_state WHERE updated_at < ?').bind(iso(cutoff)).run();
  return result.meta.changes ?? 0;
}
