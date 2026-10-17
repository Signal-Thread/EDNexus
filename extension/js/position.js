/*
 * Where the overlay card sits on the stream, and where that choice is kept.
 *
 * A position is { x, y }, each a fraction 0..1 of the *free* space (the player minus the card): x 0
 * is flush left and 1 flush right, y 0 is the top and 1 the bottom. Fractions, not pixels, so the
 * card stays where it was put through theatre mode, fullscreen and any resize, and can never end up
 * off screen. CSS turns the fraction into a place with no measuring at all:
 *
 *   left: calc(x * 100%);  transform: translateX(calc(x * -100%));
 *
 * Two places can say where the card goes, and the viewer's own choice always wins:
 *   1. the viewer's, kept in their browser (localStorage) when they move the card;
 *   2. the broadcaster's default, in Twitch's extension configuration service ("broadcaster"
 *      segment), set from the config page so the card does not start on top of their webcam.
 * With neither, the card sits on the left edge, halfway down: where it has always been.
 *
 * Pure functions, no `document`, so dev/test-views.js can run them under Node. Everything read from
 * storage or configuration is validated here: the broadcaster controls that document and the viewer
 * controls their own storage, and neither may be able to put NaN, a string or an off-screen number
 * into the layout.
 */
(function (global) {
  'use strict';

  var DEFAULT = { x: 0, y: 0.5 };

  /** localStorage key for the viewer's own choice. */
  var STORAGE_KEY = 'ednexus.card.pos';

  /** Within this fraction of an edge a released card snaps flush to it. */
  var SNAP = 0.03;

  /** Keyboard step, as a fraction of the free space; Shift takes the larger one. */
  var STEP = 0.02;
  var BIG_STEP = 0.1;

  function isFraction(value) {
    return typeof value === 'number' && isFinite(value) && value >= 0 && value <= 1;
  }

  function clamp01(value) {
    return Math.max(0, Math.min(1, value));
  }

  /** Four decimals is far finer than a pixel and keeps the stored value short and stable. */
  function round(value) {
    return Math.round(value * 10000) / 10000;
  }

  /** A valid position, or null for anything else (a missing, malformed or out-of-range value). */
  function normalize(value) {
    if (!value || typeof value !== 'object') return null;
    if (!isFraction(value.x) || !isFraction(value.y)) return null;
    return { x: round(value.x), y: round(value.y) };
  }

  /**
   * The position that puts the card's top-left corner at (left, top), given the free space around it
   * (player size minus card size). No free space on an axis pins it to 0.
   */
  function fromPixels(left, top, freeWidth, freeHeight) {
    return {
      x: round(freeWidth > 0 ? clamp01(left / freeWidth) : 0),
      y: round(freeHeight > 0 ? clamp01(top / freeHeight) : 0),
    };
  }

  /**
   * Where a card let go near an edge or the middle ends up: flush to a side within SNAP of it, and
   * flush to the top, middle or bottom likewise. Makes "back at the edge" and "centred" easy to hit.
   */
  function snap(p) {
    var x = p.x < SNAP ? 0 : (p.x > 1 - SNAP ? 1 : p.x);
    var y = p.y;
    if (y < SNAP) y = 0;
    else if (y > 1 - SNAP) y = 1;
    else if (Math.abs(y - 0.5) < SNAP) y = 0.5;
    return { x: round(x), y: round(y) };
  }

  /** The position after a keyboard step of (dx, dy) in units of `size` (STEP or BIG_STEP), clamped. */
  function step(p, dx, dy, big) {
    var size = big ? BIG_STEP : STEP;
    return { x: round(clamp01(p.x + dx * size)), y: round(clamp01(p.y + dy * size)) };
  }

  /**
   * Which edge the card is flush against, for styling: the overlay's accent bar and rounded corners
   * are drawn differently against a side than floating free.
   */
  function edgeFor(p) {
    if (p.x <= 0.005) return 'left';
    if (p.x >= 0.995) return 'right';
    return 'float';
  }

  /** A plain-words description, for the config page and for screen readers ("Right edge, top"). */
  function describe(p) {
    var horizontal;
    if (p.x <= SNAP) horizontal = 'Left edge';
    else if (p.x >= 1 - SNAP) horizontal = 'Right edge';
    else if (p.x < 1 / 3) horizontal = 'Left';
    else if (p.x > 2 / 3) horizontal = 'Right';
    else horizontal = 'Centre';

    var vertical = p.y < 1 / 3 ? 'top' : (p.y > 2 / 3 ? 'bottom' : 'middle');
    return horizontal + ', ' + vertical;
  }

  /* ---------------------------------------------------- broadcaster default -- */

  function parseObject(raw) {
    if (!raw || typeof raw !== 'string') return null;
    try {
      var parsed = JSON.parse(raw);
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : null;
    } catch (err) {
      return null;
    }
  }

  /** The broadcaster's default position from the configuration document's text, or null. */
  function parseConfig(raw) {
    var doc = parseObject(raw);
    return doc ? normalize(doc.position) : null;
  }

  /**
   * The configuration document's text after applying `changes` to what is already there. The
   * document is shared (the development form keeps `ebsBaseUrl` in it too), so every save has to
   * keep the keys it did not touch. A `null` or `undefined` change removes that key; a `position`
   * that is not valid is removed rather than stored.
   */
  function configWith(existingRaw, changes) {
    var doc = parseObject(existingRaw) || {};
    Object.keys(changes).forEach(function (key) {
      var value = changes[key];
      if (key === 'position') value = normalize(value);
      if (value === null || value === undefined) delete doc[key];
      else doc[key] = value;
    });
    return JSON.stringify(doc);
  }

  /* ------------------------------------------------------- viewer's choice -- */

  /** The viewer's saved position, or null. Storage can be missing or throw in an embedded iframe. */
  function load(storage) {
    try {
      return normalize(JSON.parse(storage.getItem(STORAGE_KEY)));
    } catch (err) {
      return null;
    }
  }

  function save(storage, p) {
    try { storage.setItem(STORAGE_KEY, JSON.stringify(p)); } catch (err) { /* the card still moves; it just will not be remembered */ }
  }

  function clear(storage) {
    try { storage.removeItem(STORAGE_KEY); } catch (err) { /* nothing was remembered anyway */ }
  }

  /**
   * Development only: `?default=0.9,0.1` stands in for a broadcaster default when there is no Twitch
   * configuration service to supply one. The caller must only use it on a loopback page.
   */
  function parseDevDefault(search) {
    var match = /[?&]default=([0-9.]+),([0-9.]+)/.exec(search || '');
    return match ? normalize({ x: parseFloat(match[1]), y: parseFloat(match[2]) }) : null;
  }

  global.EDNexusPosition = {
    DEFAULT: DEFAULT,
    STORAGE_KEY: STORAGE_KEY,
    normalize: normalize,
    fromPixels: fromPixels,
    snap: snap,
    step: step,
    edgeFor: edgeFor,
    describe: describe,
    parseConfig: parseConfig,
    configWith: configWith,
    load: load,
    save: save,
    clear: clear,
    parseDevDefault: parseDevDefault,
  };
})(window);
