import type { Deps } from '../context';

export type LimitResult = { allowed: true } | { allowed: false; retryAfterSeconds: number };

/**
 * Fixed-window counter in D1, keyed per policy and partition (a channel id). One atomic upsert both
 * counts and rolls the window, so concurrent requests cannot both slip under the limit. Used where the
 * window is shorter than the Rate Limiting binding supports (update-state: 1 per 2 seconds) and where
 * the partition is an authenticated channel, so the write volume is bounded by real broadcasters.
 */
export async function checkFixedWindow(deps: Deps, policy: string, partition: string, limit: number, windowSeconds: number): Promise<LimitResult> {
  const windowMs = windowSeconds * 1000;
  const nowMs = deps.now().getTime();
  const windowStart = Math.floor(nowMs / windowMs) * windowMs;

  const row = await deps.db
    .prepare(
      `INSERT INTO rate_limit (key, window_start, count) VALUES (?, ?, 1)
       ON CONFLICT(key) DO UPDATE SET
         count = CASE WHEN rate_limit.window_start = excluded.window_start THEN rate_limit.count + 1 ELSE 1 END,
         window_start = excluded.window_start
       RETURNING count`,
    )
    .bind(`${policy}:${partition}`, windowStart)
    .first<{ count: number }>();

  if ((row?.count ?? 1) <= limit) return { allowed: true };
  return { allowed: false, retryAfterSeconds: Math.max(1, Math.ceil((windowStart + windowMs - nowMs) / 1000)) };
}

/** Deletes counters whose window ended more than an hour ago. */
export async function pruneRateLimits(deps: Deps): Promise<number> {
  const result = await deps.db
    .prepare('DELETE FROM rate_limit WHERE window_start < ?')
    .bind(deps.now().getTime() - 3_600_000)
    .run();
  return result.meta.changes ?? 0;
}
