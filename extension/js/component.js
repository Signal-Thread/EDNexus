/*
 * The video component's shell: a compact summary a viewer can expand into the full card.
 *
 * The summary is EDNexusRender.summary (from EDNexusModel.compact); the expanded card is the same
 * EDNexusRender.build the overlay and panel use. Whether the component shows at all follows the
 * overlay's rule (EDNexusModel.layoutFor('component')): nothing unless there is a live card.
 */
(function (global) {
  'use strict';

  var doc = global.document;
  var R = global.EDNexusRender;
  var M = global.EDNexusModel;

  var layout = M.layoutFor('component');

  var root = doc.getElementById('root');
  var toggle = doc.getElementById('toggle');
  var cmdrName = doc.getElementById('cmdr-name');
  var cmdrWhere = doc.getElementById('cmdr-where');
  var summary = doc.getElementById('summary');
  var sections = doc.getElementById('sections');
  var staleNote = doc.getElementById('stale');
  var liveRegion = doc.getElementById('live');

  var refreshTimes = null;

  function setExpanded(expanded) {
    root.dataset.expanded = expanded ? 'true' : 'false';
    summary.hidden = expanded;
    sections.hidden = !expanded;
    toggle.setAttribute('aria-expanded', expanded ? 'true' : 'false');
    toggle.setAttribute('aria-label', expanded ? 'Show the compact summary' : 'Show the full commander card');
  }

  function renderSnapshot(snapshot) {
    var compact = R.summary(snapshot);
    cmdrName.textContent = compact.cmdr;
    cmdrWhere.textContent = compact.where;
    summary.replaceChildren(compact.node);

    var built = R.build(snapshot);
    refreshTimes = built.refresh;
    sections.replaceChildren.apply(sections, built.nodes);
  }

  function renderMessage(view) {
    cmdrName.textContent = 'EDNexus';
    cmdrWhere.textContent = view.sub;
    refreshTimes = null;
    summary.replaceChildren(R.message(view.message));
    sections.replaceChildren();
  }

  function onView(view) {
    root.dataset.state = view.kind === 'card' || view.kind === 'unsupported' ? 'live' : view.kind;

    if (view.kind === 'card') renderSnapshot(view.snapshot);
    else if (view.kind !== 'idle') renderMessage(view);

    // A message has no full card behind it: fall back to the summary so the viewer is never left
    // looking at an empty expanded area.
    if (view.kind !== 'card' && root.dataset.expanded === 'true') setExpanded(false);

    root.hidden = !view.visible && layout.hideWhenIdle;
  }

  function onStale(stale) {
    staleNote.textContent = stale.message;
    root.dataset.stale = stale.stale ? 'true' : 'false';
  }

  toggle.addEventListener('click', function () { setExpanded(root.dataset.expanded !== 'true'); });

  setExpanded(false);

  global.EDNexusSession.start({
    live: liveRegion,
    onView: onView,
    onStale: onStale,
    onTick: function () { if (refreshTimes) refreshTimes(); },
  });
})(window);
