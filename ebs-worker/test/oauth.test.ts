import { afterEach, describe, expect, it, vi } from 'vitest';
import { pkceChallenge } from '../src/crypto';
import { DESKTOP_REDIRECT_URI, bearer, makeWorld, postState, pubsubCalls } from './helpers';

afterEach(() => vi.unstubAllGlobals());

const authorizeUrl = async (overrides: Record<string, string> = {}) =>
  `/oauth/authorize?${new URLSearchParams({
    redirect_uri: DESKTOP_REDIRECT_URI,
    state: 's',
    code_challenge: await pkceChallenge('v'.repeat(64)),
    code_challenge_method: 'S256',
    ...overrides,
  })}`;

describe('GET /oauth/authorize', () => {
  it('redirects to Twitch with the EBS redirect URI, a session id as state, and %20-separated scopes', async () => {
    const world = makeWorld();
    const response = await world.request(await authorizeUrl());
    expect(response.status).toBe(302);
    const location = new URL(response.headers.get('location')!);
    expect(location.origin + location.pathname).toBe('https://id.twitch.tv/oauth2/authorize');
    expect(location.searchParams.get('client_id')).toBe('client-id');
    expect(location.searchParams.get('redirect_uri')).toBe('https://ebs.example.test/oauth/callback');
    expect(location.searchParams.get('response_type')).toBe('code');
    expect(location.searchParams.get('scope')).toBe('user:read:email');
    expect(location.searchParams.get('state')).not.toBe('s'); // the desktop's own state never goes to Twitch
  });

  it('asks Twitch for each scope once', async () => {
    const world = makeWorld({ TWITCH_OAUTH_SCOPES: 'user:read:email, USER:READ:EMAIL channel:bot' });
    const response = await world.request(await authorizeUrl());
    expect(response.headers.get('location')).toContain('scope=user%3Aread%3Aemail%20channel%3Abot');
  });

  it.each([
    ['a missing redirect_uri', { redirect_uri: '' }],
    ['a missing state', { state: '' }],
    ['a missing code_challenge', { code_challenge: '' }],
    ['a plain challenge method', { code_challenge_method: 'plain' }],
    ['a challenge that is not 43 base64url characters', { code_challenge: 'short' }],
    ['a non-loopback redirect_uri', { redirect_uri: 'http://evil.example/callback' }],
    ['an https loopback redirect_uri', { redirect_uri: 'https://127.0.0.1/callback' }],
    ['a loopback lookalike host', { redirect_uri: 'http://127.0.0.1.evil.example/cb' }],
    ['an oversized state', { state: 's'.repeat(513) }],
    ['an oversized redirect_uri', { redirect_uri: `http://localhost/${'a'.repeat(520)}` }],
  ])('refuses %s with a 400', async (_name, overrides) => {
    const world = makeWorld();
    const response = await world.request(await authorizeUrl(overrides));
    expect(response.status).toBe(400);
    expect(response.headers.get('content-type')).toContain('application/problem+json');
  });

  it('accepts the IPv6 loopback', async () => {
    const world = makeWorld();
    const response = await world.request(await authorizeUrl({ redirect_uri: 'http://[::1]:5000/cb' }));
    expect(response.status).toBe(302);
  });
});

describe('the full login', () => {
  it('ends with a working token, and the desktop only ever sees a one-time code', async () => {
    const world = makeWorld();
    const verifier = 'v'.repeat(64);
    const authorize = await world.request(await authorizeUrl());
    const sessionId = new URL(authorize.headers.get('location')!).searchParams.get('state')!;

    const callback = await world.request(`/oauth/callback?code=twitch-code&state=${encodeURIComponent(sessionId)}`);
    expect(callback.status).toBe(302);
    const back = new URL(callback.headers.get('location')!);
    expect(back.origin + back.pathname).toBe(DESKTOP_REDIRECT_URI);
    expect(back.searchParams.get('state')).toBe('s');
    expect(back.href).not.toContain('tw-access');
    expect(back.href).not.toContain('tw-refresh');

    const exchanged = world.fetchCalls.find((c) => c.url === 'https://id.twitch.tv/oauth2/token')!;
    expect(new URLSearchParams(exchanged.body).get('client_secret')).toBe('client-secret');
    expect(new URLSearchParams(exchanged.body).get('redirect_uri')).toBe('https://ebs.example.test/oauth/callback');

    const token = await world.request('/oauth/token', {
      method: 'POST',
      body: JSON.stringify({ code: back.searchParams.get('code'), code_verifier: verifier, redirect_uri: DESKTOP_REDIRECT_URI }),
    });
    expect(token.status).toBe(200);
    expect(await token.json()).toMatchObject({ channelId: '4242', username: 'Cmdr', token: expect.any(String) });
  });

  it('is single use: the code, and the session, work once', async () => {
    const world = makeWorld();
    const authorize = await world.request(await authorizeUrl());
    const sessionId = new URL(authorize.headers.get('location')!).searchParams.get('state')!;
    const url = `/oauth/callback?code=c&state=${encodeURIComponent(sessionId)}`;
    const first = await world.request(url);
    const code = new URL(first.headers.get('location')!).searchParams.get('code')!;

    const replayedSession = await world.request(url);
    expect(replayedSession.status).toBe(400);
    expect(await replayedSession.text()).toContain('expired or was already used');

    const body = JSON.stringify({ code, code_verifier: 'v'.repeat(64), redirect_uri: DESKTOP_REDIRECT_URI });
    expect((await world.request('/oauth/token', { method: 'POST', body })).status).toBe(200);
    const replayedCode = await world.request('/oauth/token', { method: 'POST', body });
    expect(replayedCode.status).toBe(400);
    expect(await replayedCode.text()).toContain('invalid_grant');
  });

  it('refuses a code once it has expired', async () => {
    const world = makeWorld();
    const authorize = await world.request(await authorizeUrl());
    const sessionId = new URL(authorize.headers.get('location')!).searchParams.get('state')!;
    const callback = await world.request(`/oauth/callback?code=c&state=${encodeURIComponent(sessionId)}`);
    const code = new URL(callback.headers.get('location')!).searchParams.get('code')!;

    world.clock.advance(61_000); // OAUTH_CODE_TTL_SECONDS defaults to 60
    const response = await world.request('/oauth/token', {
      method: 'POST',
      body: JSON.stringify({ code, code_verifier: 'v'.repeat(64), redirect_uri: DESKTOP_REDIRECT_URI }),
    });
    expect(response.status).toBe(400);
  });

  it('refuses a session once the commander has taken too long on the consent page', async () => {
    const world = makeWorld();
    const authorize = await world.request(await authorizeUrl());
    const sessionId = new URL(authorize.headers.get('location')!).searchParams.get('state')!;
    world.clock.advance(11 * 60_000);
    const response = await world.request(`/oauth/callback?code=c&state=${encodeURIComponent(sessionId)}`);
    expect(response.status).toBe(400);
  });

  it('refuses the wrong PKCE verifier, and the code is spent by the attempt', async () => {
    const world = makeWorld();
    const authorize = await world.request(await authorizeUrl());
    const sessionId = new URL(authorize.headers.get('location')!).searchParams.get('state')!;
    const callback = await world.request(`/oauth/callback?code=c&state=${encodeURIComponent(sessionId)}`);
    const code = new URL(callback.headers.get('location')!).searchParams.get('code')!;

    const wrong = await world.request('/oauth/token', {
      method: 'POST',
      body: JSON.stringify({ code, code_verifier: 'x'.repeat(64), redirect_uri: DESKTOP_REDIRECT_URI }),
    });
    expect(wrong.status).toBe(400);
    expect(await wrong.text()).toContain('code_verifier');

    const right = await world.request('/oauth/token', {
      method: 'POST',
      body: JSON.stringify({ code, code_verifier: 'v'.repeat(64), redirect_uri: DESKTOP_REDIRECT_URI }),
    });
    expect(right.status).toBe(400);
  });

  it('refuses a redirect_uri that differs from the one the login started with', async () => {
    const world = makeWorld();
    const authorize = await world.request(await authorizeUrl());
    const sessionId = new URL(authorize.headers.get('location')!).searchParams.get('state')!;
    const callback = await world.request(`/oauth/callback?code=c&state=${encodeURIComponent(sessionId)}`);
    const code = new URL(callback.headers.get('location')!).searchParams.get('code')!;
    const response = await world.request('/oauth/token', {
      method: 'POST',
      body: JSON.stringify({ code, code_verifier: 'v'.repeat(64), redirect_uri: 'http://127.0.0.1:1/other' }),
    });
    expect(response.status).toBe(400);
    expect(await response.text()).toContain('redirect_uri');
  });

  it('hands a Twitch denial back to the desktop as an error redirect', async () => {
    const world = makeWorld();
    const authorize = await world.request(await authorizeUrl());
    const sessionId = new URL(authorize.headers.get('location')!).searchParams.get('state')!;
    const callback = await world.request(
      `/oauth/callback?error=access_denied&error_description=nope&state=${encodeURIComponent(sessionId)}`,
    );
    const back = new URL(callback.headers.get('location')!);
    expect(back.searchParams.get('error')).toBe('access_denied');
    expect(back.searchParams.get('error_description')).toBe('nope');
    expect(back.searchParams.get('state')).toBe('s');
    expect(back.searchParams.get('code')).toBeNull();
  });

  it('reports a Twitch exchange failure to the desktop without leaking Twitch\'s response', async () => {
    const world = makeWorld();
    world.onFetch(() => new Response('secret detail', { status: 400 }));
    const authorize = await world.request(await authorizeUrl());
    const sessionId = new URL(authorize.headers.get('location')!).searchParams.get('state')!;
    const callback = await world.request(`/oauth/callback?code=c&state=${encodeURIComponent(sessionId)}`);
    const back = new URL(callback.headers.get('location')!);
    expect(back.searchParams.get('error')).toBe('server_error');
    expect(back.href).not.toContain('secret');
  });

  it('keeps a query string the desktop registered on its redirect', async () => {
    const world = makeWorld();
    const authorize = await world.request(await authorizeUrl({ redirect_uri: 'http://localhost:5000/cb?app=ednexus' }));
    const sessionId = new URL(authorize.headers.get('location')!).searchParams.get('state')!;
    const callback = await world.request(`/oauth/callback?code=c&state=${encodeURIComponent(sessionId)}`);
    const back = new URL(callback.headers.get('location')!);
    expect(back.searchParams.get('app')).toBe('ednexus');
    expect(back.searchParams.get('code')).toBeTruthy();
  });

  it('signing in again invalidates the previous token', async () => {
    const world = makeWorld();
    const first = await world.login();
    const second = await world.login();
    expect(second.token).not.toBe(first.token);
    expect((await world.request('/api/update-state', postState(first.token))).status).toBe(401);
    world.clock.advance(5_000);
    expect((await world.request('/api/update-state', postState(second.token))).status).toBe(200);
  });

  it('never stores a Twitch token or a bearer token in the clear', async () => {
    const world = makeWorld();
    const { token } = await world.login();
    const dump = JSON.stringify(world.sqlite.prepare('SELECT * FROM broadcaster_tokens').all());
    expect(dump).not.toContain('tw-access');
    expect(dump).not.toContain('tw-refresh');
    expect(dump).not.toContain(token);
    // the one-time code's row is encrypted too, while it exists
    await world.request(await authorizeUrl());
    const authorize = await world.request(await authorizeUrl());
    const sessionId = new URL(authorize.headers.get('location')!).searchParams.get('state')!;
    await world.request(`/oauth/callback?code=c&state=${encodeURIComponent(sessionId)}`);
    expect(JSON.stringify(world.sqlite.prepare("SELECT payload FROM oauth_pending WHERE kind = 'code'").all())).not.toContain('tw-access');
  });

  it.each([
    ['a missing field', { code: 'x' }],
    ['an oversized field', { code: 'x'.repeat(513), code_verifier: 'y', redirect_uri: 'z' }],
  ])('POST /oauth/token refuses %s', async (_name, body) => {
    const response = await makeWorld().request('/oauth/token', { method: 'POST', body: JSON.stringify(body) });
    expect(response.status).toBe(400);
  });
});

describe('POST /oauth/revoke', () => {
  it('signs out: clears the card, tells viewers, kills the token and revokes the Twitch grant', async () => {
    const world = makeWorld();
    const { token, channelId } = await world.login();
    await world.request('/api/update-state', postState(token));
    expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(200);

    const before = pubsubCalls(world).length;
    const response = await world.request('/oauth/revoke', { method: 'POST', headers: bearer(token) });
    expect(response.status).toBe(200);

    expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(404);
    expect(JSON.parse(JSON.parse(pubsubCalls(world)[before]!.body).message)).toEqual({ v: 1, offline: true });
    expect((await world.request('/api/update-state', postState(token))).status).toBe(401);
    const revoked = world.fetchCalls.find((c) => c.url === 'https://id.twitch.tv/oauth2/revoke')!;
    expect(new URLSearchParams(revoked.body).get('token')).toBe('tw-access');
  });

  it('is idempotent: an unknown or missing token gets a quiet 200', async () => {
    const world = makeWorld();
    expect((await world.request('/oauth/revoke', { method: 'POST' })).status).toBe(200);
    expect((await world.request('/oauth/revoke', { method: 'POST', headers: bearer('nope') })).status).toBe(200);
    expect(world.fetchCalls).toHaveLength(0);
  });

  it('still revokes the token when Twitch is down', async () => {
    const world = makeWorld();
    const { token } = await world.login();
    world.onFetch(() => new Response('down', { status: 503 }));
    expect((await world.request('/oauth/revoke', { method: 'POST', headers: bearer(token) })).status).toBe(200);
    world.onFetch(() => new Response(null, { status: 204 }));
    expect((await world.request('/api/update-state', postState(token))).status).toBe(401);
  });
});
