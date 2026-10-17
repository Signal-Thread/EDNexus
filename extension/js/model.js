/*
 * Everything about the commander card that is not DOM: formatting, the rule for what a viewer sees in
 * a given connection state, staleness, and the compact summary the video component shows.
 *
 * Kept free of `document` on purpose. Every view (overlay, panel, video component, mobile) decides
 * what to show through these functions, so the rules cannot drift between views, and
 * dev/test-views.js can exercise them under plain Node.
 */
(function (global) {
  'use strict';

  /* ----------------------------------------------------------- formatting -- */

  function isObject(value) {
    return value !== null && typeof value === 'object';
  }

  function num(value, digits) {
    if (typeof value !== 'number' || !isFinite(value)) return null;
    return value.toLocaleString(undefined, {
      minimumFractionDigits: digits || 0,
      maximumFractionDigits: digits || 0,
    });
  }

  /** A number with a unit, or null when the number is missing, so a gap never prints as "null t". */
  function withUnit(value, digits, unit) {
    var formatted = num(value, digits);
    return formatted === null ? null : formatted + unit;
  }

  /** Credits, abbreviated the way the game's own UI does once the numbers get long. */
  function credits(value) {
    if (typeof value !== 'number' || !isFinite(value)) return null;
    var abs = Math.abs(value);
    if (abs >= 1e9) return (value / 1e9).toFixed(2) + ' bn CR';
    if (abs >= 1e6) return (value / 1e6).toFixed(1) + ' m CR';
    return num(value) + ' CR';
  }

  function text(value) {
    return typeof value === 'string' && value.trim() ? value.trim() : null;
  }

  /** "in 4 min" / "3 h ago" — deliberately coarse, since the card updates every few seconds. */
  function relative(iso, now) {
    var when = Date.parse(iso);
    if (isNaN(when)) return null;
    var deltaSec = Math.round((when - (now == null ? Date.now() : now)) / 1000);
    var ahead = deltaSec >= 0;
    var abs = Math.abs(deltaSec);
    var value;
    if (abs < 60) value = abs + ' s';
    else if (abs < 3600) value = Math.round(abs / 60) + ' min';
    else value = Math.round(abs / 3600) + ' h';
    return ahead ? 'in ' + value : value + ' ago';
  }

  /** When a booked carrier jump leaves. A time already past reads as under way, not "3 min ago". */
  function departsText(iso, now) {
    var when = Date.parse(iso);
    if (isNaN(when)) return null;
    return when <= (now == null ? Date.now() : now) ? 'Departing now' : relative(iso, now);
  }

  /* ------------------------------------------------------------ view rule -- */

  var MSG_UNSUPPORTED = 'This commander is running a newer EDNexus than this card understands.';
  var MSG_ERROR = 'Can’t reach the EDNexus service right now. Trying again…';
  var MSG_CONNECTING = 'Connecting to the EDNexus service…';
  var MSG_IDLE = 'No commander card right now. It appears here while the broadcaster is playing Elite Dangerous with EDNexus running.';

  /**
   * What a viewer sees, from the transport's `status` ('waiting' | 'live' | 'offline' | 'error') and
   * the latest snapshot:
   *
   *   card         live + a snapshot the frontend understands;
   *   unsupported  live, but a newer schema than this frontend knows (a notice, never a half-parsed card);
   *   error        the card service cannot be reached (it keeps retrying);
   *   connecting   still waiting for the first answer; `visible` only once `loadingVisible`, so a
   *                request that normally answers quickly never flashes a "Connecting…" box;
   *   idle         offline / no card.
   *
   * `visible` is what the overlay and the video component use: they draw nothing on idle, because the
   * extension is installed on a channel whether or not its broadcaster is playing Elite, and a
   * permanent empty box on every such stream would be noise. The panel and mobile views occupy space
   * Twitch has already given them, so they show `message` on idle instead of a blank rectangle.
   *
   * `announce` is the text for the screen-reader live region: null leaves it alone, '' clears it.
   */
  function deriveView(status, snapshot, loadingVisible) {
    if (status === 'live' && snapshot) {
      if (snapshot.unsupported) {
        return {
          kind: 'unsupported', visible: true, snapshot: null,
          headline: 'EDNexus', sub: '', message: MSG_UNSUPPORTED, announce: MSG_UNSUPPORTED,
        };
      }
      return { kind: 'card', visible: true, snapshot: snapshot, announce: null };
    }
    if (status === 'error') {
      return {
        kind: 'error', visible: true, snapshot: null,
        headline: 'EDNexus', sub: 'Card unavailable', message: MSG_ERROR,
        announce: 'The EDNexus commander card is unavailable. Trying again.',
      };
    }
    if (status === 'waiting') {
      return {
        kind: 'connecting', visible: !!loadingVisible, snapshot: null,
        headline: 'Connecting…', sub: '', message: MSG_CONNECTING, announce: null,
      };
    }
    return {
      kind: 'idle', visible: false, snapshot: null,
      headline: 'EDNexus', sub: '', message: MSG_IDLE, announce: '',
    };
  }

  /**
   * Flags a card the app has stopped feeding, so viewers do not read old data as live. Only a card
   * can be stale. The threshold (`staleAfterMs`) is explained at STALE_AFTER_MS in state.js.
   */
  function staleness(view, now, staleAfterMs) {
    if (!view || view.kind !== 'card') return { stale: false, message: '' };
    var at = view.snapshot && view.snapshot.at ? Date.parse(view.snapshot.at) : NaN;
    var stale = isNaN(at) || (now - at) > staleAfterMs;
    if (!stale) return { stale: false, message: '' };
    return {
      stale: true,
      message: 'Last update ' + (isNaN(at) ? 'unknown' : (relative(view.snapshot.at, now) || 'unknown')),
    };
  }

  /* -------------------------------------------------------- compact view -- */

  /**
   * The few facts the video component keeps on screen all the time: where the commander is, what
   * they are flying with its fuel, and one rank bar. Every part is optional (the broadcaster chooses
   * which sections to publish), so a missing section simply leaves its part null.
   *
   * The rank shown is the first with progress to show, so the bar always means something; a
   * commander with only maxed ranks gets the first rank without a bar.
   */
  function compact(snapshot) {
    var s = isObject(snapshot) ? snapshot : {};
    var loc = isObject(s.loc) ? s.loc : {};
    var ship = isObject(s.ship) ? s.ship : {};
    var cmdr = isObject(s.cmdr) ? s.cmdr : {};

    var fuel = null;
    if (typeof ship.fuel === 'number' && typeof ship.fuelMax === 'number' && ship.fuelMax > 0) {
      var fraction = Math.max(0, Math.min(1, ship.fuel / ship.fuelMax));
      fuel = {
        fraction: fraction,
        low: fraction < 0.25,
        text: num(ship.fuel, 1) + ' / ' + num(ship.fuelMax, 1) + ' t',
      };
    }

    var rank = null;
    var ranks = Array.isArray(cmdr.ranks) ? cmdr.ranks : [];
    var valid = ranks.filter(function (r) { return isObject(r) && text(r.label) && text(r.name); });
    var picked = valid.filter(function (r) { return typeof r.pct === 'number' && r.pct > 0; })[0] || valid[0];
    if (picked) {
      rank = {
        label: text(picked.label),
        name: text(picked.name),
        elite: !!picked.elite,
        fraction: typeof picked.pct === 'number' && picked.pct > 0 ? Math.min(1, picked.pct / 100) : null,
      };
    }

    return {
      cmdr: text(cmdr.name) ? 'CMDR ' + text(cmdr.name) : 'CMDR',
      where: text(s.headline) || text(loc.system) || 'Elite Dangerous',
      shipType: text(ship.type),
      fuel: fuel,
      rank: rank,
    };
  }

  /* ------------------------------------------------------ per-view layout -- */

  /**
   * How each view frames the card, in one place so the policy is testable and the views cannot
   * quietly disagree.
   *
   *   hideWhenIdle  draw nothing when there is no live card. The overlay and the video component sit
   *                 over the stream, where an empty box would be noise. The panel and the mobile
   *                 view occupy space Twitch has already given them, so they show a short note.
   *   fold          sections collapse behind their titles (touch-first, so a long card is not one
   *                 endless scroll on a phone).
   *   openFolds     with `fold`, how many sections start open.
   */
  var LAYOUTS = {
    overlay:   { hideWhenIdle: true,  fold: false, openFolds: 0 },
    component: { hideWhenIdle: true,  fold: false, openFolds: 0 },
    panel:     { hideWhenIdle: false, fold: false, openFolds: 0 },
    mobile:    { hideWhenIdle: false, fold: true,  openFolds: 2 },
  };

  /** The layout for a view name; an unknown name gets the overlay's, which shows nothing by default. */
  function layoutFor(name) {
    var found = Object.prototype.hasOwnProperty.call(LAYOUTS, name) ? LAYOUTS[name] : LAYOUTS.overlay;
    return { hideWhenIdle: found.hideWhenIdle, fold: found.fold, openFolds: found.openFolds };
  }

  /**
   * Which folded sections are open after a re-render. The card re-renders on every snapshot, so a
   * section the viewer opened (or closed) must stay that way: `previous` maps title to open, and a
   * title not seen before starts open only if it is among the first `openFolds`.
   */
  function foldState(titles, previous, openFolds) {
    var state = {};
    titles.forEach(function (title, index) {
      state[title] = previous && typeof previous[title] === 'boolean' ? previous[title] : index < openFolds;
    });
    return state;
  }

  global.EDNexusModel = {
    isObject: isObject,
    num: num,
    withUnit: withUnit,
    credits: credits,
    text: text,
    relative: relative,
    departsText: departsText,
    deriveView: deriveView,
    staleness: staleness,
    compact: compact,
    layoutFor: layoutFor,
    foldState: foldState,
  };
})(window);
