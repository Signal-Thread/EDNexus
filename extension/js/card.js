/*
 * Renders the commander card and owns the flyout behaviour.
 *
 * Everything below builds DOM nodes and sets textContent. Nothing from the payload is ever passed to
 * innerHTML: the snapshot carries commander-authored strings (ship name, carrier name, mission
 * titles) that originate in the game and pass through the broadcaster's machine, and this code runs
 * in every viewer's browser.
 *
 * What is on screen is a function of two things: `status` (what the transport last told us:
 * 'waiting' | 'live' | 'offline' | 'error') and `current` (the snapshot, if any). See updateView.
 */
(function (global) {
  'use strict';

  var doc = global.document;
  var STORAGE_KEY = 'ednexus.card.open';

  /** Do not flash a "Connecting…" tab for a request that normally answers well inside this. */
  var LOADING_VISIBLE_AFTER_MS = 2000;

  /** How often the time-relative text ("Departs in 15 min", "Last update 30 min ago") is refreshed. */
  var TICK_MS = 30 * 1000;

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

  var current = null;
  var status = null;
  var loadingVisible = false;
  var baseSub = '';
  var wasStale = false;

  /** The "Departs" value node and its time, kept so the tick can refresh it without a re-render. */
  var departsNode = null;
  var departsAt = null;

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
  function relative(iso) {
    var when = Date.parse(iso);
    if (isNaN(when)) return null;
    var deltaSec = Math.round((when - Date.now()) / 1000);
    var ahead = deltaSec >= 0;
    var abs = Math.abs(deltaSec);
    var value;
    if (abs < 60) value = abs + ' s';
    else if (abs < 3600) value = Math.round(abs / 60) + ' min';
    else value = Math.round(abs / 3600) + ' h';
    return ahead ? 'in ' + value : value + ' ago';
  }

  /** When a booked carrier jump leaves. A time already past reads as under way, not "3 min ago". */
  function departsText(iso) {
    var when = Date.parse(iso);
    if (isNaN(when)) return null;
    return when <= Date.now() ? 'Departing now' : relative(iso);
  }

  /* ------------------------------------------------------------- elements -- */

  function el(tag, className, content) {
    var node = doc.createElement(tag);
    if (className) node.className = className;
    if (content != null) node.textContent = String(content);
    return node;
  }

  function section(title) {
    var wrap = el('section', 'ednx-section');
    wrap.appendChild(el('h2', 'ednx-section__title', title));
    return wrap;
  }

  /**
   * A definition list of label/value pairs, skipping any pair whose value is missing. A pair may
   * carry a fourth item: a function handed the value node, for text that needs refreshing later.
   */
  function rows(pairs) {
    var list = el('dl', 'ednx-rows');
    var written = 0;
    pairs.forEach(function (pair) {
      if (pair[1] == null || pair[1] === '') return;
      list.appendChild(el('dt', null, pair[0]));
      var value = el('dd', pair[2] ? 'ednx-num' : null, pair[1]);
      list.appendChild(value);
      if (typeof pair[3] === 'function') pair[3](value);
      written++;
    });
    return written ? list : null;
  }

  /** One label/value line, as used for ranks, mission stacks and mined materials. */
  function line(name, value) {
    var wrap = el('div', 'ednx-rank__line');
    wrap.appendChild(el('span', 'ednx-rank__name', name));
    wrap.appendChild(el('span', 'ednx-rank__value', value));
    return wrap;
  }

  function meter(fraction, low) {
    var clamped = Math.max(0, Math.min(1, fraction));
    var track = el('div', 'ednx-meter');
    var fill = el('div', 'ednx-meter__fill' + (low ? ' ednx-meter__fill--low' : ''));
    fill.style.width = (clamped * 100).toFixed(1) + '%';
    track.appendChild(fill);
    return track;
  }

  /** The three-sample exobiology run, as the suit shows it. */
  function pips(taken, total) {
    var wrap = el('span', 'ednx-pips');
    for (var i = 0; i < total; i++) {
      wrap.appendChild(el('span', 'ednx-pip' + (i < taken ? ' ednx-pip--on' : '')));
    }
    return wrap;
  }

  /* ------------------------------------------------------------- sections -- */

  function locationSection(loc) {
    if (!isObject(loc)) return null;
    var body = section('Location');
    var list = rows([
      ['System', text(loc.system)],
      ['Body', text(loc.body)],
      ['Docked at', loc.docked ? text(loc.station) : null],
      ['Station', loc.docked ? text(loc.stationType) : null],
      ['Status', loc.docked ? 'Docked' : 'In flight'],
    ]);
    if (!list) return null;
    body.appendChild(list);
    return body;
  }

  function shipSection(ship) {
    if (!isObject(ship)) return null;

    var name = text(ship.name);
    var ident = text(ship.ident);
    var label = name && ident ? name + ' (' + ident + ')' : (name || ident);

    var body = section('Ship');
    var list = rows([
      ['Hull', text(ship.type)],
      ['Registered', label],
      ['Cargo', withUnit(ship.cargo, 0, ' t')],
      ['Jump range', withUnit(ship.jump, 2, ' ly')],
    ]);
    if (list) body.appendChild(list);

    if (typeof ship.fuel === 'number' && typeof ship.fuelMax === 'number' && ship.fuelMax > 0) {
      var fraction = ship.fuel / ship.fuelMax;
      var group = el('div');
      group.appendChild(line('Fuel', num(ship.fuel, 1) + ' / ' + num(ship.fuelMax, 1) + ' t'));
      group.appendChild(meter(fraction, fraction < 0.25));
      body.appendChild(group);
    }

    return body.children.length > 1 ? body : null;
  }

  function carrierSection(carrier) {
    if (!isObject(carrier)) return null;
    var body = section('Fleet carrier');
    var pending = text(carrier.pendingSystem);
    var departs = pending ? departsText(carrier.departsAt) : null;
    var list = rows([
      ['Name', text(carrier.name)],
      ['Callsign', text(carrier.callsign)],
      ['Tritium', withUnit(carrier.fuel, 0, ' t')],
      ['Jump range', withUnit(carrier.jump, 0, ' ly')],
      ['Jumping to', pending],
      ['Departs', departs, false, function (node) {
        departsNode = node;
        departsAt = carrier.departsAt;
      }],
    ]);
    if (!list) return null;
    body.appendChild(list);
    return body;
  }

  function commanderSection(cmdr) {
    if (!isObject(cmdr)) return null;
    var hasCredits = typeof cmdr.credits === 'number';
    var ranks = Array.isArray(cmdr.ranks) ? cmdr.ranks : [];
    if (!hasCredits && !ranks.length) return null;

    var body = section('Commander');

    if (hasCredits) {
      var list = rows([['Balance', credits(cmdr.credits), true]]);
      if (list) body.appendChild(list);
    }

    if (ranks.length) {
      var wrap = el('div', 'ednx-ranks');
      ranks.forEach(function (rank) {
        // A null (or non-object) entry is skipped rather than allowed to throw and blank the card.
        if (!isObject(rank)) return;
        var label = text(rank.label);
        var name = text(rank.name);
        if (!label || !name) return;

        var group = el('div');
        var rankLine = line(label, name);
        if (rank.elite) rankLine.lastChild.className += ' ednx-elite';
        group.appendChild(rankLine);

        // An Elite ladder's percentage is progress towards the next Elite tier; a maxed one
        // reports 0, which would read as "no progress" rather than "nothing left to earn".
        if (typeof rank.pct === 'number' && rank.pct > 0) group.appendChild(meter(rank.pct / 100));
        wrap.appendChild(group);
      });
      if (wrap.children.length) body.appendChild(wrap);
    }

    return body.children.length > 1 ? body : null;
  }

  function exobiologySection(exo) {
    if (!isObject(exo)) return null;
    var body = section('Exobiology');

    var genus = text(exo.genus);
    if (genus) {
      var species = text(exo.species);
      var wrap = el('div', 'ednx-rank__line');
      wrap.appendChild(el('span', 'ednx-rank__name', species || genus));
      var value = el('span', 'ednx-rank__value');
      value.appendChild(pips(Math.min(typeof exo.samples === 'number' ? exo.samples : 0, 3), 3));
      wrap.appendChild(value);
      body.appendChild(wrap);
    }

    var list = rows([
      ['Body', text(exo.body)],
      ['Bio signals', exo.signals ? num(exo.signals) : null],
      ['Unsold data', exo.pendingValue ? credits(exo.pendingValue) : null],
      ['Samples held', exo.pendingCount ? num(exo.pendingCount) : null],
      ['Sold this session', exo.soldValue ? credits(exo.soldValue) : null],
      ['First discoveries', exo.firsts ? num(exo.firsts) : null],
    ]);
    if (list) body.appendChild(list);

    return body.children.length > 1 ? body : null;
  }

  function missionsSection(missions) {
    if (!isObject(missions) || !missions.active) return null;
    var body = section('Missions');

    var held = num(missions.active);
    var cap = num(missions.cap);
    var list = rows([
      ['Held', held !== null && cap !== null && missions.cap ? held + ' / ' + cap : held],
      ['Total reward', missions.reward ? credits(missions.reward) : null],
    ]);
    if (list) body.appendChild(list);

    var stacks = Array.isArray(missions.stacks) ? missions.stacks : [];
    stacks.forEach(function (stack) {
      if (!isObject(stack)) return;
      var target = text(stack.target) || text(stack.faction);
      if (!target) return;
      // KillsToClear, not the sum: one kill counts for every mission in the stack at once.
      var detail = [withUnit(stack.count, 0, '×'), withUnit(stack.kills, 0, ' kills')]
        .filter(function (part) { return part !== null; })
        .join(' · ');
      body.appendChild(line(target, detail));
    });

    return body.children.length > 1 ? body : null;
  }

  function miningSection(mining) {
    if (!isObject(mining)) return null;
    var body = section('Mining');

    var list = rows([
      ['Last rock', text(mining.content)],
      ['Core', text(mining.motherlode)],
      ['Remaining', withUnit(mining.remaining, 1, '%')],
      ['Prospected', mining.prospected ? num(mining.prospected) : null],
      ['Refined', mining.refined ? withUnit(mining.refined, 0, ' t') : null],
    ]);
    if (list) body.appendChild(list);

    var materials = Array.isArray(mining.materials) ? mining.materials : [];
    materials.forEach(function (material) {
      if (!isObject(material)) return;
      var name = text(material.name);
      var pct = withUnit(material.pct, 1, '%');
      if (!name || pct === null) return;
      body.appendChild(line(name, pct));
    });

    return body.children.length > 1 ? body : null;
  }

  function cargoSection(cargo, more) {
    if (!Array.isArray(cargo) || !cargo.length) return null;
    var body = section('Cargo hold');
    var list = el('dl', 'ednx-rows');
    cargo.forEach(function (item) {
      if (!isObject(item)) return;
      var name = text(item.name);
      var tonnes = withUnit(item.t, 0, ' t');
      if (!name || tonnes === null) return;
      list.appendChild(el('dt', null, name));
      list.appendChild(el('dd', 'ednx-num', tonnes));
    });
    if (!list.children.length) return null;
    body.appendChild(list);

    // The app sends the biggest lots only. Say so, rather than letting a truncated manifest read
    // as the whole hold.
    if (typeof more === 'number' && more > 0) {
      body.appendChild(el('p', 'ednx-more', '+ ' + num(more) + ' more ' + (more === 1 ? 'commodity' : 'commodities')));
    }
    return body;
  }

  /* --------------------------------------------------------------- render -- */

  function setSections(nodes) {
    sections.replaceChildren.apply(sections, nodes);
  }

  /** The card proper, for a snapshot the frontend understands. */
  function renderSnapshot(snapshot) {
    var headline = text(snapshot.headline) || 'Elite Dangerous';
    handleHeadline.textContent = headline;
    baseSub = text(snapshot.subline) || '';
    cmdrWhere.textContent = headline;

    var commander = isObject(snapshot.cmdr) ? snapshot.cmdr : null;
    cmdrName.textContent = (commander && text(commander.name)) ? 'CMDR ' + text(commander.name) : 'CMDR';

    departsNode = null;
    departsAt = null;

    var built = [
      locationSection(snapshot.loc),
      shipSection(snapshot.ship),
      commanderSection(commander),
      exobiologySection(snapshot.exo),
      missionsSection(snapshot.missions),
      miningSection(snapshot.mining),
      carrierSection(snapshot.carrier),
      cargoSection(snapshot.cargo, snapshot.cargoMore),
    ].filter(Boolean);

    if (!built.length) {
      built.push(el('p', 'ednx-empty', 'Waiting for the commander to launch…'));
    }
    setSections(built);
  }

  /** A card with nothing from the commander in it: the tab and panel carry one plain message. */
  function renderMessage(headline, sub, message) {
    handleHeadline.textContent = headline;
    baseSub = sub;
    cmdrName.textContent = 'EDNexus';
    cmdrWhere.textContent = '';
    departsNode = null;
    departsAt = null;
    setSections([el('p', 'ednx-empty', message)]);
  }

  function announce(message) {
    if (liveRegion.textContent !== message) liveRegion.textContent = message;
  }

  /**
   * The single place that decides what the viewer sees, from `status` and `current`:
   *
   *   live + snapshot  the card (or, for a newer schema, a notice saying so);
   *   error            a muted tab saying the card service cannot be reached (it keeps retrying);
   *   waiting          nothing at first, then a "Connecting…" tab if it is taking a while;
   *   offline / no card  nothing at all.
   *
   * Hiding is the point of the last row. The extension is installed on a channel whether or not the
   * broadcaster is playing Elite or running EDNexus, and a permanent empty tab on every such stream
   * would be noise for the viewers and for the broadcaster's overlay.
   */
  function updateView() {
    root.dataset.state = status;

    if (status === 'live' && current) {
      if (current.unsupported) {
        // Clear the header too: a v1 snapshot followed by a newer broadcast would otherwise leave the
        // old commander and location sitting above the incompatibility notice, reading as current.
        renderMessage('EDNexus', '', 'This commander is running a newer EDNexus than this card understands.');
        announce('This commander is running a newer EDNexus than this card understands.');
      } else {
        renderSnapshot(current);
      }
      root.hidden = false;
    } else if (status === 'error') {
      renderMessage('EDNexus', 'Card unavailable', 'Can’t reach the EDNexus service right now. Trying again…');
      announce('The EDNexus commander card is unavailable. Trying again.');
      root.hidden = false;
    } else if (status === 'waiting') {
      renderMessage('Connecting…', '', 'Connecting to the EDNexus service…');
      root.hidden = !loadingVisible;
    } else {
      root.hidden = true;
      announce('');
    }

    refreshStale();
  }

  /**
   * Flags a card the app has stopped feeding, so viewers do not read old data as live. Shown on the
   * tab (so it is visible while collapsed) as well as in the panel footer, and announced once when a
   * card goes stale. The threshold is explained at STALE_AFTER_MS in state.js.
   */
  function refreshStale() {
    var live = status === 'live' && current && !current.unsupported;
    var at = live && current.at ? Date.parse(current.at) : NaN;
    var stale = !!live && (isNaN(at) || (Date.now() - at) > global.EDNexusState.STALE_AFTER_MS);

    var message = stale ? 'Last update ' + (isNaN(at) ? 'unknown' : (relative(current.at) || 'unknown')) : '';
    staleNote.textContent = message;
    handleSub.textContent = stale ? message : baseSub;
    root.dataset.stale = stale ? 'true' : 'false';

    if (stale && !wasStale) announce('Commander data may be out of date. ' + message + '.');
    else if (!stale && wasStale) announce('');
    wasStale = stale;
  }

  /** Time-relative text that would otherwise stay frozen at its first render. */
  function tick() {
    if (departsNode && departsAt) {
      var value = departsText(departsAt);
      if (value) departsNode.textContent = value;
    }
    refreshStale();
  }

  function render(snapshot) {
    current = snapshot;
    updateView();
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
        updateView();
      }, LOADING_VISIBLE_AFTER_MS);
    }
    updateView();
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

  global.EDNexusState.connect({
    onSnapshot: render,
    onStatus: setStatus,
  });

  global.setInterval(tick, TICK_MS);
})(window);
