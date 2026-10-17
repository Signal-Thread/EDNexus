/*
 * The video overlay's shell: a small tab over the stream that opens into the commander card, and the
 * flyout behaviour around it.
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

  handle.addEventListener('click', function () { setOpen(true); });
  closeButton.addEventListener('click', function () { setOpen(false); });
  doc.addEventListener('keydown', function (event) {
    if (event.key === 'Escape' && root.dataset.open === 'true') setOpen(false);
  });

  /* ----------------------------------------------------------------- boot -- */

  restoreOpenState();

  global.EDNexusSession.start({
    live: liveRegion,
    onView: onView,
    onStale: onStale,
    onTick: function () { if (refreshTimes) refreshTimes(); },
  });
})(window);
