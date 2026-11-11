import { configFor } from './app';
import type { Deps } from './context';
import { pruneExpiredPending } from './db/pending';
import { pruneRateLimits } from './db/ratelimit';
import { pruneExpiredState } from './db/state';
import { dueForRefresh, markTwitchGrantInvalid, updateTwitchTokens } from './db/tokens';
import type { Env } from './env';
import { refreshToken, TwitchOAuthError } from './twitch/oauth';
import { checkCredentials } from './twitch/probe';

/** Cron expressions registered in wrangler.example.jsonc. Keep the two in step. */
export const CRON_PRUNE = '*/15 * * * *';
export const CRON_REFRESH = '*/30 * * * *';
export const CRON_CREDENTIAL_CHECK = '0 */6 * * *';

/**
 * Each refresh is one subrequest to Twitch, and a Worker invocation has a subrequest cap (50 on the free
 * plan). Do the soonest-to-expire grants first and leave the rest for the next run: the buffer is an
 * hour and the runs are 30 minutes apart, so a backlog drains before anything actually expires.
 */
const MAX_REFRESHES_PER_RUN = 40;

/** Replaces the three background services of the ASP.NET build. */
export async function runScheduled(env: Env, cron: string, now: () => Date = () => new Date()): Promise<void> {
  const result = configFor(env);
  if (!result.ok) return; // already logged by configFor
  const deps: Deps = { env, db: env.DB, config: result.config, now };

  switch (cron) {
    case CRON_PRUNE: {
      // A snapshot whose clear never arrived does not stay public forever, and expired logins and counters do not pile up.
      const [state, pending, limits] = [await pruneExpiredState(deps), await pruneExpiredPending(deps), await pruneRateLimits(deps)];
      console.log(`Pruned ${state} snapshot(s), ${pending} pending login row(s) and ${limits} rate-limit counter(s).`);
      return;
    }
    case CRON_REFRESH:
      await refreshDueGrants(deps);
      return;
    case CRON_CREDENTIAL_CHECK:
      await checkCredentials(deps);
      return;
    default:
      console.warn(`Unrecognised cron "${cron}"; nothing to do.`);
  }
}

/**
 * Keeps every broadcaster's underlying Twitch grant alive without them ever having to re-auth: refreshes
 * the access token of any grant whose expiry is within the buffer. If Twitch rejects the refresh token
 * (a 400/401: the commander revoked access, or it expired) the grant is marked invalid so
 * /api/update-state starts answering 401 and the desktop client prompts for a new login. A single
 * broadcaster's failure never aborts the run or logs anyone out.
 */
export async function refreshDueGrants(deps: Deps): Promise<void> {
  const cutoff = new Date(deps.now().getTime() + deps.config.tokenRefreshBufferMinutes * 60_000);

  for (const record of await dueForRefresh(deps, cutoff, MAX_REFRESHES_PER_RUN)) {
    if (!record.twitchRefreshToken) {
      // Could not be decrypted (TOKEN_ENCRYPTION_KEY changed): nothing to refresh with, so say so.
      await markTwitchGrantInvalid(deps, record.channelId);
      continue;
    }

    try {
      const refreshed = await refreshToken(deps.config, record.twitchRefreshToken);
      await updateTwitchTokens(
        deps,
        record.channelId,
        refreshed.accessToken,
        refreshed.refreshToken.trim() ? refreshed.refreshToken : record.twitchRefreshToken,
        new Date(deps.now().getTime() + refreshed.expiresIn * 1000),
      );
    } catch (error) {
      if (error instanceof TwitchOAuthError && error.isGrantRejection) {
        console.warn(`Failed to refresh the Twitch grant for channel ${record.channelId}; marking it invalid until the broadcaster re-authenticates: ${error.message}`);
        await markTwitchGrantInvalid(deps, record.channelId);
      } else {
        // A network blip, timeout, Twitch 5xx/429, an unparseable body, or "invalid client" from a
        // misconfigured secret: the grant is left valid and simply retried on the next run.
        console.warn(`Transient error refreshing the Twitch grant for channel ${record.channelId}; will retry next run: ${error instanceof Error ? error.message : error}`);
      }
    }
  }
}
