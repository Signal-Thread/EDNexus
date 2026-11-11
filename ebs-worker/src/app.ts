import { Hono } from 'hono';
import { cors } from 'hono/cors';
import { clearChannel, OFFLINE_MESSAGE, SNAPSHOT_REMOVED_HEADER } from './clearing';
import { loadConfig, PROJECT_SITE_URL, type ConfigResult } from './config';
import type { Deps } from './context';
import { checkFixedWindow } from './db/ratelimit';
import { getState, removeState, setState } from './db/state';
import { getCredentialStatus, REJECTED_HEADER } from './db/status';
import { getByToken } from './db/tokens';
import type { Env, RateLimit } from './env';
import { byteLength, isPlainObject, json, problem, readJsonBody, unauthorized } from './http';
import { registerOAuthRoutes } from './routes/oauth';
import { broadcast } from './twitch/pubsub';

export type AuthResult = { ok: true; channelId: string; token: string } | { ok: false; response: Response };

export type AppEnv = {
  Bindings: Env;
  Variables: { deps: Deps; auth: AuthResult };
};

export interface AppOptions {
  /** Injectable clock, for tests. */
  now?: () => Date;
}

const configCache = new WeakMap<object, ConfigResult>();

/** Validated once per isolate (bindings do not change within one). */
export function configFor(env: Env): ConfigResult {
  let result = configCache.get(env);
  if (!result) {
    result = loadConfig(env);
    configCache.set(env, result);
    if (!result.ok) console.error(`EDNexus EBS cannot serve requests: the configuration is invalid.\n  - ${result.failures.join('\n  - ')}`);
  }
  return result;
}

/** The client address Cloudflare saw. The edge sets this header itself, so a client cannot choose its own bucket. */
export function clientIp(request: Request): string {
  return request.headers.get('cf-connecting-ip') ?? 'unknown';
}

/** A 429 that tells the client when to retry and is never replayed from a shared cache. */
export function tooManyRequests(retryAfterSeconds: number): Response {
  return new Response(null, { status: 429, headers: { 'retry-after': String(retryAfterSeconds), 'cache-control': 'no-store' } });
}

/** Per-client-IP limit through a Rate Limiting binding. Skipped when the binding is not configured (tests, local dev). */
export async function limitByIp(binding: RateLimit | undefined, request: Request, periodSeconds: number): Promise<Response | null> {
  if (!binding) return null;
  const { success } = await binding.limit({ key: clientIp(request) });
  return success ? null : tooManyRequests(periodSeconds);
}

function isAllowedFrontendOrigin(origin: string, additional: Set<string>): boolean {
  if (additional.has(origin.toLowerCase())) return true;
  try {
    const url = new URL(origin);
    return url.protocol === 'https:' && (url.hostname === 'ext-twitch.tv' || url.hostname.endsWith('.ext-twitch.tv'));
  } catch {
    return false;
  }
}

const bearerToken = (header: string | null): string | null => {
  if (!header || !/^bearer /i.test(header.trim())) return null;
  return header.trim().slice('Bearer '.length).trim();
};

/**
 * Authenticates a broadcaster by the long-lived EBS token minted at POST /oauth/token. The channel id is
 * resolved server-side from the token, never taken from the request body, so a compromised client cannot
 * spoof another broadcaster's channel.
 */
export async function authenticateBroadcaster(deps: Deps, header: string | null, requireValidTwitchGrant: boolean): Promise<AuthResult> {
  const token = bearerToken(header);
  if (!token) return { ok: false, response: unauthorized() };

  const record = await getByToken(deps, token);
  if (!record) {
    // Say which kind of 401 this is: an empty 401 sends people hunting through their Twitch console
    // configuration. Tokens survive restarts, so an unknown one was revoked, replaced by a newer
    // sign-in, or issued by a different EBS.
    return {
      ok: false,
      response: problem(401, 'This token is not known to the service. It was revoked, replaced by a newer sign-in, or issued by a different service — log in again.'),
    };
  }
  if (requireValidTwitchGrant && !record.isTwitchGrantValid)
    return { ok: false, response: problem(401, 'The underlying Twitch grant is no longer valid — please log in again.') };

  return { ok: true, channelId: record.channelId, token };
}

function describeKind(value: unknown): string {
  if (Array.isArray(value)) return 'array';
  if (typeof value === 'boolean') return String(value);
  return typeof value;
}

export function createApp(options: AppOptions = {}): Hono<AppEnv> {
  const now = options.now ?? (() => new Date());
  const app = new Hono<AppEnv>();

  // Anyone who lands on the bare EBS host (e.g. following the OAuth redirect URI's origin) gets the
  // project site rather than a 404. Needs no configuration, so it answers even when that is invalid.
  app.get('/', (c) => c.redirect(PROJECT_SITE_URL, 302));

  // Validate the configuration (the ASP.NET build refused to start; a Worker can only refuse to serve).
  app.use('*', async (c, next) => {
    const result = configFor(c.env);
    if (!result.ok) return c.json({ status: 'misconfigured' }, 503);
    c.set('deps', { env: c.env, db: c.env.DB, config: result.config, now });
    await next();
  });

  // Refuse an honestly-declared oversized body before anything reads it. A chunked upload that declares
  // nothing is caught where the body is read (readJsonBody).
  app.use('*', async (c, next) => {
    const path = new URL(c.req.url).pathname.toLowerCase();
    if ((c.req.method === 'POST' || c.req.method === 'DELETE') && (path.startsWith('/api/update-state') || path.startsWith('/oauth'))) {
      const limit = c.var.deps.config.maxRequestBodyBytes;
      if (Number(c.req.header('content-length')) > limit) return problem(413, `The request body exceeds the ${limit} byte limit.`);
    }
    await next();
  });

  // Only the extension's own Twitch-hosted iframe (https://*.ext-twitch.tv), plus origins listed in
  // ADDITIONAL_ALLOWED_FRONTEND_ORIGINS (Developer Rig, local asset server), may read from a browser.
  app.use('*', (c, next) =>
    cors({
      origin: (origin) => (isAllowedFrontendOrigin(origin, c.var.deps.config.additionalAllowedFrontendOrigins) ? origin : ''),
      allowMethods: ['GET'],
      // A browser hides every response header outside the CORS-safelisted set from the extension's
      // script unless it is exposed; the frontend honours Retry-After on a 429 from initial-state.
      exposeHeaders: ['Retry-After'],
    })(c, next),
  );

  // "ok" means the Worker is up and answering, and a credential problem is reported alongside it, never
  // instead of it. `pubsub` is "unknown", "accepted" or "rejected".
  app.get('/healthz', async (c) => {
    const status = await getCredentialStatus(c.var.deps);
    return c.json({ status: 'ok', pubsub: status.state, pubsubStatus: status.httpStatus, pubsubCheckedAt: status.at });
  });

  registerOAuthRoutes(app);

  // Authenticates (via the EBS-issued token) and rate-limits per authenticated channel; an unauthenticated
  // caller is limited per client address instead, so it cannot grind through guesses for free.
  app.use('/api/update-state', async (c, next) => {
    const method = c.req.method;
    if (method !== 'POST' && method !== 'DELETE') return next();

    const deps = c.var.deps;
    // Taking a card down needs no Twitch grant (PubSub is signed with the extension secret), and a
    // broadcaster whose grant lapsed must still be able to stop being shown.
    const auth = await authenticateBroadcaster(deps, c.req.header('authorization') ?? null, method === 'POST');
    c.set('auth', auth);

    const partition = auth.ok ? `ch:${auth.channelId}` : `ip:${clientIp(c.req.raw)}`;
    // Separate budgets so switching the card off straight after a publish is never rejected by that
    // publish's window: a clear is the privacy-critical request of the two.
    const limited =
      method === 'POST'
        ? await checkFixedWindow(deps, 'update-state', partition, deps.config.updateStateRateLimit, deps.config.updateStateRateLimitWindowSeconds)
        : await checkFixedWindow(deps, 'clear-state', partition, 10, 60);
    if (!limited.allowed) return tooManyRequests(limited.retryAfterSeconds);

    await next();
  });

  app.post('/api/update-state', async (c) => {
    const deps = c.var.deps;
    const auth = c.var.auth;
    if (!auth.ok) return auth.response;

    const body = await readJsonBody(c.req.raw, deps.config.maxRequestBodyBytes);
    if (!body.ok) return body.response;

    const state = isPlainObject(body.value) ? body.value.state : undefined;
    if (!isPlainObject(state)) {
      return problem(400, state === undefined || state === null ? 'The request must contain a "state" JSON object.' : `"state" must be a JSON object, not ${describeKind(state)}.`);
    }

    // What is size-checked is the compact serialisation, so that is what is stored and broadcast.
    const message = JSON.stringify(state);
    const bytes = byteLength(message);
    if (bytes > deps.config.maxStatePayloadBytes)
      return problem(413, `Serialized state payload is ${bytes} bytes, which exceeds the ${deps.config.maxStatePayloadBytes} byte limit.`);

    const channelId = auth.channelId;

    // Still the live credential? A sign-out that finished while this request was being read has revoked
    // the token, and storing a snapshot after that clear would leave a public card with no credential
    // able to take it down.
    const stillAuthorized = await authenticateBroadcaster(deps, c.req.header('authorization') ?? null, true);
    if (!stillAuthorized.ok || stillAuthorized.channelId !== channelId) return stillAuthorized.ok ? unauthorized() : stillAuthorized.response;

    await setState(deps, channelId, message);
    const result = await broadcast(deps, channelId, message);

    // Without a per-channel lock (a Worker has none), close the remaining race after the fact: a sign-out
    // that landed between the check above and the store has revoked the token, and its own clear may have
    // run before this snapshot was written. If the credential is gone now, take the card back down.
    const afterwards = await authenticateBroadcaster(deps, c.req.header('authorization') ?? null, false);
    if (!afterwards.ok || afterwards.channelId !== channelId) {
      await removeState(deps, channelId);
      if (result === 'published') await broadcast(deps, channelId, OFFLINE_MESSAGE);
      return afterwards.ok ? unauthorized() : afterwards.response;
    }

    if (result !== 'published') {
      console.warn(`Failed to publish state update for channel ${channelId} to Twitch PubSub.`);

      // Twitch refusing the EBS's own credentials is a server misconfiguration, not an outage, and no
      // amount of retrying fixes it. Say so with a header (a proxy in front can replace a 502's body)
      // and a 503 (which one does not rewrite), so the desktop app can tell the commander it is not
      // their sign-in. Decided from THIS call's outcome, not the shared status.
      if (result === 'credentials_rejected') {
        return problem(
          503,
          "Twitch is rejecting this service's PubSub credentials, so live updates cannot be delivered. The card is stored and new viewers still see it. This is a server configuration problem.",
          { [REJECTED_HEADER]: 'credentials' },
        );
      }
      return problem(502, 'Failed to publish the update to Twitch PubSub.');
    }

    return json({ published: true, channelId, bytes });
  });

  // The broadcaster switched the card off or signed out: forget the stored snapshot so /api/initial-state
  // stops serving it to anyone who asks, and tell viewers already watching to hide it.
  app.delete('/api/update-state', async (c) => {
    const auth = c.var.auth;
    if (!auth.ok) return auth.response;

    const { viewersTold } = await clearChannel(c.var.deps, auth.channelId);

    // A 502, like the POST's, when viewers already watching were not told: the snapshot is gone either
    // way, and a retry is harmless, so the client tries again rather than leave them the card.
    if (!viewersTold)
      return problem(502, 'The card was removed, but Twitch PubSub did not deliver the offline message to viewers already watching.', {
        [SNAPSHOT_REMOVED_HEADER]: 'true',
      });

    return new Response(null, { status: 204 });
  });

  app.get('/api/initial-state/:channelId', async (c) => {
    const deps = c.var.deps;
    const limited = await limitByIp(c.env.RL_INITIAL_STATE, c.req.raw, 10);
    if (limited) return limited;

    const state = await getState(deps, c.req.param('channelId'));
    if (state === null) return json({ message: 'No state has been published for this channel yet.' }, 404);

    // Lets a CDN/proxy/browser absorb a burst of viewers; live changes arrive by PubSub, so a few seconds
    // of staleness is invisible. The CORS middleware adds Vary: Origin, so a shared cache keeps one copy
    // per allowed origin instead of replaying one origin's Access-Control-Allow-Origin.
    const headers: Record<string, string> = { 'content-type': 'application/json' };
    if (deps.config.initialStateCacheSeconds > 0) headers['cache-control'] = `public, max-age=${deps.config.initialStateCacheSeconds}`;
    return new Response(state, { headers });
  });

  return app;
}
