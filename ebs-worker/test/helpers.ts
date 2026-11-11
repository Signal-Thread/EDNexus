import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { DatabaseSync, type SQLInputValue } from 'node:sqlite';
import { vi } from 'vitest';
import { createApp } from '../src/app';
import { pkceChallenge } from '../src/crypto';
import type { Env } from '../src/env';

// ---------------------------------------------------------------------------------------------
// A D1 stand-in over real SQLite (node:sqlite), so the tests run the Worker's actual SQL — upserts,
// RETURNING, the real migration — rather than asserting on recorded strings.
// ---------------------------------------------------------------------------------------------

class FakeStatement {
  constructor(
    private readonly sqlite: DatabaseSync,
    readonly sql: string,
    private readonly params: SQLInputValue[] = [],
  ) {}

  bind(...params: SQLInputValue[]): FakeStatement {
    return new FakeStatement(this.sqlite, this.sql, params);
  }

  async first<T>(): Promise<T | null> {
    return (this.sqlite.prepare(this.sql).get(...this.params) as T | undefined) ?? null;
  }

  async all<T>(): Promise<{ results: T[] }> {
    return { results: this.sqlite.prepare(this.sql).all(...this.params) as T[] };
  }

  async run(): Promise<{ success: true; results: []; meta: { changes: number } }> {
    const result = this.sqlite.prepare(this.sql).run(...this.params);
    return { success: true, results: [], meta: { changes: Number(result.changes) } };
  }
}

class FakeD1 {
  constructor(readonly sqlite: DatabaseSync) {}

  prepare(sql: string): FakeStatement {
    return new FakeStatement(this.sqlite, sql);
  }

  async batch(statements: FakeStatement[]) {
    this.sqlite.exec('BEGIN');
    try {
      const results = [];
      for (const statement of statements) results.push(await statement.run());
      this.sqlite.exec('COMMIT');
      return results;
    } catch (error) {
      this.sqlite.exec('ROLLBACK');
      throw error;
    }
  }
}

const migration = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../migrations/0001_init.sql'), 'utf8');

export const b64 = (bytes: number, fill: number) => Buffer.alloc(bytes, fill).toString('base64');

export interface TestWorld {
  env: Env;
  sqlite: DatabaseSync;
  clock: { now: () => Date; advance: (ms: number) => void; set: (iso: string) => void };
  app: ReturnType<typeof createApp>;
  fetchCalls: FetchCall[];
  /** Replaces the handler for outbound fetches (Twitch). Default answers every Twitch endpoint happily. */
  onFetch: (handler: (call: FetchCall) => Response | Promise<Response>) => void;
  request: (path: string, init?: RequestInit) => Promise<Response>;
  /** Runs the whole OAuth login and returns the issued EBS token and channel id. */
  login: (options?: { twitchUserId?: string }) => Promise<{ token: string; channelId: string }>;
}

export interface FetchCall {
  url: string;
  method: string;
  headers: Headers;
  body: string;
}

export const EXTENSION_SECRET = b64(32, 7);
export const ENCRYPTION_KEY = b64(32, 9);

export function makeEnv(overrides: Partial<Env> = {}): { env: Env; sqlite: DatabaseSync } {
  const sqlite = new DatabaseSync(':memory:');
  sqlite.exec(migration);
  const env = {
    DB: new FakeD1(sqlite) as unknown as D1Database,
    TWITCH_CLIENT_ID: 'client-id',
    TWITCH_CLIENT_SECRET: 'client-secret',
    TWITCH_EXTENSION_ID: 'extension-id',
    TWITCH_EXTENSION_SECRET: EXTENSION_SECRET,
    TOKEN_ENCRYPTION_KEY: ENCRYPTION_KEY,
    TWITCH_OAUTH_REDIRECT_URI: 'https://ebs.example.test/oauth/callback',
    ...overrides,
  } satisfies Env;
  return { env, sqlite };
}

const DESKTOP_REDIRECT = 'http://127.0.0.1:53682/callback';

function defaultTwitch(call: FetchCall): Response {
  if (call.url.startsWith('https://id.twitch.tv/oauth2/token'))
    return Response.json({ access_token: 'tw-access', refresh_token: 'tw-refresh', expires_in: 14400 });
  if (call.url.startsWith('https://api.twitch.tv/helix/users'))
    return Response.json({ data: [{ id: '4242', login: 'cmdr', display_name: 'Cmdr' }] });
  if (call.url.startsWith('https://api.twitch.tv/helix/extensions/pubsub')) return new Response(null, { status: 204 });
  if (call.url.startsWith('https://id.twitch.tv/oauth2/revoke')) return new Response(null, { status: 200 });
  return new Response('unexpected request in test', { status: 500 });
}

export function makeWorld(overrides: Partial<Env> = {}): TestWorld {
  const { env, sqlite } = makeEnv(overrides);

  let time = Date.parse('2026-11-10T12:00:00.000Z');
  const clock = {
    now: () => new Date(time),
    advance: (ms: number) => void (time += ms),
    set: (isoText: string) => void (time = Date.parse(isoText)),
  };

  const fetchCalls: FetchCall[] = [];
  let handler: (call: FetchCall) => Response | Promise<Response> = defaultTwitch;
  vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
    const call: FetchCall = {
      url: typeof input === 'string' ? input : input instanceof URL ? input.href : input.url,
      method: init?.method ?? 'GET',
      headers: new Headers(init?.headers),
      body: init?.body instanceof URLSearchParams ? init.body.toString() : String(init?.body ?? ''),
    };
    fetchCalls.push(call);
    return handler(call);
  });

  const app = createApp({ now: clock.now });
  const request = (path: string, init?: RequestInit) => Promise.resolve(app.request(path, init, env));

  async function login(options: { twitchUserId?: string } = {}) {
    if (options.twitchUserId) {
      const previous = handler;
      handler = (call) =>
        call.url.startsWith('https://api.twitch.tv/helix/users')
          ? Response.json({ data: [{ id: options.twitchUserId, login: 'cmdr', display_name: 'Cmdr' }] })
          : previous(call);
    }
    const verifier = 'v'.repeat(64);
    const challenge = await pkceChallenge(verifier);
    const authorize = await request(
      `/oauth/authorize?${new URLSearchParams({ redirect_uri: DESKTOP_REDIRECT, state: 'desktop-state', code_challenge: challenge, code_challenge_method: 'S256' })}`,
    );
    const twitchUrl = new URL(authorize.headers.get('location')!);
    const sessionId = twitchUrl.searchParams.get('state')!;
    const callback = await request(`/oauth/callback?code=twitch-code&state=${encodeURIComponent(sessionId)}`);
    const code = new URL(callback.headers.get('location')!).searchParams.get('code')!;
    const tokenResponse = await request('/oauth/token', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ code, code_verifier: verifier, redirect_uri: DESKTOP_REDIRECT }),
    });
    const body = (await tokenResponse.json()) as { token: string; channelId: string };
    return { token: body.token, channelId: body.channelId };
  }

  return {
    env,
    sqlite,
    clock,
    app,
    fetchCalls,
    onFetch: (h) => void (handler = h),
    request,
    login,
  };
}

export const DESKTOP_REDIRECT_URI = DESKTOP_REDIRECT;

export const bearer = (token: string) => ({ authorization: `Bearer ${token}` });

export const postState = (token: string, state: unknown = { v: 1, headline: 'hello' }, extra: Record<string, string> = {}): RequestInit => ({
  method: 'POST',
  headers: { 'content-type': 'application/json', ...bearer(token), ...extra },
  body: JSON.stringify({ state }),
});

export const pubsubCalls = (world: TestWorld) => world.fetchCalls.filter((c) => c.url.startsWith('https://api.twitch.tv/helix/extensions/pubsub'));
