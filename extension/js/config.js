/*
 * Broadcaster config page. It has one real setting, and one development aid.
 *
 * Where the card starts. The broadcaster places the overlay card on a preview of their stream, so it
 * does not start on top of their webcam or game interface, and saves it to Twitch's extension
 * configuration service ("broadcaster" segment) as { "position": { "x", "y" } }. js/card.js reads it
 * back for every viewer as the card's starting place; a viewer who moves the card keeps their own.
 * The document is public to viewers, which is fine for two numbers, and js/position.js validates what
 * comes back (the broadcaster controls it, so it is not trusted).
 *
 * The development aid. When the page is served from localhost, a small form lets a local test choose a
 * local EBS by writing { "ebsBaseUrl": "..." } to the same document, which js/state.js reads back,
 * again only on a localhost page, and only for a loopback URL. A production viewer's fetch target is
 * never taken from configuration, because the channel controls that document and the viewer's browser
 * would otherwise be pointed at whatever host it names.
 *
 * Both write the one shared document, so every save goes through EDNexusPosition.configWith, which
 * keeps the keys it did not touch: saving a position must not erase the dev EBS, or the reverse.
 */
(function (global) {
  'use strict';

  var doc = global.document;
  var state = global.EDNexusState;
  var P = global.EDNexusPosition;
  var helper = global.Twitch && global.Twitch.ext;

  /**
   * The configuration document's text: as Twitch last delivered it, or as this page last wrote it
   * (Twitch's own copy only catches up through configuration.onChanged, and a second save in the
   * meantime must build on the first, not on the stale copy).
   */
  var rawConfig = '';

  /** How long after a save to wait for Twitch to echo the new configuration back. */
  var CONFIRM_AFTER_MS = 6000;

  /** Called whenever the configuration may have changed, and once at the start. */
  var configListeners = [];

  function deliveredConfig() {
    try {
      var broadcaster = helper && helper.configuration && helper.configuration.broadcaster;
      return broadcaster && typeof broadcaster.content === 'string' ? broadcaster.content : null;
    } catch (err) {
      return null;
    }
  }

  function syncConfig() {
    var delivered = deliveredConfig();
    if (delivered !== null) rawConfig = delivered;
    configListeners.forEach(function (listener) { listener(); });
  }

  /** Applies `changes` to the shared document and saves it. Throws if the service is unavailable. */
  function writeConfig(changes) {
    if (!helper || !helper.configuration) throw new Error('Twitch configuration service unavailable.');
    var next = P.configWith(rawConfig, changes);
    helper.configuration.set('broadcaster', '1', next);
    rawConfig = next;
  }

  /* ----------------------------------------------------- card position -- */

  function initPosition() {
    var stage = doc.getElementById('stage');
    var card = doc.getElementById('stage-card');
    var text = doc.getElementById('position-text');
    var status = doc.getElementById('position-status');
    var saveButton = doc.getElementById('position-save');
    var defaultButton = doc.getElementById('position-default');
    var presetsBox = doc.getElementById('presets');
    if (!stage || !card || !text || !status || !saveButton || !defaultButton || !presetsBox) return;

    var PRESETS = [
      ['Top left', 0, 0], ['Top centre', 0.5, 0], ['Top right', 1, 0],
      ['Middle left', 0, 0.5], ['Middle', 0.5, 0.5], ['Middle right', 1, 0.5],
      ['Bottom left', 0, 1], ['Bottom centre', 0.5, 1], ['Bottom right', 1, 1],
    ];

    /** What the configuration service holds (null: nothing, so the built-in default applies). */
    var saved = null;
    /** What the preview shows. */
    var draft = P.DEFAULT;
    /** The broadcaster has changed the preview here, so an incoming configuration must not overwrite it. */
    var edited = false;

    function same(a, b) {
      return Math.abs(a.x - b.x) < 0.0001 && Math.abs(a.y - b.y) < 0.0001;
    }

    function say(message, isError) {
      status.textContent = message;
      status.className = 'status' + (isError ? ' status--error' : ' status--ok');
    }

    var presetButtons = PRESETS.map(function (preset) {
      var button = doc.createElement('button');
      button.type = 'button';
      button.className = 'preset';
      button.setAttribute('aria-label', preset[0]);
      button.setAttribute('aria-pressed', 'false');
      button.addEventListener('click', function () {
        draft = { x: preset[1], y: preset[2] };
        changed();
      });
      presetsBox.appendChild(button);
      return { button: button, x: preset[1], y: preset[2] };
    });

    function render() {
      var baseline = saved || P.DEFAULT;
      card.style.setProperty('--ednx-x', String(draft.x));
      card.style.setProperty('--ednx-y', String(draft.y));
      text.textContent = P.describe(draft) + (same(draft, baseline) ? '' : ' (not saved yet)');
      saveButton.disabled = same(draft, baseline);
      defaultButton.disabled = same(draft, P.DEFAULT);
      presetButtons.forEach(function (preset) {
        preset.button.setAttribute('aria-pressed', same(draft, preset) ? 'true' : 'false');
      });
    }

    /** The broadcaster has picked a place: show it, and say so for screen readers. */
    function changed() {
      edited = true;
      render();
      say('Position: ' + P.describe(draft) + '. Save to apply.', false);
    }

    function loadSaved() {
      saved = P.parseConfig(rawConfig);
      if (!edited) draft = saved || P.DEFAULT;
      render();
    }

    defaultButton.addEventListener('click', function () {
      draft = P.DEFAULT;
      changed();
    });

    saveButton.addEventListener('click', function () {
      // The default is stored as nothing at all, so a later change to the default reaches this channel.
      var isDefault = same(draft, P.DEFAULT);
      try {
        writeConfig({ position: isDefault ? null : draft });
      } catch (err) {
        say('Could not save: ' + err.message, true);
        return;
      }
      saved = isDefault ? null : draft;
      edited = false;
      render();
      say(isDefault ? 'Saved: the default place.' : 'Saved: ' + P.describe(draft) + '.', false);
      confirmLater(saved);
    });

    /**
     * Twitch's configuration.set gives no result, and a write it ignores (for instance before the page
     * is authorised) looks exactly like one it accepted. Twitch echoes a real change back through
     * onChanged, so a few seconds on, check that what it holds is what was just written, and say so
     * if not rather than leave a broadcaster believing a position is saved that is not.
     */
    function confirmLater(expected) {
      global.setTimeout(function () {
        var held = P.parseConfig(deliveredConfig());
        var confirmed = expected === null ? held === null : (held !== null && same(held, expected));
        if (!confirmed && !edited) {
          say('Twitch has not confirmed the save yet. Reload this page to check that it stuck.', true);
        }
      }, CONFIRM_AFTER_MS);
    }

    // Keyboard: arrows nudge the card, Shift goes further, Home returns to the default.
    card.addEventListener('keydown', function (event) {
      var dx = 0;
      var dy = 0;
      if (event.key === 'ArrowLeft') dx = -1;
      else if (event.key === 'ArrowRight') dx = 1;
      else if (event.key === 'ArrowUp') dy = -1;
      else if (event.key === 'ArrowDown') dy = 1;
      else if (event.key === 'Home') {
        event.preventDefault();
        draft = P.DEFAULT;
        changed();
        return;
      } else {
        return;
      }
      event.preventDefault();
      draft = P.step(draft, dx, dy, event.shiftKey);
      changed();
    });

    // Pointer: press the card to drag it from where you pressed, or press an empty spot to put the
    // card's middle there and carry on dragging. Snaps to an edge or the middle on release.
    stage.addEventListener('pointerdown', function (event) {
      if (event.pointerType === 'mouse' && event.button !== 0) return;

      var stageRect = stage.getBoundingClientRect();
      var cardRect = card.getBoundingClientRect();
      var onCard = card.contains(event.target);
      var offsetX = onCard ? event.clientX - cardRect.left : cardRect.width / 2;
      var offsetY = onCard ? event.clientY - cardRect.top : cardRect.height / 2;

      function place(e) {
        draft = P.fromPixels(
          e.clientX - stageRect.left - offsetX,
          e.clientY - stageRect.top - offsetY,
          stageRect.width - cardRect.width,
          stageRect.height - cardRect.height);
        edited = true;
        render();
      }

      function onMove(e) {
        if (e.pointerId === event.pointerId) place(e);
      }

      function onEnd(e) {
        if (e.pointerId !== event.pointerId) return;
        stage.removeEventListener('pointermove', onMove);
        stage.removeEventListener('pointerup', onEnd);
        stage.removeEventListener('pointercancel', onEnd);
        try { stage.releasePointerCapture(e.pointerId); } catch (err) { /* already released */ }
        delete stage.dataset.dragging;
        draft = P.snap(draft);
        changed();
      }

      try { stage.setPointerCapture(event.pointerId); } catch (err) { /* the drag still works over the stage */ }
      stage.dataset.dragging = 'true';
      if (!onCard) place(event);
      stage.addEventListener('pointermove', onMove);
      stage.addEventListener('pointerup', onEnd);
      stage.addEventListener('pointercancel', onEnd);
      card.focus();
    });

    configListeners.push(loadSaved);
    render();
  }

  /* ---------------------------------------------- development: local EBS -- */

  function initDevForm() {
    var form = doc.getElementById('dev-form');
    var input = doc.getElementById('ebs');
    var status = doc.getElementById('status');

    // Not a development page: nothing to do, and the form stays hidden.
    if (!form || !input || !status || !state || !state.isDevHost()) return;
    form.hidden = false;

    function say(message, isError) {
      status.textContent = message;
      status.className = 'status' + (isError ? ' status--error' : ' status--ok');
    }

    function currentConfig() {
      try {
        var parsed = JSON.parse(rawConfig);
        return parsed && typeof parsed === 'object' ? parsed : {};
      } catch (err) {
        return {};
      }
    }

    /** Empty clears the override; anything else must be a loopback URL. Returns null when invalid. */
    function normalizeUrl(raw) {
      var value = (raw || '').trim();
      if (!value) return '';
      return state.loopbackOrigin(value);
    }

    // onAuthorized does not mean the configuration has arrived — Twitch delivers that through
    // configuration.onChanged. Reading it on authorization showed an empty field to a broadcaster who
    // had already saved a URL.
    var loaded = false;

    /** @param {boolean} fromConfig True only when Twitch has actually delivered the configuration. */
    function showSavedUrl(fromConfig) {
      // Never clobber an edit in progress: onChanged also fires on a token refresh, and on the
      // broadcaster's own save.
      if (loaded) return;
      // Only a delivered configuration settles the field. Authorization can arrive first, with the
      // configuration still empty; latching then would ignore the saved URL when it lands, and the
      // next save would erase it.
      if (fromConfig) loaded = true;
      if (input.value.trim() !== '') return;
      input.value = currentConfig().ebsBaseUrl || '';
    }

    // A channel with no configuration document yet never gets an onChanged, so the field simply
    // stays empty and the placeholder explains the default.
    // Counts as "delivered" only if Twitch has actually handed over a document: the up-front sync below
    // runs before it has, and latching then would hide a saved URL when it arrives.
    configListeners.push(function () { showSavedUrl(deliveredConfig() !== null); });
    if (helper) helper.onAuthorized(function () { showSavedUrl(false); });

    // A real <form>, so Enter in the field saves as well as the button.
    form.addEventListener('submit', function (event) {
      event.preventDefault();

      var normalized = normalizeUrl(input.value);
      if (normalized === null) {
        say('That is not a localhost URL.', true);
        return;
      }

      try {
        writeConfig({ ebsBaseUrl: normalized || null });
        input.value = normalized;
        say(normalized ? 'Saved.' : 'Cleared — using the default EBS.');
      } catch (err) {
        say('Could not save: ' + err.message, true);
      }
    });
  }

  /* ----------------------------------------------------------------- boot -- */

  // Twitch delivers the configuration through onChanged, not on authorization. Both are listened to,
  // and the configuration is read once up front for a page that opens with it already there.
  if (helper) {
    if (helper.configuration && typeof helper.configuration.onChanged === 'function') {
      helper.configuration.onChanged(syncConfig);
    }
    if (typeof helper.onAuthorized === 'function') helper.onAuthorized(syncConfig);
  }

  initPosition();
  initDevForm();
  syncConfig();
})(window);
