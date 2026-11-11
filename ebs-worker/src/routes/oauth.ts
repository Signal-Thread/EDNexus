import type { Hono } from 'hono';
import { clientIp, limitByIp, type AppEnv } from '../app';
import { clearChannel, OFFLINE_MESSAGE } from '../clearing';
import { TWITCH_AUTHORIZATION_ENDPOINT } from '../config';
import { verifyPkce } from '../crypto';
import {
  consumeAuthorizationCode,
  consumeSession,
  createAuthorizationCode,
  createSession,
} from '../db/pending';
import { removeState } from '../db/state';
import { getByToken, issueToken, revoke } from '../db/tokens';
import { isPlainObject, json, problem, readJsonBody } from '../http';
import { broadcast } from '../twitch/pubsub';
import { exchangeAuthorizationCode, getUser, revokeToken, TwitchOAuthError } from '../twitch/oauth';

// A real redirect_uri is a loopback address with a port and a short path; state is a random token; a
// PKCE S256 challenge is always exactly 43 base64url characters. Anything bigger is not a client of ours
// and would only be bytes held for the session's lifetime.
const MAX_REDIRECT_URI_LENGTH = 512;
const MAX_STATE_LENGTH = 512;
const CODE_CHALLENGE_LENGTH = 43;
const MAX_TOKEN_FIELD_LENGTH = 512;

const isBase64UrlChallenge = (value: string) => value.length === CODE_CHALLENGE_LENGTH && /^[A-Za-z0-9_-]+$/.test(value);

/** Exact host matching on purpose. The IPv6 loopback keeps its brackets in URL.hostname. */
function parseLoopbackRedirect(value: string): URL | null {
  try {
    const url = new URL(value);
    if (url.protocol !== 'http:') return null;
    return url.hostname === 'localhost' || url.hostname === '127.0.0.1' || url.hostname === '[::1]' ? url : null;
  } catch {
    return null;
  }
}

function buildDesktopRedirect(base: URL, state: string, extra: { code?: string; error?: string; errorDescription?: string }): string {
  // Serialised from the parsed URL, so the Location header is exactly the address that passed
  // parseLoopbackRedirect; any query string the desktop registered is kept.
  const url = new URL(base.href);
  url.searchParams.append('state', state);
  if (extra.code) url.searchParams.append('code', extra.code);
  if (extra.error) url.searchParams.append('error', extra.error);
  if (extra.errorDescription) url.searchParams.append('error_description', extra.errorDescription);
  return url.href;
}

const escapeHtml = (value: string) =>
  value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');

function errorPage(message: string): Response {
  return new Response(
    `<!DOCTYPE html>
<html><head><title>Login failed</title></head>
<body style="font-family: sans-serif; background:#141414; color:#e0a030; text-align:center; padding-top: 10vh;">
<h2>Login failed</h2><p>${escapeHtml(message)}</p>
</body></html>`,
    { status: 400, headers: { 'content-type': 'text/html; charset=utf-8' } },
  );
}

const bearer = (header: string | undefined): string | null =>
  header && /^bearer /i.test(header.trim()) ? header.trim().slice('Bearer '.length).trim() : null;

/**
 * The EBS-mediated Twitch OAuth flow: the desktop client only ever talks to these endpoints, never to
 * Twitch directly. See README.md for the full request/response contract.
 */
export function registerOAuthRoutes(app: Hono<AppEnv>): void {
  // One per-IP budget across all four routes: they are unauthenticated, and /authorize and /token each
  // allocate server-side state.
  app.use('/oauth/*', async (c, next) => {
    const limited = await limitByIp(c.env.RL_OAUTH, c.req.raw, 60);
    return limited ?? next();
  });

  // Step 1: the desktop app opens the commander's browser here. Records the desktop's own loopback
  // redirect/state/PKCE challenge, then redirects to Twitch's real consent page with the EBS's own
  // registered redirect URI and a session id (as Twitch's `state`) standing in for it.
  app.get('/oauth/authorize', async (c) => {
    const deps = c.var.deps;
    const query = c.req.query();
    const redirectUri = query.redirect_uri ?? '';
    const state = query.state ?? '';
    const codeChallenge = query.code_challenge ?? '';
    const method = query.code_challenge_method ?? '';

    if (!redirectUri.trim() || !state.trim() || !codeChallenge.trim())
      return problem(400, 'redirect_uri, state, and code_challenge are all required.');
    if (redirectUri.length > MAX_REDIRECT_URI_LENGTH || state.length > MAX_STATE_LENGTH) return problem(400, 'redirect_uri or state is too long.');
    if (method && method !== 'S256') return problem(400, 'Only the S256 code_challenge_method is supported.');
    if (!isBase64UrlChallenge(codeChallenge)) return problem(400, 'code_challenge must be a 43-character base64url S256 challenge.');
    if (!parseLoopbackRedirect(redirectUri)) return problem(400, 'redirect_uri must be an http loopback (localhost/127.0.0.1/[::1]) address.');

    const sessionId = await createSession(
      deps,
      { desktopRedirectUri: redirectUri, desktopState: state, codeChallenge },
      deps.config.oauthSessionTtlMinutes * 60_000,
    );

    const target = new URL(TWITCH_AUTHORIZATION_ENDPOINT);
    // URLSearchParams would encode the scope separator as "+"; Twitch is documented with "%20".
    const scope = encodeURIComponent(deps.config.oauthScopes.join(' '));
    const search =
      `client_id=${encodeURIComponent(deps.config.clientId)}` +
      `&redirect_uri=${encodeURIComponent(deps.config.oauthRedirectUri)}` +
      `&response_type=code&scope=${scope}&state=${encodeURIComponent(sessionId)}`;
    return c.redirect(`${target.origin}${target.pathname}?${search}`, 302);
  });

  // Step 2: Twitch redirects the commander's browser back here after they approve/deny the request. The
  // EBS exchanges the code for Twitch tokens using its client secret (never exposed to the desktop
  // client), identifies the broadcaster, and redirects the browser back to the desktop's own loopback
  // listener with a one-time authorization code (not a Twitch token).
  app.get('/oauth/callback', async (c) => {
    const deps = c.var.deps;
    const query = c.req.query();

    const sessionId = query.state ?? '';
    const session = sessionId.trim() ? await consumeSession(deps, sessionId) : null;
    if (!session) return errorPage('This login link has expired or was already used. Please restart the login from EDNexus.');

    // /oauth/authorize only stores a redirect_uri that passed this same check. Re-parsing it here means
    // every redirect below is built from the validated URL, never from the raw stored string.
    const desktop = parseLoopbackRedirect(session.desktopRedirectUri);
    if (!desktop) return errorPage('This login session is invalid. Please restart the login from EDNexus.');

    if (query.error) return c.redirect(buildDesktopRedirect(desktop, session.desktopState, { error: query.error, errorDescription: query.error_description }), 302);

    const code = query.code ?? '';
    if (!code.trim())
      return c.redirect(buildDesktopRedirect(desktop, session.desktopState, { error: 'server_error', errorDescription: 'Twitch did not return an authorization code.' }), 302);

    try {
      const twitchToken = await exchangeAuthorizationCode(deps.config, code, deps.config.oauthRedirectUri);
      const user = await getUser(deps.config, twitchToken.accessToken);
      if (!user?.id.trim())
        return c.redirect(buildDesktopRedirect(desktop, session.desktopState, { error: 'server_error', errorDescription: 'Could not retrieve the Twitch user profile.' }), 302);

      const authCode = await createAuthorizationCode(
        deps,
        {
          channelId: user.id,
          username: user.displayName.trim() ? user.displayName : user.login,
          twitchAccessToken: twitchToken.accessToken,
          twitchRefreshToken: twitchToken.refreshToken,
          twitchExpiresAt: new Date(deps.now().getTime() + twitchToken.expiresIn * 1000).toISOString(),
          codeChallenge: session.codeChallenge,
          redirectUri: session.desktopRedirectUri,
        },
        deps.config.oauthCodeTtlSeconds * 1000,
      );
      return c.redirect(buildDesktopRedirect(desktop, session.desktopState, { code: authCode }), 302);
    } catch (error) {
      if (!(error instanceof TwitchOAuthError)) throw error;
      console.warn(`Twitch rejected the OAuth callback exchange: ${error.message}`);
      return c.redirect(buildDesktopRedirect(desktop, session.desktopState, { error: 'server_error', errorDescription: 'Twitch rejected the login.' }), 302);
    }
  });

  // Step 3: the desktop's loopback listener has the authorization code; it exchanges it here (proving
  // possession of the PKCE verifier) for the long-lived, per-broadcaster token.
  app.post('/oauth/token', async (c) => {
    const deps = c.var.deps;
    const body = await readJsonBody(c.req.raw, deps.config.maxRequestBodyBytes);
    if (!body.ok) return body.response;

    const fields = isPlainObject(body.value) ? body.value : {};
    const code = typeof fields.code === 'string' ? fields.code : '';
    const verifier = typeof fields.code_verifier === 'string' ? fields.code_verifier : '';
    const redirectUri = typeof fields.redirect_uri === 'string' ? fields.redirect_uri : '';

    if (!code.trim() || !verifier.trim() || !redirectUri.trim()) return problem(400, 'code, code_verifier, and redirect_uri are all required.');
    if (code.length > MAX_TOKEN_FIELD_LENGTH || verifier.length > MAX_TOKEN_FIELD_LENGTH || redirectUri.length > MAX_REDIRECT_URI_LENGTH)
      return problem(400, 'code, code_verifier, or redirect_uri is too long.');

    const pending = await consumeAuthorizationCode(deps, code);
    if (!pending) return problem(400, 'invalid_grant: the authorization code is unknown, expired, or already used.');
    if (!(await verifyPkce(verifier, pending.codeChallenge))) return problem(400, 'invalid_grant: the PKCE code_verifier does not match.');

    // RFC 6749 §4.1.3 / RFC 7636: the redirect_uri presented here must match the one bound to the
    // authorization at /oauth/authorize.
    if (redirectUri !== pending.redirectUri) return problem(400, 'invalid_grant: redirect_uri does not match the one used to start this authorization.');

    const issued = await issueToken(
      deps,
      pending.channelId,
      pending.username,
      pending.twitchAccessToken,
      pending.twitchRefreshToken,
      new Date(pending.twitchExpiresAt),
    );
    return json({ token: issued.token, channelId: issued.channelId, username: issued.username });
  });

  // Best-effort logout: revokes the broadcaster's long-lived token and their underlying Twitch grant,
  // and takes their card off the air, since signing out is a request to stop being shown. Idempotent: an
  // unknown or missing token gets 200, as there is nothing to do.
  app.post('/oauth/revoke', async (c) => {
    const deps = c.var.deps;
    const token = bearer(c.req.header('authorization'));
    const record = token ? await getByToken(deps, token) : null;
    if (!token || !record) return new Response(null, { status: 200 });

    // Clear before revoking: if the clear throws, the token still works and the client can retry. A
    // failed offline broadcast does not hold the revoke back: the snapshot is already gone, and leaving
    // a signed-out credential valid through a PubSub outage is the worse outcome.
    await clearChannel(deps, record.channelId);
    await revoke(deps, token);

    // There is no per-channel lock on a Worker, so a publish that authenticated before the revoke can
    // still store its snapshot after the clear above. A publish re-checks its token after storing and
    // takes the card back down if it is gone; this second removal covers the other interleaving (the
    // store landed before the token was deleted, but after the clear). Together no snapshot outlives
    // its credential.
    if (await removeState(deps, record.channelId)) await broadcast(deps, record.channelId, OFFLINE_MESSAGE);

    // Best-effort: the EBS credential is already dead, and this is a network call to Twitch. Twitch
    // documents the endpoint as taking the access token; the refresh token is deleted with the record
    // and expires on Twitch's own schedule.
    try {
      await revokeToken(deps.config, record.twitchAccessToken);
    } catch {
      /* best-effort */
    }

    return new Response(null, { status: 200 });
  });
}

// Re-exported for tests that want the same view of a request as the rate limiter has.
export { clientIp };
