#!/usr/bin/env node
/*
 * Dependency-free checks for the parts of the commander card every view shares: js/model.js (what a
 * viewer sees in a given state, staleness, the compact summary, per-view layout) and the structure of
 * the view entry pages. Run under plain Node with a stubbed `window`. Not part of the Twitch bundle
 * (dev/ is excluded).
 *
 *   node extension/dev/test-views.js
 *
 * The transport has its own checks in test-state.js.
 */
'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const root = path.join(__dirname, '..');
const read = (file) => fs.readFileSync(path.join(root, file), 'utf8');

const win = { console };
win.window = win;
vm.createContext(win);
vm.runInContext(read('js/model.js'), win);
const M = win.EDNexusModel;

const sample = JSON.parse(read('dev/sample-state.json'));
const STALE_AFTER = 25 * 60 * 1000;

let passed = 0;
function test(name, fn) {
  try {
    fn();
    passed++;
    console.log(`  ok   ${name}`);
  } catch (err) {
    process.exitCode = 1;
    console.log(`  FAIL ${name}\n       ${err.message}`);
  }
}

/* ---------------------------------------------------------- what is shown -- */

test('a live snapshot is the card, and is visible', () => {
  const v = M.deriveView('live', sample, false);
  assert.strictEqual(v.kind, 'card');
  assert.strictEqual(v.visible, true);
  assert.strictEqual(v.snapshot, sample);
});

test('a newer schema is a notice, never a half-parsed card', () => {
  const v = M.deriveView('live', { unsupported: true }, false);
  assert.strictEqual(v.kind, 'unsupported');
  assert.strictEqual(v.visible, true);
  assert.strictEqual(v.snapshot, null, 'the old snapshot must not survive under the notice');
  assert.match(v.message, /newer EDNexus/);
});

test('an unreachable service shows a retrying message', () => {
  const v = M.deriveView('error', null, false);
  assert.strictEqual(v.kind, 'error');
  assert.strictEqual(v.visible, true);
  assert.match(v.message, /Trying again/);
});

test('connecting stays invisible until the loading delay has passed', () => {
  assert.strictEqual(M.deriveView('waiting', null, false).visible, false);
  assert.strictEqual(M.deriveView('waiting', null, true).visible, true);
  assert.strictEqual(M.deriveView('waiting', null, true).kind, 'connecting');
});

test('offline, an unknown status and no snapshot are all idle and invisible', () => {
  for (const [status, snapshot] of [['offline', null], [null, null], ['live', null], ['bogus', sample]]) {
    const v = M.deriveView(status, snapshot, true);
    assert.strictEqual(v.kind, 'idle', `${status}/${snapshot}`);
    assert.strictEqual(v.visible, false);
  }
});

test('idle clears the live region; a plain card leaves it alone', () => {
  assert.strictEqual(M.deriveView('offline', null, false).announce, '');
  assert.strictEqual(M.deriveView('live', sample, false).announce, null);
});

/* ------------------------------------------------------------- staleness -- */

test('a fresh card is not stale; one older than the threshold is', () => {
  const at = Date.parse(sample.at);
  const view = M.deriveView('live', sample, false);
  assert.strictEqual(M.staleness(view, at + 60 * 1000, STALE_AFTER).stale, false);
  const stale = M.staleness(view, at + STALE_AFTER + 1000, STALE_AFTER);
  assert.strictEqual(stale.stale, true);
  assert.match(stale.message, /^Last update \d+ min ago$/);
});

test('a card with no timestamp is stale, not fresh', () => {
  const view = M.deriveView('live', { v: 1 }, false);
  const s = M.staleness(view, Date.now(), STALE_AFTER);
  assert.strictEqual(s.stale, true);
  assert.strictEqual(s.message, 'Last update unknown');
});

test('only a card can be stale', () => {
  for (const view of [M.deriveView('error', null), M.deriveView('offline', null), M.deriveView('live', { unsupported: true })]) {
    assert.strictEqual(M.staleness(view, Date.now() + 1e12, STALE_AFTER).stale, false, view.kind);
  }
});

/* ------------------------------------------------------- compact summary -- */

test('the compact summary carries the place, ship, fuel and one rank', () => {
  const c = M.compact(sample);
  assert.strictEqual(c.cmdr, 'CMDR Hadfield');
  assert.strictEqual(c.where, 'Nervi / Nervi 2 a');
  assert.strictEqual(c.shipType, 'Krait Phantom');
  assert.ok(Math.abs(c.fuel.fraction - 9.4 / 32) < 1e-9);
  assert.strictEqual(c.fuel.low, false);
  assert.strictEqual(c.rank.label, 'Combat');
  assert.strictEqual(c.rank.fraction, 0.62);
});

test('low fuel is flagged below a quarter of the tank', () => {
  assert.strictEqual(M.compact({ ship: { fuel: 7, fuelMax: 32 } }).fuel.low, true);
  assert.strictEqual(M.compact({ ship: { fuel: 8, fuelMax: 32 } }).fuel.low, false);
});

test('a missing section leaves its part out instead of printing a gap', () => {
  const c = M.compact({ headline: 'Sol' });
  assert.strictEqual(c.cmdr, 'CMDR');
  assert.strictEqual(c.where, 'Sol');
  assert.strictEqual(c.shipType, null);
  assert.strictEqual(c.fuel, null);
  assert.strictEqual(c.rank, null);
});

test('garbage never throws and falls back to a default place', () => {
  for (const bad of [null, undefined, 5, 'x', [], { ship: 5, cmdr: 'x', loc: [] }, { ship: { fuel: 'a', fuelMax: 0 } }]) {
    const c = M.compact(bad);
    assert.strictEqual(c.where, 'Elite Dangerous');
    assert.strictEqual(c.fuel, null);
  }
});

test('the rank shown is the first with progress; a maxed ladder has no bar', () => {
  const ranks = [
    { label: 'Combat', name: 'Elite', pct: 0, elite: true },
    { label: 'Trade', name: 'Tycoon', pct: 18 },
    null,
    { label: '', name: 'x', pct: 50 },
  ];
  const withProgress = M.compact({ cmdr: { ranks } }).rank;
  assert.strictEqual(withProgress.label, 'Trade');
  assert.strictEqual(withProgress.fraction, 0.18);

  const maxedOnly = M.compact({ cmdr: { ranks: [ranks[0]] } }).rank;
  assert.strictEqual(maxedOnly.label, 'Combat');
  assert.strictEqual(maxedOnly.elite, true);
  assert.strictEqual(maxedOnly.fraction, null);
});

test('rank progress over 100% is clamped', () => {
  assert.strictEqual(M.compact({ cmdr: { ranks: [{ label: 'a', name: 'b', pct: 250 }] } }).rank.fraction, 1);
});

/* ------------------------------------------------------ per-view layout -- */

test('overlay and component hide when idle; panel and mobile do not', () => {
  assert.strictEqual(M.layoutFor('overlay').hideWhenIdle, true);
  assert.strictEqual(M.layoutFor('component').hideWhenIdle, true);
  assert.strictEqual(M.layoutFor('panel').hideWhenIdle, false);
  assert.strictEqual(M.layoutFor('mobile').hideWhenIdle, false);
});

test('only the mobile view folds its sections', () => {
  assert.strictEqual(M.layoutFor('mobile').fold, true);
  assert.strictEqual(M.layoutFor('mobile').openFolds, 2);
  for (const name of ['overlay', 'component', 'panel']) assert.strictEqual(M.layoutFor(name).fold, false, name);
});

test('an unknown or missing view name gets the overlay layout, which shows nothing by default', () => {
  for (const name of [undefined, null, '', 'tablet', '__proto__', 'constructor', 'toString']) {
    assert.deepStrictEqual(M.layoutFor(name), M.layoutFor('overlay'), String(name));
  }
});

test('layoutFor returns a copy, so a view cannot change another view\'s layout', () => {
  M.layoutFor('panel').hideWhenIdle = true;
  assert.strictEqual(M.layoutFor('panel').hideWhenIdle, false);
});

test('fold state: the first N sections start open, and a viewer\'s choice survives a re-render', () => {
  const titles = ['Location', 'Ship', 'Commander', 'Cargo hold'];
  const first = M.foldState(titles, {}, 2);
  // Compared through JSON: objects built inside the vm context have another realm's prototype.
  assert.deepStrictEqual(JSON.parse(JSON.stringify(first)), { Location: true, Ship: true, Commander: false, 'Cargo hold': false });

  // The viewer closes Ship and opens Cargo hold; a new snapshot arrives with a section added.
  const edited = Object.assign({}, first, { Ship: false, 'Cargo hold': true });
  const next = M.foldState(['Location', 'Ship', 'Exobiology', 'Cargo hold'], edited, 2);
  assert.strictEqual(next.Ship, false, 'a closed section was reopened by the re-render');
  assert.strictEqual(next['Cargo hold'], true, 'an opened section was closed by the re-render');
  assert.strictEqual(next.Exobiology, false, 'a new section past the first N should start closed');
  assert.ok(!('Commander' in next), 'a section that left the card should leave the state');
});

/* ------------------------------------------------------------ entry pages -- */

// Which entry page is which view, and the scripts each must load, in order. state.js first, then the
// helper-dependent layers: model -> render -> session -> the view's own shell.
const SHARED = ['js/state.js', 'js/model.js', 'js/render.js', 'js/session.js'];
const VIEWS = {
  'video_overlay.html': { shell: 'js/card.js', css: 'css/card.css' },
  'panel.html': { shell: 'js/page.js', css: 'css/page.css', dataView: 'panel' },
  'mobile.html': { shell: 'js/page.js', css: 'css/mobile.css', dataView: 'mobile' },
  // The video component is a small box with no footer mark, so it has nothing to link.
  'component.html': { shell: 'js/component.js', css: 'css/component.css', brand: false },
};

// The extension's only link out is to the project site, opened in a new tab without passing the
// channel page along (mirrors the "Check the bundle" step in .github/workflows/extension-package.yml).
const SITE = 'https://signal-thread.github.io/EDNexus/';

function assertOnlyProjectLinks(html) {
  for (const tag of html.match(/<a\s[^>]*>/g) || []) {
    assert.ok(tag.includes(`href="${SITE}"`), `link to somewhere else: ${tag}`);
    assert.ok(tag.includes('target="_blank"'), `link without target="_blank": ${tag}`);
    assert.ok(tag.includes('rel="noopener noreferrer"'), `link without rel="noopener noreferrer": ${tag}`);
  }
  for (const line of html.split(/\r?\n/)) {
    if (/\starget=/.test(line)) assert.ok(/<a\s[^>]*\btarget="_blank"/.test(line), `target= on a non-link: ${line.trim()}`);
  }
}

test('the config page links to the project site and nowhere else', () => {
  const html = read('config.html');
  assertOnlyProjectLinks(html);
  assert.ok(/<a\s[^>]*class="ednx-link"/.test(html), 'no project-site link on the config page');
});

for (const [file, spec] of Object.entries(VIEWS)) {
  const html = read(file);
  const scripts = [...html.matchAll(/<script\s+src="([^"]+)"/g)].map((m) => m[1]);

  test(`${file}: loads the Twitch helper first, then the shared layers, then its shell`, () => {
    assert.match(scripts[0], /twitch-ext\.min\.js$/);
    assert.deepStrictEqual(scripts.slice(1), [...SHARED, spec.shell]);
  });

  test(`${file}: every local script and stylesheet exists`, () => {
    const hrefs = [...html.matchAll(/(?:href|src)="((?:css|js)\/[^"]+)"/g)].map((m) => m[1]);
    assert.ok(hrefs.includes(spec.css), `does not link ${spec.css}`);
    for (const h of hrefs) assert.ok(fs.existsSync(path.join(root, h)), `${h} does not exist`);
  });

  test(`${file}: no inline script or style`, () => {
    assert.ok(!/<script(?![^>]*\bsrc=)/.test(html), 'inline <script>');
    assert.ok(!/<style/.test(html), 'inline <style>');
    assert.ok(!/\sstyle=/.test(html), 'style= attribute');
  });

  test(`${file}: the EDNexus mark links to the project site and nowhere else is linked`, () => {
    assertOnlyProjectLinks(html);
    if (spec.brand === false) return;
    const brand = html.match(/<a\s[^>]*class="ednx-brand"[^>]*>EDNexus<\/a>/);
    assert.ok(brand, 'the footer mark is not the project-site link');
  });

  if (spec.dataView) {
    test(`${file}: data-view names a real layout`, () => {
      assert.ok(html.includes(`data-view="${spec.dataView}"`));
      assert.strictEqual(M.layoutFor(spec.dataView).hideWhenIdle, false, 'panel/mobile must show a note when idle');
    });
  }
}

test('no stylesheet picks a colour by hand (AGENTS.md rule 8)', () => {
  for (const css of fs.readdirSync(path.join(root, 'css')).filter((f) => f !== 'tokens.css')) {
    const source = read(`css/${css}`).replace(/\/\*[\s\S]*?\*\//g, '');
    assert.ok(!/#[0-9a-fA-F]{3,8}\b/.test(source), `${css} has a hex colour`);
    assert.ok(!/\b(?:rgb|rgba|hsl|hsla)\(/.test(source), `${css} has a literal colour function`);
  }
});

console.log(`\n${passed} passed${process.exitCode ? ', with failures' : ''}`);
