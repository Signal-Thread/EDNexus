/*
 * The shell for the views that are a whole card on a page of their own: the Panel (under the stream)
 * and the Mobile view (in Twitch's app). Which of the two it is comes from <body data-view="…">, and
 * EDNexusModel.layoutFor says how that view frames the card: both always show the card, and the
 * mobile view folds sections behind their titles.
 *
 * Unlike the overlay these have no flyout: Twitch has already given the view its space, so a quiet
 * note stands in for the card when there is none, instead of a blank rectangle.
 */
(function (global) {
  'use strict';

  var doc = global.document;
  var R = global.EDNexusRender;
  var M = global.EDNexusModel;

  var layout = M.layoutFor(doc.body.getAttribute('data-view'));

  var root = doc.getElementById('root');
  var cmdrName = doc.getElementById('cmdr-name');
  var cmdrWhere = doc.getElementById('cmdr-where');
  var sections = doc.getElementById('sections');
  var staleNote = doc.getElementById('stale');
  var liveRegion = doc.getElementById('live');

  var refreshTimes = null;

  /** title -> open, kept across re-renders: the card redraws on every snapshot. */
  var folds = {};

  /**
   * Turns each section's title into a button that shows or hides the section's contents. The button
   * sits inside the heading, so the section keeps its place in the document outline for screen
   * readers, and is a full-width touch target (see .ednx-fold in mobile.css).
   */
  function foldSections(nodes) {
    var sectionNodes = nodes.filter(function (node) { return node.classList.contains('ednx-section'); });
    var titles = sectionNodes.map(function (node) { return node.firstChild.textContent; });
    folds = M.foldState(titles, folds, layout.openFolds);

    sectionNodes.forEach(function (node, index) {
      var heading = node.firstChild;
      var title = titles[index];
      var open = folds[title];

      var body = R.el('div', 'ednx-fold__body');
      while (heading.nextSibling) body.appendChild(heading.nextSibling);
      body.id = 'fold-' + index;
      body.hidden = !open;

      var toggle = R.el('button', 'ednx-fold');
      toggle.type = 'button';
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      toggle.setAttribute('aria-controls', body.id);
      toggle.appendChild(R.el('span', 'ednx-fold__title', title));
      toggle.appendChild(R.el('span', 'ednx-fold__chevron'));
      toggle.addEventListener('click', function () {
        var nowOpen = body.hidden;
        body.hidden = !nowOpen;
        toggle.setAttribute('aria-expanded', nowOpen ? 'true' : 'false');
        folds[title] = nowOpen;
      });

      heading.replaceChildren(toggle);
      node.appendChild(body);
    });
    return nodes;
  }

  function setSections(nodes) {
    sections.replaceChildren.apply(sections, nodes);
  }

  function renderSnapshot(snapshot) {
    var commander = M.isObject(snapshot.cmdr) ? snapshot.cmdr : null;
    cmdrName.textContent = (commander && M.text(commander.name)) ? 'CMDR ' + M.text(commander.name) : 'CMDR';
    cmdrWhere.textContent = M.text(snapshot.headline) || 'Elite Dangerous';

    var built = R.build(snapshot);
    refreshTimes = built.refresh;
    setSections(layout.fold ? foldSections(built.nodes) : built.nodes);
  }

  function renderMessage(view) {
    cmdrName.textContent = 'EDNexus';
    cmdrWhere.textContent = view.sub;
    refreshTimes = null;
    setSections([R.message(view.message)]);
  }

  function onView(view) {
    root.dataset.state = view.kind === 'card' || view.kind === 'unsupported' ? 'live' : view.kind;

    if (view.kind === 'card') {
      renderSnapshot(view.snapshot);
    } else if (view.kind === 'connecting' && !view.visible) {
      // A request that normally answers quickly must not flash "Connecting…" at every page load.
      cmdrName.textContent = 'EDNexus';
      cmdrWhere.textContent = '';
      refreshTimes = null;
      setSections([]);
    } else {
      renderMessage(view);
    }

    root.hidden = !view.visible && layout.hideWhenIdle;
  }

  function onStale(stale) {
    staleNote.textContent = stale.message;
    root.dataset.stale = stale.stale ? 'true' : 'false';
  }

  global.EDNexusSession.start({
    live: liveRegion,
    onView: onView,
    onStale: onStale,
    onTick: function () { if (refreshTimes) refreshTimes(); },
  });
})(window);
