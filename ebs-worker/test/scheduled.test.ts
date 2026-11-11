import { afterEach, describe, expect, it, vi } from 'vitest';
import { CRON_CREDENTIAL_CHECK, CRON_PRUNE, CRON_REFRESH, runScheduled } from '../src/scheduled';
import { INVALID_TARGET } from '../src/twitch/probe';
import { makeWorld, postState } from './helpers';

afterEach(() => vi.unstubAllGlobals());

const run = (world: ReturnType<typeof makeWorld>, cron: string) => runScheduled(world.env, cron, world.clock.now);
const grant = (world: ReturnType<typeof makeWorld>) =>
  world.sqlite.prepare('SELECT is_twitch_grant_valid AS valid, twitch_expires_at AS expires, last_refreshed_at AS refreshed FROM broadcaster_tokens').get() as {
    valid: number;
    expires: string;
    refreshed: string | null;
  };

describe('prune cron', () => {
  it('deletes snapshots past the limit, expired pending logins and old counters, and nothing else', async () => {
    const world = makeWorld();
    const { token, channelId } = await world.login();
    await world.request('/api/update-state', postState(token));
    await world.request('/oauth/authorize?redirect_uri=http%3A%2F%2Flocalhost%2Fcb&state=s&code_challenge=' + 'a'.repeat(43));

    await run(world, CRON_PRUNE);
    expect(world.sqlite.prepare('SELECT COUNT(*) AS n FROM channel_state').get()).toEqual({ n: 1 });
    expect(world.sqlite.prepare('SELECT COUNT(*) AS n FROM oauth_pending').get()).toEqual({ n: 1 });

    world.clock.advance(25 * 3_600_000);
    await run(world, CRON_PRUNE);
    expect(world.sqlite.prepare('SELECT COUNT(*) AS n FROM channel_state').get()).toEqual({ n: 0 });
    expect(world.sqlite.prepare('SELECT COUNT(*) AS n FROM oauth_pending').get()).toEqual({ n: 0 });
    expect(world.sqlite.prepare('SELECT COUNT(*) AS n FROM rate_limit').get()).toEqual({ n: 0 });
    // the broadcaster's token is not a cache: it stays
    expect(world.sqlite.prepare('SELECT channel_id FROM broadcaster_tokens').get()).toEqual({ channel_id: channelId });
  });
});

describe('refresh cron', () => {
  it('refreshes only grants inside the expiry buffer, and stores the new tokens encrypted', async () => {
    const world = makeWorld();
    await world.login();
    await run(world, CRON_REFRESH); // 4h left, buffer is 1h
    expect(world.fetchCalls.filter((c) => c.url === 'https://id.twitch.tv/oauth2/token')).toHaveLength(1); // only the login's exchange

    world.clock.advance(3.5 * 3_600_000); // 30 minutes left
    world.onFetch(() => Response.json({ access_token: 'new-access', refresh_token: 'new-refresh', expires_in: 14400 }));
    await run(world, CRON_REFRESH);

    const refresh = world.fetchCalls.at(-1)!;
    expect(new URLSearchParams(refresh.body).get('grant_type')).toBe('refresh_token');
    expect(new URLSearchParams(refresh.body).get('refresh_token')).toBe('tw-refresh');
    expect(grant(world).valid).toBe(1);
    expect(Date.parse(grant(world).expires)).toBe(world.clock.now().getTime() + 14400 * 1000);
    expect(JSON.stringify(world.sqlite.prepare('SELECT * FROM broadcaster_tokens').all())).not.toContain('new-access');
  });

  it('keeps the old refresh token when Twitch does not send a new one', async () => {
    const world = makeWorld();
    await world.login();
    world.clock.advance(4 * 3_600_000);
    world.onFetch(() => Response.json({ access_token: 'a2', expires_in: 100 }));
    await run(world, CRON_REFRESH);
    world.onFetch(() => Response.json({ access_token: 'a3', refresh_token: 'r3', expires_in: 100 }));
    world.clock.advance(3_600_000);
    await run(world, CRON_REFRESH);
    expect(new URLSearchParams(world.fetchCalls.at(-1)!.body).get('refresh_token')).toBe('tw-refresh');
  });

  it('marks a grant invalid when Twitch rejects the refresh token, so the next publish 401s', async () => {
    const world = makeWorld();
    const { token } = await world.login();
    world.clock.advance(4 * 3_600_000);
    world.onFetch(() => new Response('{"message":"Invalid refresh token"}', { status: 400 }));
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    await run(world, CRON_REFRESH);
    expect(grant(world).valid).toBe(0);

    world.onFetch(() => new Response(null, { status: 204 }));
    expect((await world.request('/api/update-state', postState(token))).status).toBe(401);
    // an invalid grant is not retried every run
    const calls = world.fetchCalls.length;
    await run(world, CRON_REFRESH);
    expect(world.fetchCalls).toHaveLength(calls);
  });

  it.each([
    ['a Twitch 500', () => new Response('', { status: 500 })],
    ['a 429', () => new Response('', { status: 429 })],
    ['"invalid client" (a rotated secret of OURS)', () => new Response('{"message":"invalid client secret"}', { status: 400 })],
    ['a network error', () => {
      throw new TypeError('network down');
    }],
    ['an unparseable body', () => new Response('<html>', { status: 200 })],
  ])('leaves the grant valid to retry after %s, and never logs anyone out', async (_name, respond) => {
    const world = makeWorld();
    await world.login();
    world.clock.advance(4 * 3_600_000);
    world.onFetch(respond);
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    await run(world, CRON_REFRESH);
    expect(grant(world).valid).toBe(1);
  });

  it('one broadcaster failing does not stop the others', async () => {
    const world = makeWorld();
    await world.login({ twitchUserId: '1' });
    await world.login({ twitchUserId: '2' });
    world.clock.advance(4 * 3_600_000);
    let first = true;
    world.onFetch(() => {
      if (first) {
        first = false;
        throw new TypeError('blip');
      }
      return Response.json({ access_token: 'a', refresh_token: 'r', expires_in: 100 });
    });
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    await run(world, CRON_REFRESH);
    const rows = world.sqlite.prepare('SELECT last_refreshed_at AS r FROM broadcaster_tokens ORDER BY channel_id').all() as { r: string | null }[];
    expect(rows.filter((r) => r.r !== null)).toHaveLength(1);
  });
});

describe('credential check cron', () => {
  const check = async (response: () => Response) => {
    const world = makeWorld({ TWITCH_OWNER_USER_ID: '777' });
    world.onFetch(response);
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.spyOn(console, 'log').mockImplementation(() => {});
    await run(world, CRON_CREDENTIAL_CHECK);
    return { world, health: await (await world.request('/healthz')).json() };
  };

  it('sends a request signed like a broadcast but aimed at a target nothing can receive', async () => {
    const { world } = await check(() => new Response('', { status: 400 }));
    const call = world.fetchCalls[0]!;
    expect(JSON.parse(call.body)).toEqual({ broadcaster_id: '777', target: [INVALID_TARGET], message: '{}' });
    expect(call.headers.get('client-id')).toBe('extension-id');
    expect(call.headers.get('authorization')).toMatch(/^Bearer [\w-]+\.[\w-]+\.[\w-]+$/);
  });

  it.each([
    ['a 400 (the bogus target was refused)', 400],
    ['a 403 "Permission denied to use target" (valid credentials, bogus target)', 403],
  ])('records "accepted" for %s', async (_n, status) => {
    const { health } = await check(() => new Response(status === 403 ? 'Permission denied to use target' : '', { status }));
    expect(health).toMatchObject({ pubsub: 'accepted' });
  });

  it.each([401, 403])('records "rejected" for a %i that is not the permission answer, and logs it as an error', async (status) => {
    const { health } = await check(() => new Response('JWT could not be verified', { status }));
    expect(health).toMatchObject({ pubsub: 'rejected', pubsubStatus: status });
    expect(console.error).toHaveBeenCalled();
  });

  it.each([
    ['a 500', () => new Response('', { status: 500 })],
    ['a 429', () => new Response('', { status: 429 })],
    ['a network error', () => {
      throw new TypeError('down');
    }],
  ])('says nothing after %s', async (_n, respond) => {
    const { health } = await check(respond);
    expect(health).toMatchObject({ pubsub: 'unknown' });
  });

  it('does not overwrite a rejection with an inconclusive answer', async () => {
    const world = makeWorld();
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    vi.spyOn(console, 'error').mockImplementation(() => {});
    world.onFetch(() => new Response('', { status: 403 }));
    await run(world, CRON_CREDENTIAL_CHECK);
    world.onFetch(() => new Response('', { status: 503 }));
    await run(world, CRON_CREDENTIAL_CHECK);
    expect(await (await world.request('/healthz')).json()).toMatchObject({ pubsub: 'rejected' });
  });
});

describe('scheduled dispatch', () => {
  it('ignores an unknown cron and an invalid configuration without throwing', async () => {
    const world = makeWorld();
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    await expect(run(world, '1 1 1 1 1')).resolves.toBeUndefined();
    const broken = makeWorld({ TOKEN_ENCRYPTION_KEY: '' });
    vi.spyOn(console, 'error').mockImplementation(() => {});
    await expect(run(broken, CRON_REFRESH)).resolves.toBeUndefined();
  });
});
