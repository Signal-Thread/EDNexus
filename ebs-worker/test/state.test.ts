import { createHmac } from 'node:crypto';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { base64UrlDecode } from '../src/crypto';
import { EXTENSION_SECRET, bearer, makeWorld, postState, pubsubCalls } from './helpers';

afterEach(() => vi.unstubAllGlobals());

describe('POST /api/update-state', () => {
  it('stores the compact state, broadcasts it to Twitch, and says so', async () => {
    const world = makeWorld();
    const { token, channelId } = await world.login();
    const response = await world.request('/api/update-state', {
      method: 'POST',
      headers: { 'content-type': 'application/json', ...bearer(token) },
      body: '{ "state": { "v": 1,   "headline": "Docked" } }',
    });
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ published: true, channelId, bytes: 27 });

    const [call] = pubsubCalls(world);
    expect(call!.headers.get('client-id')).toBe('extension-id');
    const sent = JSON.parse(call!.body);
    expect(sent).toMatchObject({ target: ['broadcast'], broadcaster_id: channelId });
    // What was size-checked is what is stored and what is broadcast: the compact form, not the request's whitespace.
    expect(sent.message).toBe('{"v":1,"headline":"Docked"}');
    expect(await (await world.request(`/api/initial-state/${channelId}`)).text()).toBe('{"v":1,"headline":"Docked"}');
  });

  it('signs the PubSub call with a verifiable external-role JWT for the broadcaster', async () => {
    const world = makeWorld({ TWITCH_OWNER_USER_ID: '999' });
    const { token, channelId } = await world.login();
    await world.request('/api/update-state', postState(token));

    const jwt = pubsubCalls(world)[0]!.headers.get('authorization')!.slice('Bearer '.length);
    const [header, payload, signature] = jwt.split('.') as [string, string, string];
    const expected = createHmac('sha256', Buffer.from(EXTENSION_SECRET, 'base64')).update(`${header}.${payload}`).digest('base64url');
    expect(signature).toBe(expected);
    expect(JSON.parse(new TextDecoder().decode(base64UrlDecode(header)))).toEqual({ alg: 'HS256', typ: 'JWT' });
    const claims = JSON.parse(new TextDecoder().decode(base64UrlDecode(payload)));
    expect(claims).toMatchObject({ role: 'external', channel_id: channelId, user_id: '999', pubsub_perms: { send: ['broadcast'] } });
    expect(claims.exp - claims.iat).toBe(180);
  });

  it('uses the historical placeholder user_id when no owner is configured', async () => {
    const world = makeWorld();
    const { token } = await world.login();
    await world.request('/api/update-state', postState(token));
    const payload = pubsubCalls(world)[0]!.headers.get('authorization')!.split('.')[1]!;
    expect(JSON.parse(new TextDecoder().decode(base64UrlDecode(payload))).user_id).toBe('ednexus_ebs');
  });

  describe('authentication', () => {
    it('401s without a token, with a non-Bearer header, and says why for an unknown token', async () => {
      const world = makeWorld();
      expect((await world.request('/api/update-state', { method: 'POST', body: '{}' })).status).toBe(401);
      world.clock.advance(2_000); // unauthenticated callers share a per-address budget
      expect((await world.request('/api/update-state', { method: 'POST', headers: { authorization: 'Basic abc' }, body: '{}' })).status).toBe(401);

      world.clock.advance(2_000);
      const unknown = await world.request('/api/update-state', postState('not-a-token'));
      expect(unknown.status).toBe(401);
      expect(await unknown.text()).toContain('not known to the service');
    });

    it('401s a broadcaster whose Twitch grant has lapsed, but still lets them take their card down', async () => {
      const world = makeWorld();
      const { token, channelId } = await world.login();
      await world.request('/api/update-state', postState(token));
      world.sqlite.exec('UPDATE broadcaster_tokens SET is_twitch_grant_valid = 0');

      world.clock.advance(5_000);
      const publish = await world.request('/api/update-state', postState(token));
      expect(publish.status).toBe(401);
      expect(await publish.text()).toContain('Twitch grant is no longer valid');

      expect((await world.request('/api/update-state', { method: 'DELETE', headers: bearer(token) })).status).toBe(204);
      expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(404);
    });

    it('treats a grant that cannot be decrypted (a changed TOKEN_ENCRYPTION_KEY) as lapsed, not as a 500', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      world.sqlite.exec("UPDATE broadcaster_tokens SET twitch_access_token = 'v1.AAAA.AAAA'");
      const response = await world.request('/api/update-state', postState(token));
      expect(response.status).toBe(401);
    });

    it('takes the channel from the token, never from the body', async () => {
      const world = makeWorld();
      const { token, channelId } = await world.login();
      await world.request('/api/update-state', {
        method: 'POST',
        headers: bearer(token),
        body: JSON.stringify({ channelId: 'someone-else', broadcaster_id: 'someone-else', state: { v: 1 } }),
      });
      expect(JSON.parse(pubsubCalls(world)[0]!.body).broadcaster_id).toBe(channelId);
    });
  });

  describe('validation', () => {
    it.each([
      ['no state', {}, 'must contain a "state" JSON object'],
      ['a null state', { state: null }, 'must contain a "state" JSON object'],
      ['a string state', { state: 'x' }, 'not string'],
      ['an array state', { state: [1] }, 'not array'],
      ['a number state', { state: 3 }, 'not number'],
    ])('400s %s', async (_name, body, message) => {
      const world = makeWorld();
      const { token } = await world.login();
      const response = await world.request('/api/update-state', { method: 'POST', headers: bearer(token), body: JSON.stringify(body) });
      expect(response.status).toBe(400);
      expect(((await response.json()) as { detail: string }).detail).toContain(message);
    });

    it('400s a body that is not JSON', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      expect((await world.request('/api/update-state', { method: 'POST', headers: bearer(token), body: '{nope' })).status).toBe(400);
    });

    it('413s a state over the payload limit, measured on its compact form, and stores nothing', async () => {
      const world = makeWorld({ MAX_STATE_PAYLOAD_BYTES: '100' });
      const { token, channelId } = await world.login();
      const response = await world.request('/api/update-state', postState(token, { v: 1, pad: 'x'.repeat(100) }));
      expect(response.status).toBe(413);
      expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(404);
      expect(pubsubCalls(world)).toHaveLength(0);
    });

    it('counts UTF-8 bytes, not characters', async () => {
      const world = makeWorld({ MAX_STATE_PAYLOAD_BYTES: '1024' });
      const { token } = await world.login();
      // 400 three-byte characters: 1200 bytes, only 400 characters
      expect((await world.request('/api/update-state', postState(token, { v: 1, name: '€'.repeat(400) }))).status).toBe(413);
    });

    it('413s a declared body over the request cap before reading it', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      const response = await world.request('/api/update-state', {
        method: 'POST',
        headers: { ...bearer(token), 'content-length': '20000' },
        body: '{}',
      });
      expect(response.status).toBe(413);
    });

    it('413s an undeclared (chunked) body over the cap once it has been read', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      const response = await world.request('/api/update-state', {
        method: 'POST',
        headers: bearer(token),
        body: JSON.stringify({ state: { v: 1, pad: 'x'.repeat(20000) } }),
      });
      expect(response.status).toBe(413);
    });

    it('stops reading an undeclared body as soon as it passes the cap', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      let pulls = 0;
      const stream = new ReadableStream<Uint8Array>({
        pull(controller) {
          pulls++;
          controller.enqueue(new Uint8Array(8 * 1024));
          if (pulls >= 1000) controller.close();
        },
      });
      const response = await world.request('/api/update-state', {
        method: 'POST',
        headers: bearer(token),
        body: stream,
        // @ts-expect-error duplex is required by undici for a streamed request body
        duplex: 'half',
      });
      expect(response.status).toBe(413);
      expect(pulls).toBeLessThan(10); // not the whole 8 MB
    });

    it('does not fail a publish when recording the credential status fails', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      world.sqlite.exec('DROP TABLE service_status');
      vi.spyOn(console, 'warn').mockImplementation(() => {});
      expect((await world.request('/api/update-state', postState(token))).status).toBe(200);
    });

    it('applies the same body cap to /oauth/*', async () => {
      const world = makeWorld();
      const response = await world.request('/oauth/token', { method: 'POST', headers: { 'content-length': '20000' }, body: '{}' });
      expect(response.status).toBe(413);
    });
  });

  describe('when Twitch fails', () => {
    it('answers 502 and still stores the card, so new viewers see it', async () => {
      const world = makeWorld();
      const { token, channelId } = await world.login();
      world.onFetch(() => new Response('boom', { status: 500 }));
      const response = await world.request('/api/update-state', postState(token));
      expect(response.status).toBe(502);
      expect(response.headers.get('x-ednexus-pubsub-rejected')).toBeNull();
      expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(200);
    });

    it('answers 502 when the call cannot be made at all', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      world.onFetch(() => {
        throw new TypeError('network down');
      });
      expect((await world.request('/api/update-state', postState(token))).status).toBe(502);
    });

    it('answers 503 with the credentials header when Twitch refuses the EBS’s own credentials', async () => {
      const world = makeWorld();
      const { token, channelId } = await world.login();
      world.onFetch(() => new Response('JWT could not be verified', { status: 403 }));
      const response = await world.request('/api/update-state', postState(token));
      expect(response.status).toBe(503);
      expect(response.headers.get('x-ednexus-pubsub-rejected')).toBe('credentials');
      expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(200);

      const health = await (await world.request('/healthz')).json();
      expect(health).toMatchObject({ status: 'ok', pubsub: 'rejected', pubsubStatus: 403 });
    });

    it('does not call a 400 or a 5xx a credentials problem, and a later success clears a rejection', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      world.onFetch(() => new Response('', { status: 403 }));
      await world.request('/api/update-state', postState(token));
      world.clock.advance(5_000);
      world.onFetch(() => new Response('', { status: 400 }));
      expect((await world.request('/api/update-state', postState(token))).headers.get('x-ednexus-pubsub-rejected')).toBeNull();
      expect(await (await world.request('/healthz')).json()).toMatchObject({ pubsub: 'rejected' });

      world.clock.advance(5_000);
      world.onFetch(() => new Response(null, { status: 204 }));
      expect((await world.request('/api/update-state', postState(token))).status).toBe(200);
      expect(await (await world.request('/healthz')).json()).toMatchObject({ pubsub: 'accepted', pubsubStatus: null });
    });
  });

  describe('rate limiting', () => {
    it('allows one publish per window per channel, with Retry-After and no caching on the 429', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      expect((await world.request('/api/update-state', postState(token))).status).toBe(200);

      const limited = await world.request('/api/update-state', postState(token));
      expect(limited.status).toBe(429);
      expect(limited.headers.get('retry-after')).toBe('2');
      expect(limited.headers.get('cache-control')).toBe('no-store');

      world.clock.advance(2_000);
      expect((await world.request('/api/update-state', postState(token))).status).toBe(200);
    });

    it('keeps channels apart', async () => {
      const world = makeWorld();
      const a = await world.login({ twitchUserId: '1' });
      const b = await world.login({ twitchUserId: '2' });
      expect((await world.request('/api/update-state', postState(a.token))).status).toBe(200);
      expect((await world.request('/api/update-state', postState(b.token))).status).toBe(200);
    });

    it('limits an unauthenticated caller per client address, so guessing tokens is not free', async () => {
      const world = makeWorld();
      const guess = { ...postState('guess'), headers: { ...bearer('guess'), 'cf-connecting-ip': '203.0.113.9' } };
      expect((await world.request('/api/update-state', guess)).status).toBe(401);
      expect((await world.request('/api/update-state', guess)).status).toBe(429);
      const other = { ...guess, headers: { ...guess.headers, 'cf-connecting-ip': '203.0.113.10' } };
      expect((await world.request('/api/update-state', other)).status).toBe(401);
    });

    it('gives a clear its own budget, so switching off straight after a publish is never rejected', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      expect((await world.request('/api/update-state', postState(token))).status).toBe(200);
      expect((await world.request('/api/update-state', { method: 'DELETE', headers: bearer(token) })).status).toBe(204);
    });

    it('allows 10 clears a minute, then 429s', async () => {
      const world = makeWorld();
      const { token } = await world.login();
      for (let i = 0; i < 10; i++) expect((await world.request('/api/update-state', { method: 'DELETE', headers: bearer(token) })).status).toBe(204);
      expect((await world.request('/api/update-state', { method: 'DELETE', headers: bearer(token) })).status).toBe(429);
    });
  });

  it('takes the card back down if the token is revoked while the publish is in flight', async () => {
    const world = makeWorld();
    const { token, channelId } = await world.login();
    // The sign-out lands while the PubSub call is out: the token is gone by the time the publish returns.
    world.onFetch(() => {
      world.sqlite.exec('DELETE FROM broadcaster_tokens');
      return new Response(null, { status: 204 });
    });

    const response = await world.request('/api/update-state', postState(token));
    expect(response.status).toBe(401);
    expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(404);
    // the viewers who just got the card are told to hide it again
    const messages = pubsubCalls(world).map((c) => JSON.parse(JSON.parse(c.body).message));
    expect(messages.at(-1)).toEqual({ v: 1, offline: true });
  });
});

describe('DELETE /api/update-state', () => {
  it('forgets the snapshot, tells viewers, and answers 204', async () => {
    const world = makeWorld();
    const { token, channelId } = await world.login();
    await world.request('/api/update-state', postState(token));
    const response = await world.request('/api/update-state', { method: 'DELETE', headers: bearer(token) });
    expect(response.status).toBe(204);
    expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(404);
    expect(JSON.parse(JSON.parse(pubsubCalls(world).at(-1)!.body).message)).toEqual({ v: 1, offline: true });
  });

  it('is idempotent, and fine for a channel that never published', async () => {
    const world = makeWorld();
    const { token } = await world.login();
    expect((await world.request('/api/update-state', { method: 'DELETE', headers: bearer(token) })).status).toBe(204);
    expect((await world.request('/api/update-state', { method: 'DELETE', headers: bearer(token) })).status).toBe(204);
  });

  it('answers 502 with X-EDNexus-Snapshot-Removed when viewers could not be told, though the snapshot is gone', async () => {
    const world = makeWorld();
    const { token, channelId } = await world.login();
    await world.request('/api/update-state', postState(token));
    world.onFetch(() => new Response('', { status: 500 }));
    const response = await world.request('/api/update-state', { method: 'DELETE', headers: bearer(token) });
    expect(response.status).toBe(502);
    expect(response.headers.get('x-ednexus-snapshot-removed')).toBe('true');
    expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(404);
  });

  it('401s without a token', async () => {
    expect((await makeWorld().request('/api/update-state', { method: 'DELETE' })).status).toBe(401);
  });
});

describe('GET /api/initial-state/{channelId}', () => {
  it('404s with a message when nothing was published, and is not cacheable', async () => {
    const response = await makeWorld().request('/api/initial-state/123');
    expect(response.status).toBe(404);
    expect(await response.json()).toEqual({ message: 'No state has been published for this channel yet.' });
    expect(response.headers.get('cache-control')).toBeNull();
  });

  it('serves the snapshot as JSON, cacheable for a few seconds', async () => {
    const world = makeWorld();
    const { token, channelId } = await world.login();
    await world.request('/api/update-state', postState(token, { v: 1, headline: 'x' }));
    const response = await world.request(`/api/initial-state/${channelId}`);
    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toContain('application/json');
    expect(response.headers.get('cache-control')).toBe('public, max-age=5');
    expect(await response.json()).toEqual({ v: 1, headline: 'x' });
  });

  it('can turn caching off', async () => {
    const world = makeWorld({ INITIAL_STATE_CACHE_SECONDS: '0' });
    const { token, channelId } = await world.login();
    await world.request('/api/update-state', postState(token));
    expect((await world.request(`/api/initial-state/${channelId}`)).headers.get('cache-control')).toBeNull();
  });

  it('treats a snapshot older than the maximum age as gone, before the cron has deleted it', async () => {
    const world = makeWorld();
    const { token, channelId } = await world.login();
    await world.request('/api/update-state', postState(token));
    world.clock.advance(23 * 3_600_000);
    expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(200);
    world.clock.advance(2 * 3_600_000);
    expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(404);
  });

  it('fails closed on a row with an unreadable timestamp', async () => {
    const world = makeWorld();
    const { token, channelId } = await world.login();
    await world.request('/api/update-state', postState(token));
    world.sqlite.exec("UPDATE channel_state SET updated_at = 'garbage'");
    expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(404);
  });

  it('keeps a snapshot for ever when the age limit is 0', async () => {
    const world = makeWorld({ CHANNEL_STATE_MAX_AGE_HOURS: '0' });
    const { token, channelId } = await world.login();
    await world.request('/api/update-state', postState(token));
    world.clock.advance(1000 * 3_600_000);
    expect((await world.request(`/api/initial-state/${channelId}`)).status).toBe(200);
  });

  it('is limited per client address through the rate-limit binding, with Retry-After', async () => {
    const seen: string[] = [];
    const world = makeWorld({
      RL_INITIAL_STATE: {
        limit: async ({ key }) => {
          seen.push(key);
          return { success: key !== '198.51.100.1' };
        },
      },
    });
    const limited = await world.request('/api/initial-state/1', { headers: { 'cf-connecting-ip': '198.51.100.1' } });
    expect(limited.status).toBe(429);
    expect(limited.headers.get('retry-after')).toBe('10');
    expect(limited.headers.get('cache-control')).toBe('no-store');
    expect((await world.request('/api/initial-state/1', { headers: { 'cf-connecting-ip': '198.51.100.2' } })).status).toBe(404);
    expect(seen).toEqual(['198.51.100.1', '198.51.100.2']);
  });

  describe('CORS', () => {
    it.each(['https://abc123.ext-twitch.tv', 'https://ext-twitch.tv'])('allows %s and varies by origin', async (origin) => {
      const response = await makeWorld().request('/api/initial-state/1', { headers: { origin } });
      expect(response.headers.get('access-control-allow-origin')).toBe(origin);
      expect(response.headers.get('vary')).toContain('Origin');
      expect(response.headers.get('access-control-expose-headers')).toContain('Retry-After');
    });

    it.each(['https://evil.example', 'http://abc.ext-twitch.tv', 'https://ext-twitch.tv.evil.example', 'https://notext-twitch.tv'])(
      'does not allow %s',
      async (origin) => {
        const response = await makeWorld().request('/api/initial-state/1', { headers: { origin } });
        expect(response.headers.get('access-control-allow-origin')).toBeNull();
      },
    );

    it('allows an origin listed in ADDITIONAL_ALLOWED_FRONTEND_ORIGINS', async () => {
      const world = makeWorld({ ADDITIONAL_ALLOWED_FRONTEND_ORIGINS: 'https://localhost:8080' });
      const response = await world.request('/api/initial-state/1', { headers: { origin: 'https://localhost:8080' } });
      expect(response.headers.get('access-control-allow-origin')).toBe('https://localhost:8080');
    });

    it('answers a preflight for GET from an allowed origin', async () => {
      const response = await makeWorld().request('/api/initial-state/1', {
        method: 'OPTIONS',
        headers: {
          origin: 'https://abc.ext-twitch.tv',
          'access-control-request-method': 'GET',
          'access-control-request-headers': 'x-custom',
        },
      });
      expect(response.status).toBe(204);
      expect(response.headers.get('access-control-allow-origin')).toBe('https://abc.ext-twitch.tv');
      expect(response.headers.get('access-control-allow-methods')).toBe('GET');
      expect(response.headers.get('access-control-allow-headers')).toBe('x-custom');
    });
  });
});

describe('GET / and /healthz', () => {
  it('redirects the bare host to the project site', async () => {
    const response = await makeWorld().request('/');
    expect(response.status).toBe(302);
    expect(response.headers.get('location')).toBe('https://signal-thread.github.io/EDNexus/');
  });

  it('reports ok with pubsub "unknown" until something has been learned', async () => {
    const response = await makeWorld().request('/healthz');
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ status: 'ok', pubsub: 'unknown', pubsubStatus: null, pubsubCheckedAt: null });
  });

  it('refuses to serve with an invalid configuration, naming nothing secret, but still redirects the bare host', async () => {
    const world = makeWorld({ TWITCH_EXTENSION_SECRET: 'not base64!' });
    const error = vi.spyOn(console, 'error').mockImplementation(() => {});
    const response = await world.request('/healthz');
    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({ status: 'misconfigured' });
    expect(error.mock.calls[0]![0]).toContain('TWITCH_EXTENSION_SECRET');
    expect((await world.request('/')).status).toBe(302);
    error.mockRestore();
  });
});
