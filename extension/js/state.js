/*
 * Transport for the commander card: where snapshots come from, and nothing about how they look.
 *
 * Two sources feed the same callback:
 *   - GET {ebs}/api/initial-state/{channelId}, once, so a viewer who opens the stream mid-session
 *     sees the card immediately instead of waiting for the commander's next journal event;
 *   - Twitch Extensions PubSub ("broadcast" target), for every update after that.
 *
 * The payload is produced by EDNexus.Core.Twitch.StreamCardSnapshot. Every field is optional —
 * broadcasters choose which sections they publish — so nothing here assumes a section exists.
 */
(function (global) {
  'use strict';

  /** Fallback EBS, used when the broadcaster has not configured one. Keep in sync with AppSettings.Twitch.EbsBaseUrl. */
  var DEFAULT_EBS = 'https://ednexus.signal-and-thread.com';

  /** Schema version this frontend understands. See StreamCardSchema.Version on the app side. */
  var SUPPORTED_SCHEMA = 1;

  /** A card older than this is flagged as stale — the app has probably stopped publishing. */
  var STALE_AFTER_MS = 5 * 60 * 1000;

  /** How long to wait for the broadcaster's configuration before falling back to the default EBS. */
  var CONFIG_WAIT_MS = 3000;

  /**
   * With a card on screen and no word from the EBS for this long, ask it again. The broadcaster's
   * "offline" message travels over PubSub, which is best-effort: a viewer who misses it would
   * otherwise keep a card the broadcaster has switched off until they reload.
   */
  var RECHECK_AFTER_MS = STALE_AFTER_MS;

  /** How often to look at whether a recheck is due. Page load time staggers viewers across it. */
  var RECHECK_TICK_MS = 60 * 1000;

  /**
   * A recheck that has not finished by now is abandoned. Only one runs at a time, so a request the
   * network never answers would otherwise stop every later one.
   */
  var RECHECK_TIMEOUT_MS = 15 * 1000;

  /** Total attempts at the one-time initial-state fetch, and the first backoff between them. */
  var INITIAL_STATE_ATTEMPTS = 3;
  var INITIAL_STATE_BACKOFF_MS = 1000;

  function parseConfig(raw) {
    if (!raw) return {};
    try {
      var parsed = JSON.parse(raw);
      return parsed && typeof parsed === 'object' ? parsed : {};
    } catch (err) {
      return {};
    }
  }

  /**
   * The EBS the broadcaster configured, from the extension configuration service. Only an https
   * origin is honoured: the configuration string is broadcaster-supplied, and this value is used to
   * build a fetch URL.
   */
  function resolveEbsBase(helper) {
    var configured = '';
    try {
      var broadcaster = helper && helper.configuration && helper.configuration.broadcaster;
      configured = parseConfig(broadcaster && broadcaster.content).ebsBaseUrl || '';
    } catch (err) {
      configured = '';
    }

    if (!configured) return DEFAULT_EBS;

    try {
      var url = new URL(configured);
      if (url.protocol !== 'https:' && url.hostname !== 'localhost' && url.hostname !== '127.0.0.1') {
        return DEFAULT_EBS;
      }
      return url.origin;
    } catch (err) {
      return DEFAULT_EBS;
    }
  }

  /**
   * Narrows whatever arrived to a plain snapshot object. A message that is not an object, or carries
   * a schema version this frontend does not know, is rejected rather than half-rendered.
   */
  function normalize(payload) {
    if (!payload || typeof payload !== 'object') return null;
    var version = typeof payload.v === 'number' ? payload.v : 0;
    if (version > SUPPORTED_SCHEMA) return { unsupported: true, v: version };
    return payload;
  }

  function parseMessage(message) {
    if (typeof message !== 'string') return normalize(message);
    try {
      return normalize(JSON.parse(message));
    } catch (err) {
      return null;
    }
  }

  /**
   * @param {object} handlers
   * @param {function} handlers.onSnapshot Called with each snapshot (or {unsupported:true}).
   * @param {function} handlers.onStatus   Called with 'waiting' | 'live' | 'offline' | 'error'.
   */
  function connect(handlers) {
    var onSnapshot = handlers.onSnapshot || function () {};
    var onStatus = handlers.onStatus || function () {};
    var helper = global.Twitch && global.Twitch.ext;

    // Mock mode: `?mock=1` renders the bundled sample payload with no Twitch and no EBS in the
    // loop, so the card can be developed and reviewed in a plain browser.
    var mock = /[?&]mock=1\b/.test(global.location.search);
    if (mock || !helper) {
      loadMock(onSnapshot, onStatus);
      return;
    }

    onStatus('waiting');

    // Twitch re-runs onAuthorized on every token refresh, not just once. Without these guards each
    // refresh would re-fetch the cached initial state, and a slow response could land after a live
    // broadcast and roll the card back to older data.
    var started = false;
    var liveSeen = false;
    var channelId = null;
    var configReady = false;

    // For the recheck: whether a card is on screen, when the EBS was last heard from (by either
    // route), and a count of broadcasts so a recheck answer that raced one is thrown away.
    var showing = false;
    var lastHeard = Date.now();
    var broadcasts = 0;
    var recheckInFlight = false;

    function show(snapshot) {
      showing = !!snapshot;
      lastHeard = Date.now();
      onSnapshot(snapshot);
    }

    // onAuthorized says nothing about whether the broadcaster's configuration has arrived; Twitch
    // delivers that through configuration.onChanged. Reading the EBS URL too early silently falls
    // back to the default, so wait for both before the one-time fetch.
    helper.onAuthorized(function (auth) {
      channelId = auth.channelId;
      begin();
    });

    if (helper.configuration && typeof helper.configuration.onChanged === 'function') {
      helper.configuration.onChanged(function () { configReady = true; begin(); });
    } else {
      configReady = true;
    }

    // A broadcaster who has never opened the config page has no configuration document, so
    // onChanged may never fire. Fall back to the default rather than never showing a card.
    global.setTimeout(function () { configReady = true; begin(); }, CONFIG_WAIT_MS);

    function begin() {
      if (started || !channelId || !configReady) return;
      started = true;
      fetchInitialState(resolveEbsBase(helper), channelId, function (snapshot) {
        // A retry that resolves after a live broadcast must not roll the card backwards.
        if (liveSeen) return;
        show(snapshot);
      }, onStatus);
      global.setInterval(recheckIfQuiet, RECHECK_TICK_MS);
    }

    function recheckIfQuiet() {
      if (!showing || recheckInFlight || Date.now() - lastHeard < RECHECK_AFTER_MS) return;
      recheckInFlight = true;
      var broadcastsBefore = broadcasts;
      var settled = false;

      // The abort covers the body read as well as the response. The timer settles this recheck on
      // its own, so a request that ignores the abort (or a browser without AbortController) still
      // frees the next tick, and any answer it gives afterwards is dropped.
      var controller = typeof global.AbortController === 'function' ? new global.AbortController() : null;
      var timer = global.setTimeout(function () {
        if (controller) controller.abort();
        settle();
      }, RECHECK_TIMEOUT_MS);

      function settle() {
        if (settled) return false;
        settled = true;
        global.clearTimeout(timer);
        recheckInFlight = false;
        return true;
      }

      var init = { method: 'GET' };
      if (controller) init.signal = controller.signal;

      global.fetch(resolveEbsBase(helper) + '/api/initial-state/' + encodeURIComponent(channelId), init)
        .then(function (response) {
          if (response.status === 404) return { gone: true };
          if (!response.ok) return null; // try again on a later tick
          return response.json().then(function (body) { return { snapshot: normalize(body) }; });
        })
        .then(function (answer) {
          // Timed out already: whatever this says, the next recheck asks again.
          if (!settle()) return;
          // A live broadcast that landed meanwhile is newer than anything this answer says.
          if (!answer || broadcasts !== broadcastsBefore) return;
          if (answer.gone) {
            // Switched off while this viewer missed the "offline" message.
            onStatus('offline');
            show(null);
          } else if (answer.snapshot) {
            onStatus('live');
            show(answer.snapshot);
          }
        })
        // Unreachable EBS, or aborted: keep the card and try again on a later tick.
        .catch(function () { settle(); });
    }

    helper.listen('broadcast', function (_target, _contentType, message) {
      var snapshot = parseMessage(message);
      if (!snapshot) return;
      liveSeen = true;
      broadcasts++;
      // The broadcaster switched the card off or signed out (EDNexus.Ebs ChannelStateClearing):
      // drop what is on screen, exactly as if the initial-state fetch had found nothing.
      if (snapshot.offline === true) {
        onStatus('offline');
        show(null);
        return;
      }
      onStatus('live');
      show(snapshot);
    });
  }

  function fetchInitialState(base, channelId, onSnapshot, onStatus, attempt) {
    attempt = attempt || 0;

    function retry() {
      // The EBS rate-limits this endpoint per caller IP, and a viewer who then gets no PubSub
      // update would sit without a card for the whole visit. Bounded, so a genuinely down service
      // is not hammered by every viewer.
      if (attempt + 1 >= INITIAL_STATE_ATTEMPTS) { onStatus('error'); return; }
      global.setTimeout(function () {
        fetchInitialState(base, channelId, onSnapshot, onStatus, attempt + 1);
      }, INITIAL_STATE_BACKOFF_MS * Math.pow(2, attempt));
    }

    global.fetch(base + '/api/initial-state/' + encodeURIComponent(channelId), { method: 'GET' })
      .then(function (response) {
        // 404 is not a failure: the commander simply has not published yet this session. The card
        // waits for the next PubSub message rather than retrying.
        if (response.status === 404) { onStatus('offline'); return null; }
        if (!response.ok) { retry(); return null; }
        return response.json();
      })
      .then(function (body) {
        if (body === null) return;
        var snapshot = normalize(body);
        if (!snapshot) return;
        onStatus('live');
        onSnapshot(snapshot);
      })
      .catch(function () { retry(); });
  }

  function loadMock(onSnapshot, onStatus) {
    onStatus('waiting');
    global.fetch('dev/sample-state.json')
      .then(function (response) { return response.json(); })
      .then(function (body) {
        var snapshot = normalize(body);
        if (!snapshot) { onStatus('error'); return; }
        // The sample is committed with fixed timestamps; move them relative to now so the card
        // doesn't read as stale, or show a carrier jump that departed hours ago.
        snapshot.at = new Date().toISOString();
        if (snapshot.carrier && snapshot.carrier.departsAt) {
          snapshot.carrier.departsAt = new Date(Date.now() + 15 * 60 * 1000).toISOString();
        }
        onStatus('live');
        onSnapshot(snapshot);
      })
      .catch(function () { onStatus('error'); });
  }

  global.EDNexusState = {
    connect: connect,
    SUPPORTED_SCHEMA: SUPPORTED_SCHEMA,
    STALE_AFTER_MS: STALE_AFTER_MS,
    DEFAULT_EBS: DEFAULT_EBS,
  };
})(window);
