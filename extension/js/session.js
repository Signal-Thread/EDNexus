/*
 * The part every view shares: connect to the transport, turn its status and snapshots into "what
 * should the viewer see" (EDNexusModel.deriveView), keep the staleness flag and the screen-reader
 * live region honest, and tick so time-relative text does not freeze.
 *
 * A view supplies rendering only:
 *
 *   EDNexusSession.start({
 *     live:    the role="status" element announcements are written to;
 *     onView:  (view) => render a new view state (a new snapshot, or a change of status);
 *     onStale: (stale, view) => show or clear the "Last update …" note; also called on every tick;
 *     onTick:  () => refresh time-relative text in the rendered sections (optional).
 *   });
 */
(function (global) {
  'use strict';

  var M = global.EDNexusModel;

  /** Do not flash a "Connecting…" box for a request that normally answers well inside this. */
  var LOADING_VISIBLE_AFTER_MS = 2000;

  /** How often the time-relative text ("Departs in 15 min", "Last update 30 min ago") is refreshed. */
  var TICK_MS = 30 * 1000;

  function start(options) {
    var live = options.live;
    var onView = options.onView;
    var onStale = options.onStale || function () {};
    var onTick = options.onTick || function () {};

    var current = null;
    var status = null;
    var loadingVisible = false;
    var view = M.deriveView(null, null, false);
    var wasStale = false;

    function announce(message) {
      if (live && live.textContent !== message) live.textContent = message;
    }

    function refreshStale() {
      var stale = M.staleness(view, Date.now(), global.EDNexusState.STALE_AFTER_MS);

      // Announce a card going stale once, and clear the announcement when it recovers.
      if (stale.stale && !wasStale) announce('Commander data may be out of date. ' + stale.message + '.');
      else if (!stale.stale && wasStale) announce('');
      wasStale = stale.stale;

      onStale(stale, view);
    }

    function update() {
      view = M.deriveView(status, current, loadingVisible);
      if (view.announce !== null) announce(view.announce);
      onView(view);
      refreshStale();
    }

    function setStatus(next) {
      // The transport reports 'live' before every snapshot; only a real change needs a redraw.
      if (next === status) return;
      status = next;
      if (status === 'waiting') {
        loadingVisible = false;
        global.setTimeout(function () {
          if (status !== 'waiting') return;
          loadingVisible = true;
          update();
        }, LOADING_VISIBLE_AFTER_MS);
      }
      update();
    }

    global.EDNexusState.connect({
      onSnapshot: function (snapshot) {
        current = snapshot;
        update();
      },
      onStatus: setStatus,
    });

    global.setInterval(function () {
      onTick();
      refreshStale();
    }, TICK_MS);
  }

  global.EDNexusSession = { start: start };
})(window);
