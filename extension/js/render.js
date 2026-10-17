/*
 * Builds the commander card's sections as DOM, for every view.
 *
 * Everything below builds DOM nodes and sets textContent. Nothing from the payload is ever passed to
 * innerHTML: the snapshot carries commander-authored strings (ship name, carrier name, mission
 * titles) that originate in the game and pass through the broadcaster's machine, and this code runs
 * in every viewer's browser.
 *
 * The views differ in how they frame these sections (a flyout, a panel, a component, a mobile
 * column), never in what a section contains, so there is exactly one copy of this logic.
 */
(function (global) {
  'use strict';

  var doc = global.document;
  var M = global.EDNexusModel;
  var isObject = M.isObject;
  var num = M.num;
  var withUnit = M.withUnit;
  var credits = M.credits;
  var text = M.text;

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

  /** Set by carrierSection for the duration of one build(); see build(). */
  var departs = null;

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
    var departsValue = pending ? M.departsText(carrier.departsAt) : null;
    var list = rows([
      ['Name', text(carrier.name)],
      ['Callsign', text(carrier.callsign)],
      ['Tritium', withUnit(carrier.fuel, 0, ' t')],
      ['Jump range', withUnit(carrier.jump, 0, ' ly')],
      ['Jumping to', pending],
      ['Departs', departsValue, false, function (node) {
        departs = { node: node, at: carrier.departsAt };
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

  /* --------------------------------------------------------------- public -- */

  /**
   * The sections for a snapshot the frontend understands, in display order, with any section the
   * broadcaster has hidden (or the game has not reported on yet) left out.
   *
   * Returns `{ nodes, refresh }`. `refresh` re-writes the time-relative text that would otherwise
   * stay frozen at its first render ("Departs in 15 min"), or is null when there is none.
   */
  function build(snapshot) {
    departs = null;

    var commander = isObject(snapshot.cmdr) ? snapshot.cmdr : null;
    var nodes = [
      locationSection(snapshot.loc),
      shipSection(snapshot.ship),
      commanderSection(commander),
      exobiologySection(snapshot.exo),
      missionsSection(snapshot.missions),
      miningSection(snapshot.mining),
      carrierSection(snapshot.carrier),
      cargoSection(snapshot.cargo, snapshot.cargoMore),
    ].filter(Boolean);

    if (!nodes.length) {
      nodes.push(el('p', 'ednx-empty', 'Waiting for the commander to launch…'));
    }

    var tracked = departs;
    departs = null;
    var refresh = tracked ? function () {
      var value = M.departsText(tracked.at);
      if (value) tracked.node.textContent = value;
    } : null;

    return { nodes: nodes, refresh: refresh };
  }

  /** A single plain message in place of the sections (connecting, error, unsupported, idle). */
  function message(content) {
    return el('p', 'ednx-empty', content);
  }

  /**
   * The video component's always-visible summary (see EDNexusModel.compact): the ship with a fuel
   * meter, and one rank bar. Parts the broadcaster does not publish are left out. Returns the
   * commander and place for the component's header alongside the body, since the header holds the
   * expand button and so is the component's to build.
   */
  function summary(snapshot) {
    var c = M.compact(snapshot);
    var wrap = el('div', 'ednx-summary');

    if (c.shipType || c.fuel) {
      var ship = el('div', 'ednx-summary__block');
      if (c.shipType) ship.appendChild(line('Ship', c.shipType));
      if (c.fuel) {
        ship.appendChild(line('Fuel', c.fuel.text));
        ship.appendChild(meter(c.fuel.fraction, c.fuel.low));
      }
      wrap.appendChild(ship);
    }

    if (c.rank) {
      var rank = el('div', 'ednx-summary__block');
      var rankLine = line(c.rank.label, c.rank.name);
      if (c.rank.elite) rankLine.lastChild.className += ' ednx-elite';
      rank.appendChild(rankLine);
      if (c.rank.fraction !== null) rank.appendChild(meter(c.rank.fraction));
      wrap.appendChild(rank);
    }

    return { cmdr: c.cmdr, where: c.where, node: wrap };
  }

  global.EDNexusRender = { el: el, build: build, message: message, summary: summary };
})(window);
