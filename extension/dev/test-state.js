#!/usr/bin/env node
/*
 * Dependency-free checks for js/state.js (the transport), run under plain Node with a stubbed
 * `window`, fake timers and a scripted fetch. Not part of the Twitch bundle (dev/ is excluded).
 *
 *   node extension/dev/test-state.js
 *
 * Covers: Retry-After handling, exponential backoff with jitter that never gives up, the 'error'
 * status, 404 not retrying, a rendering exception not triggering a refetch, and the EBS override
 * being honoured only for loopback URLs on a loopback page.
 */
'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'js', 'state.js'), 'utf8');

/** Loads state.js into a fresh fake window and returns it with its controls. */
function load({ hostname = 'ednexus-test.ext-twitch.tv', search = '', fetchQueue = [], config = null } = {}) {
  const timers = [];
  let now = 1_000_000;
  const requests = [];

  const win = {
    location: { hostname, search },
    console,
    Date: class extends Date {
      constructor(...args) { super(...(args.length ? args : [now])); }
      static now() { return now; }
    },
    setTimeout(fn, ms) { const t = { at: now + ms, fn, cancelled: false }; timers.push(t); return t; },
    clearTimeout(t) { if (t) t.cancelled = true; },
    setInterval() { return {}; },
    AbortController,
    URL,
    fetch(url) {
      requests.push(url);
      const next = fetchQueue.shift();
      if (!next) return new Promise(() => {}); // hangs; the timeout will settle it
      if (next instanceof Error) return Promise.reject(next);
      return Promise.resolve(next);
    },
  };
  win.window = win;

  const listeners = {};
  let authorized;
  win.Twitch = {
    ext: {
      onAuthorized(cb) { authorized = cb; },
      listen(topic, cb) { listeners[topic] = cb; },
      configuration: { broadcaster: config ? { content: JSON.stringify(config) } : undefined, onChanged() {} },
    },
  };

  vm.createContext(win);
  vm.runInContext(source, win);

  return {
    win,
    state: win.EDNexusState,
    requests,
    listeners,
    authorize: (channelId = '1234') => authorized({ channelId }),
    /** Advances fake time, firing due timers in order. Returns the delays that were scheduled. */
    async advance(ms) {
      const end = now + ms;
      for (;;) {
        const due = timers.filter((t) => !t.cancelled && t.at <= end).sort((a, b) => a.at - b.at)[0];
        if (!due) break;
        due.cancelled = true;
        now = Math.max(now, due.at);
        due.fn();
        await flush();
      }
      now = end;
    },
    pending: () => timers.filter((t) => !t.cancelled).map((t) => t.at - now),
    now: () => now,
  };
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

const json = (status, body, headers = {}) => ({
  status,
  ok: status >= 200 && status < 300,
  headers: { get: (name) => headers[name] ?? headers[name.toLowerCase()] ?? null },
  json: () => Promise.resolve(body),
});

let passed = 0;
async function test(name, fn) {
  try {
    await fn();
    passed++;
    console.log('ok   ' + name);
  } catch (err) {
    console.error('FAIL ' + name + '\n' + (err.stack || err));
    process.exitCode = 1;
  }
}

(async () => {
  await test('Retry-After parses seconds, dates and garbage, and is capped', () => {
    const { state } = load();
    const { retryAfterMs } = state._internals;
    assert.strictEqual(retryAfterMs(json(429, {}, { 'Retry-After': '30' })), 30_000);
    assert.strictEqual(retryAfterMs(json(429, {}, { 'Retry-After': '99999' })), 5 * 60 * 1000);
    assert.strictEqual(retryAfterMs(json(429, {}, {})), 0);
    assert.strictEqual(retryAfterMs(json(429, {}, { 'Retry-After': 'soon' })), 0);
  });

  await test('backoff grows, is jittered, is capped, and respects Retry-After', () => {
    const { backoffDelay } = load().state._internals;
    for (let attempt = 0; attempt < 20; attempt++) {
      const ceiling = Math.min(5 * 60 * 1000, 2000 * 2 ** attempt);
      for (let i = 0; i < 50; i++) {
        const d = backoffDelay(attempt, 0);
        assert.ok(d >= ceiling / 2 && d <= ceiling, `attempt ${attempt}: ${d} not in [${ceiling / 2}, ${ceiling}]`);
      }
    }
    for (let i = 0; i < 50; i++) assert.ok(backoffDelay(0, 30_000) >= 30_000);
  });

  await test('429 with Retry-After waits at least that long, then recovers without giving up', async () => {
    const snapshot = { v: 1, at: 'x', headline: 'Docked at X' };
    const t = load({
      fetchQueue: [
        json(429, {}, { 'Retry-After': '30' }),
        json(503, {}),
        json(503, {}),
        json(503, {}),
        json(503, {}),
        json(200, snapshot),
      ],
    });
    const statuses = [];
    const snapshots = [];
    t.state.connect({ onStatus: (s) => statuses.push(s), onSnapshot: (s) => snapshots.push(s) });
    t.authorize();
    await flush();
    assert.strictEqual(t.requests.length, 1);

    // The 429 asked for 30 s: nothing may happen before then.
    await t.advance(29_000);
    assert.strictEqual(t.requests.length, 1, 'retried before Retry-After elapsed');

    // Then it keeps retrying well past the old 3-attempt limit, and reports 'error' meanwhile.
    for (let i = 0; i < 20 && snapshots.length === 0; i++) await t.advance(6 * 60 * 1000);
    assert.strictEqual(t.requests.length, 6);
    assert.ok(statuses.includes('error'), 'never reported error');
    assert.deepStrictEqual(snapshots, [snapshot]);
    assert.strictEqual(statuses[statuses.length - 1], 'live');
  });

  await test('404 means offline and is not retried', async () => {
    const t = load({ fetchQueue: [json(404, {})] });
    const statuses = [];
    t.state.connect({ onStatus: (s) => statuses.push(s), onSnapshot() {} });
    t.authorize();
    await flush();
    await t.advance(10 * 60 * 1000);
    assert.strictEqual(t.requests.length, 1);
    assert.deepStrictEqual(statuses, ['waiting', 'offline']);
  });

  await test('a render exception does not trigger a refetch', async () => {
    const t = load({ fetchQueue: [json(200, { v: 1, at: 'x' }), json(200, { v: 1, at: 'y' })] });
    const errors = [];
    t.win.console = { error: (...a) => errors.push(a) };
    t.state.connect({ onStatus() {}, onSnapshot() { throw new Error('boom'); } });
    t.authorize();
    await flush();
    await t.advance(10 * 60 * 1000);
    assert.strictEqual(t.requests.length, 1, 'refetched after a render error');
  });

  await test('a live broadcast stops the retry loop', async () => {
    const t = load({ fetchQueue: [json(503, {}), json(503, {})] });
    const snapshots = [];
    t.state.connect({ onStatus() {}, onSnapshot: (s) => snapshots.push(s) });
    t.authorize();
    await flush();
    t.listeners.broadcast('broadcast', 'application/json', JSON.stringify({ v: 1, at: 'live' }));
    await t.advance(10 * 60 * 1000);
    assert.strictEqual(t.requests.length, 1);
    assert.strictEqual(snapshots.length, 1);
  });

  await test('production ignores a configured EBS; loopback pages accept only loopback ones', () => {
    const evil = { ebsBaseUrl: 'https://evil.example' };
    const local = { ebsBaseUrl: 'http://localhost:8787/some/path' };
    const helperFor = (config) => ({ configuration: { broadcaster: { content: JSON.stringify(config) } } });

    const prod = load({ hostname: 'abc.ext-twitch.tv' });
    assert.strictEqual(prod.state._internals.resolveEbsBase(helperFor(evil)), prod.state.DEFAULT_EBS);
    assert.strictEqual(prod.state._internals.resolveEbsBase(helperFor(local)), prod.state.DEFAULT_EBS);

    const dev = load({ hostname: 'localhost' });
    assert.strictEqual(dev.state._internals.resolveEbsBase(helperFor(evil)), dev.state.DEFAULT_EBS);
    assert.strictEqual(dev.state._internals.resolveEbsBase(helperFor(local)), 'http://localhost:8787');
  });

  await test('without the Twitch helper, production draws nothing and mock mode is off', () => {
    const t = load({ hostname: 'abc.ext-twitch.tv', search: '?mock=1' });
    t.win.Twitch = undefined;
    const statuses = [];
    t.state.connect({ onStatus: (s) => statuses.push(s), onSnapshot() {} });
    assert.deepStrictEqual(statuses, ['offline']);
    assert.strictEqual(t.requests.length, 0);
  });

  console.log(`\n${passed} passed${process.exitCode ? ', with failures' : ''}`);
})();
