/*
 * The video overlay's shell: a small tab over the stream that opens into the commander card, the
 * flyout behaviour around it, and where on the stream it sits (the viewer can move it; the
 * broadcaster sets where it starts).
 *
 * What the card contains is built by js/render.js, and what it shows in a given connection state is
 * decided by js/model.js and js/session.js, the same code the panel, video component and mobile
 * views run. This file only frames it.
 */
(function (global) {
  'use strict';

  var doc = global.document;
  var R = global.EDNexusRender;
  var M = global.EDNexusModel;
  var STORAGE_KEY = 'ednexus.card.open';

  var root = doc.getElementById('root');
  var handle = doc.getElementById('handle');
  var panel = doc.getElementById('panel');
  var closeButton = doc.getElementById('close');
  var handleHeadline = doc.getElementById('handle-headline');
  var handleSub = doc.getElementById('handle-sub');
  var cmdrName = doc.getElementById('cmdr-name');
  var cmdrWhere = doc.getElementById('cmdr-where');
  var sections = doc.getElementById('sections');
  var staleNote = doc.getElementById('stale');
  var liveRegion = doc.getElementById('live');

  var baseSub = '';
  var refreshTimes = null;

  function setSections(nodes) {
    sections.replaceChildren.apply(sections, nodes);
  }

  /** The card proper, for a snapshot the frontend understands. */
  function renderSnapshot(snapshot) {
    var headline = M.text(snapshot.headline) || 'Elite Dangerous';
    handleHeadline.textContent = headline;
    baseSub = M.text(snapshot.subline) || '';
    cmdrWhere.textContent = headline;

    var commander = M.isObject(snapshot.cmdr) ? snapshot.cmdr : null;
    cmdrName.textContent = (commander && M.text(commander.name)) ? 'CMDR ' + M.text(commander.name) : 'CMDR';

    var built = R.build(snapshot);
    refreshTimes = built.refresh;
    setSections(built.nodes);
  }

  /** A card with nothing from the commander in it: the tab and panel carry one plain message. */
  function renderMessage(view) {
    handleHeadline.textContent = view.headline;
    baseSub = view.sub;
    cmdrName.textContent = 'EDNexus';
    cmdrWhere.textContent = '';
    refreshTimes = null;
    setSections([R.message(view.message)]);
  }

  /*
   * The overlay draws nothing when there is no live card (view.visible is false): the extension is
   * installed on a channel whether or not the broadcaster is playing Elite or running EDNexus, and a
   * permanent empty tab on every such stream would be noise for viewers and for the broadcaster.
   */
  function onView(view) {
    root.dataset.state = view.kind === 'card' || view.kind === 'unsupported' ? 'live' : view.kind;

    if (view.kind === 'card') renderSnapshot(view.snapshot);
    else if (view.kind !== 'idle') renderMessage(view);

    root.hidden = !view.visible;
  }

  /**
   * Shown on the tab (so it is visible while collapsed) as well as in the panel footer; the
   * announcement for a card going stale is made by js/session.js.
   */
  function onStale(stale) {
    staleNote.textContent = stale.message;
    handleSub.textContent = stale.stale ? stale.message : baseSub;
    root.dataset.stale = stale.stale ? 'true' : 'false';
  }

  /* --------------------------------------------------------------- flyout -- */

  function setOpen(open) {
    root.dataset.open = open ? 'true' : 'false';
    panel.hidden = !open;
    handle.setAttribute('aria-expanded', open ? 'true' : 'false');
    try {
      global.localStorage.setItem(STORAGE_KEY, open ? '1' : '0');
    } catch (err) {
      // Storage can be unavailable in an embedded iframe; the card still works, it just
      // forgets the viewer's choice between page loads.
    }
    if (open) closeButton.focus();
    else handle.focus();
  }

  function restoreOpenState() {
    var stored = null;
    try { stored = global.localStorage.getItem(STORAGE_KEY); } catch (err) { stored = null; }
    // Collapsed unless this viewer previously opened it — the stream comes first.
    root.dataset.open = stored === '1' ? 'true' : 'false';
    panel.hidden = stored !== '1';
    handle.setAttribute('aria-expanded', stored === '1' ? 'true' : 'false');
  }

  handle.addEventListener('click', function () {
    // The click that ends a drag is not a request to open the card.
    if (justDragged) return;
    setOpen(true);
  });
  closeButton.addEventListener('click', function () { setOpen(false); });
  doc.addEventListener('keydown', function (event) {
    if (event.key === 'Escape' && root.dataset.open === 'true') setOpen(false);
  });

  /* ------------------------------------------------------------- position -- */

  /*
   * Where the card sits. The viewer can drag it (by the tab, or by the open card's header) or move it
   * with the arrow keys; the broadcaster's default, from the Twitch configuration service, is where it
   * starts, so it need not cover their webcam or game. The viewer's own choice always wins, and is
   * kept in their browser. The maths and the validation of both stored values are in js/position.js.
   */

  var P = global.EDNexusPosition;
  var dragHead = doc.getElementById('drag-head');
  var moveButton = doc.getElementById('move');
  var resetButton = doc.getElementById('reset-pos');
  var moveLive = doc.getElementById('move-live');

  /** A press that moves less than this is a click, so the tab still opens when tapped. */
  var DRAG_THRESHOLD_PX = 4;

  var viewerPos = null;
  var broadcasterPos = null;
  var justDragged = false;

  function storage() {
    // Reading `localStorage` itself can throw in an embedded iframe with storage blocked.
    try { return global.localStorage; } catch (err) { return null; }
  }

  function effectivePosition() {
    return viewerPos || broadcasterPos || P.DEFAULT;
  }

  function applyPosition() {
    var p = effectivePosition();
    root.style.setProperty('--ednx-x', String(p.x));
    root.style.setProperty('--ednx-y', String(p.y));
    root.dataset.edge = P.edgeFor(p);
    // Only offer to put it back once it has been moved off where it would otherwise start.
    resetButton.hidden = !viewerPos;
  }

  function announceMove(prefix) {
    var message = prefix + P.describe(effectivePosition()) + '.';
    // The same words twice in a row would not be spoken again, so clear first.
    moveLive.textContent = '';
    global.setTimeout(function () { moveLive.textContent = message; }, 50);
  }

  function setViewerPosition(p) {
    viewerPos = p;
    P.save(storage(), p);
    applyPosition();
  }

  function resetPosition() {
    viewerPos = null;
    P.clear(storage());
    applyPosition();
    announceMove('Card back at its starting place: ');
  }

  /** The broadcaster's default, whenever Twitch (re)delivers the configuration. */
  function readBroadcasterDefault() {
    var raw = null;
    try {
      var helper = global.Twitch && global.Twitch.ext;
      var broadcaster = helper && helper.configuration && helper.configuration.broadcaster;
      raw = broadcaster && broadcaster.content;
    } catch (err) {
      raw = null;
    }
    broadcasterPos = P.parseConfig(raw);

    // Development only: stand in for a broadcaster default when there is no Twitch to supply one.
    if (!broadcasterPos && global.EDNexusState.isDevHost()) broadcasterPos = P.parseDevDefault(global.location.search);

    applyPosition();
  }

  /**
   * Starts a drag from `surface`: the tab, or the open card's header (not the buttons in it). Nothing
   * moves until the pointer has travelled DRAG_THRESHOLD_PX, so a plain click still opens the tab;
   * after a real drag the click that follows is swallowed. The new position is shown live and only
   * saved, snapped to an edge or the middle when close, when the pointer is let go.
   */
  function beginDrag(event) {
    if (event.pointerType === 'mouse' && event.button !== 0) return;
    if (event.currentTarget !== handle && event.target.closest && event.target.closest('button')) return;

    var surface = event.currentTarget;
    var rect = root.getBoundingClientRect();
    var startX = event.clientX;
    var startY = event.clientY;
    var moved = false;
    var latest = null;

    try { surface.setPointerCapture(event.pointerId); } catch (err) { /* the drag still works while the pointer stays over the card */ }

    function onMove(e) {
      if (e.pointerId !== event.pointerId) return;
      var dx = e.clientX - startX;
      var dy = e.clientY - startY;
      if (!moved) {
        if (Math.abs(dx) < DRAG_THRESHOLD_PX && Math.abs(dy) < DRAG_THRESHOLD_PX) return;
        moved = true;
        root.dataset.dragging = 'true';
      }
      // Free space is the player minus the card, so the card can reach every edge and no further.
      latest = P.fromPixels(rect.left + dx, rect.top + dy, global.innerWidth - rect.width, global.innerHeight - rect.height);
      viewerPos = latest;
      applyPosition();
    }

    function onEnd(e) {
      if (e.pointerId !== event.pointerId) return;
      surface.removeEventListener('pointermove', onMove);
      surface.removeEventListener('pointerup', onEnd);
      surface.removeEventListener('pointercancel', onEnd);
      try { surface.releasePointerCapture(e.pointerId); } catch (err) { /* already released */ }
      delete root.dataset.dragging;

      if (!moved) return;
      justDragged = true;
      global.setTimeout(function () { justDragged = false; }, 0);
      setViewerPosition(P.snap(latest));
    }

    surface.addEventListener('pointermove', onMove);
    surface.addEventListener('pointerup', onEnd);
    surface.addEventListener('pointercancel', onEnd);
  }

  /** The keyboard route to moving the card: arrows nudge it, Shift goes further, Home puts it back. */
  function onMoveKey(event) {
    var dx = 0;
    var dy = 0;
    if (event.key === 'ArrowLeft') dx = -1;
    else if (event.key === 'ArrowRight') dx = 1;
    else if (event.key === 'ArrowUp') dy = -1;
    else if (event.key === 'ArrowDown') dy = 1;
    else if (event.key === 'Home') {
      event.preventDefault();
      resetPosition();
      return;
    } else {
      return;
    }
    event.preventDefault();
    setViewerPosition(P.step(effectivePosition(), dx, dy, event.shiftKey));
    announceMove('Card moved to ');
  }

  handle.addEventListener('pointerdown', beginDrag);
  dragHead.addEventListener('pointerdown', beginDrag);
  moveButton.addEventListener('keydown', onMoveKey);
  resetButton.addEventListener('click', function () {
    resetPosition();
    // The button hides itself once the card is back; keep keyboard focus somewhere useful.
    moveButton.focus();
  });

  function initPosition() {
    viewerPos = P.load(storage());
    readBroadcasterDefault();

    var helper = global.Twitch && global.Twitch.ext;
    if (helper && helper.configuration && typeof helper.configuration.onChanged === 'function') {
      helper.configuration.onChanged(readBroadcasterDefault);
    }
  }

  /* ----------------------------------------------------------------- boot -- */

  restoreOpenState();
  initPosition();

  global.EDNexusSession.start({
    live: liveRegion,
    onView: onView,
    onStale: onStale,
    onTick: function () { if (refreshTimes) refreshTimes(); },
  });
})(window);
