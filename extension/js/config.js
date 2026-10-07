/*
 * Broadcaster config page. In production it only shows setup guidance (see config.html) and writes
 * nothing.
 *
 * The one exception is a development aid: when the page is served from localhost, a small form lets
 * a local test choose a local EBS. It writes a { "ebsBaseUrl": "..." } document to the extension
 * configuration service, which js/state.js reads back, again only on a localhost page, and only for
 * a loopback URL. A production viewer's fetch target is never taken from configuration, because the
 * channel controls that document and the viewer's browser would otherwise be pointed at whatever
 * host it names.
 */
(function (global) {
  'use strict';

  var doc = global.document;
  var state = global.EDNexusState;
  var form = doc.getElementById('dev-form');
  var input = doc.getElementById('ebs');
  var status = doc.getElementById('status');
  var helper = global.Twitch && global.Twitch.ext;

  // Not a development page: nothing to do, and the form stays hidden.
  if (!form || !state || !state.isDevHost()) return;
  form.hidden = false;

  function say(message, isError) {
    status.textContent = message;
    status.className = 'status' + (isError ? ' status--error' : ' status--ok');
  }

  function currentConfig() {
    try {
      var broadcaster = helper && helper.configuration && helper.configuration.broadcaster;
      if (!broadcaster || !broadcaster.content) return {};
      var parsed = JSON.parse(broadcaster.content);
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
  if (helper) {
    var loaded = false;

    /** @param {boolean} fromConfig True only when Twitch has actually delivered the configuration. */
    var showSavedUrl = function (fromConfig) {
      // Never clobber an edit in progress: onChanged also fires on a token refresh, and on the
      // broadcaster's own save.
      if (loaded) return;
      // Only a delivered configuration settles the field. Authorization can arrive first, with the
      // configuration still empty; latching then would ignore the saved URL when it lands, and the
      // next save would erase it.
      if (fromConfig) loaded = true;
      if (input.value.trim() !== '') return;
      input.value = currentConfig().ebsBaseUrl || '';
    };

    if (helper.configuration && typeof helper.configuration.onChanged === 'function') {
      helper.configuration.onChanged(function () { showSavedUrl(true); });
    }
    // A channel with no configuration document yet never gets an onChanged, so the field simply
    // stays empty and the placeholder explains the default.
    helper.onAuthorized(function () { showSavedUrl(false); });
  }

  // A real <form>, so Enter in the field saves as well as the button.
  form.addEventListener('submit', function (event) {
    event.preventDefault();

    var normalized = normalizeUrl(input.value);
    if (normalized === null) {
      say('That is not a localhost URL.', true);
      return;
    }

    if (!helper || !helper.configuration) {
      say('Twitch configuration service unavailable.', true);
      return;
    }

    try {
      helper.configuration.set('broadcaster', '1', JSON.stringify({ ebsBaseUrl: normalized }));
      input.value = normalized;
      say(normalized ? 'Saved.' : 'Cleared — using the default EBS.');
    } catch (err) {
      say('Could not save: ' + err.message, true);
    }
  });
})(window);
