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

  /**
   * The EBS every viewer reads from. Keep in sync with AppSettings.Twitch.EbsBaseUrl.
   *
   * This is deliberately not configurable by the broadcaster: the viewer's browser fetches this
   * origin, so letting a channel choose it would let any channel point its viewers' browsers (and
   * their IP addresses) at a host of its choosing. The only override is the development one in
   * resolveEbsBase, and it only exists when the page itself is served from loopback.
   */
  var DEFAULT_EBS = 'https://ednexus.signal-and-thread.com';

  /**
   * Schema version this frontend understands. See StreamCardSchema.Version on the app side
   * (src/EDNexus.Core/Twitch): if that changes, change this in the same commit.
   */
  var SUPPORTED_SCHEMA = 1;

  /**
   * The publishing contract with the desktop app: while the game is running and the card is switched
   * on, EDNexus re-publishes at least this often even if nothing changed (a heartbeat, which
   * refreshes the snapshot's `at`), and it clears the card when the app exits. Everything below is
   * sized from it. Change it here and in the app together.
   */
  var HEARTBEAT_INTERVAL_MS = 10 * 60 * 1000;

  /**
   * A card whose `at` is older than this is flagged as stale: the app has stopped publishing without
   * clearing (crash, lost network, machine asleep). Two missed heartbeats plus slack, so a commander
   * who is docked and idle (nothing happening in the journal) is never flagged while the app is
   * live. Flagging sooner than this shows viewers a false "last update 12 min ago" warning.
   */
  var STALE_AFTER_MS = 25 * 60 * 1000;

  /**
   * With a card on screen and no word from the EBS for this long, ask it again. The broadcaster's
   * "offline" message travels over PubSub, which is best-effort: a viewer who misses it would
   * otherwise keep a card the broadcaster has switched off until they reload. A heartbeat is due
   * every HEARTBEAT_INTERVAL_MS, so a little over that means one was missed.
   */
  var RECHECK_AFTER_MS = HEARTBEAT_INTERVAL_MS + 2 * 60 * 1000;

  /** How often to look at whether a recheck is due. Page load time staggers viewers across it. */
  var RECHECK_TICK_MS = 60 * 1000;

  /**
   * A recheck that has not finished by now is abandoned. Only one runs at a time, so a request the
   * network never answers would otherwise stop every later one.
   */
  var RECHECK_TIMEOUT_MS = 15 * 1000;

  /** How long to wait for the broadcaster's configuration, in development only (see resolveEbsBase). */
  var CONFIG_WAIT_MS = 3000;

  /**
   * Initial-state retry schedule. The EBS rate-limits this endpoint per caller IP and a viewer who
   * then gets no PubSub update would sit without a card for the whole visit, so a failure is retried
   * for as long as the page is open, but slowly and spread out, so a service that is genuinely down
   * is not hammered in lockstep by every viewer:
   *   delay = min(RETRY_MAX_MS, RETRY_BASE_MS * 2^attempt), then randomised into [delay/2, delay];
   * a Retry-After from the server raises that floor (capped at RETRY_AFTER_MAX_MS).
   */
  var RETRY_BASE_MS = 2000;
  var RETRY_MAX_MS = 5 * 60 * 1000;
  var RETRY_AFTER_MAX_MS = 5 * 60 * 1000;
  var INITIAL_STATE_TIMEOUT_MS = 15 * 1000;

  /** Failed attempts before the card admits it cannot reach the service ('error'); it keeps retrying. */
  var ERROR_AFTER_ATTEMPTS = 3;

  function isLoopbackHost(hostname) {
    return hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '[::1]';
  }

  /**
   * True only when this page is itself served from loopback: the local dev server in extension/dev
   * (or the Twitch local-test Base URI, which loads the same files from https://localhost:8080).
   * Twitch's hosted bundle is served from *.ext-twitch.tv, so no channel can ever put a production
   * viewer in this mode: the broadcaster controls the extension's configuration, not the origin the
   * viewer's iframe is loaded from.
   */
  function isDevHost() {
    try {
      return isLoopbackHost(global.location.hostname);
    } catch (err) {
      return false;
    }
  }

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
   * The origin of a loopback URL, or null for anything else. Used for the development override, so
   * only a local EBS can ever be selected that way, never a remote host.
   */
  function loopbackOrigin(raw) {
    try {
      var url = new URL(raw);
      if ((url.protocol !== 'https:' && url.protocol !== 'http:') || !isLoopbackHost(url.hostname)) return null;
      return url.origin;
    } catch (err) {
      return null;
    }
  }

  /**
   * The EBS to read from: always DEFAULT_EBS in production. When the page is served from loopback
   * (local development against a local EBS), the broadcaster configuration may name a loopback EBS
   * instead; a non-loopback value there is ignored.
   */
  function resolveEbsBase(helper) {
    if (!isDevHost()) return DEFAULT_EBS;

    var configured = '';
    try {
      var broadcaster = helper && helper.configuration && helper.configuration.broadcaster;
      configured = parseConfig(broadcaster && broadcaster.content).ebsBaseUrl || '';
    } catch (err) {
      configured = '';
    }
    return (configured && loopbackOrigin(configured)) || DEFAULT_EBS;
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
   * A Retry-After header as milliseconds, or 0 when absent or unusable. Accepts delta-seconds and
   * an HTTP date. (The browser only exposes this header to the extension if the EBS lists it in
   * Access-Control-Expose-Headers; without that it reads as absent and the backoff alone applies.)
   */
  function retryAfterMs(response) {
    var raw = null;
    try {
      raw = response && response.headers && typeof response.headers.get === 'function'
        ? response.headers.get('Retry-After')
        : null;
    } catch (err) {
      raw = null;
    }
    if (!raw) return 0;
    var value = String(raw).trim();
    var ms = 0;
    if (/^\d+$/.test(value)) {
      ms = parseInt(value, 10) * 1000;
    } else {
      var date = Date.parse(value);
      if (!isNaN(date)) ms = date - Date.now();
    }
    return ms > 0 ? Math.min(ms, RETRY_AFTER_MAX_MS) : 0;
  }

  /**
   * How long to wait before attempt number `attempt + 1`: capped exponential backoff with jitter,
   * never shorter than a Retry-After the server sent (itself jittered upwards so viewers told the
   * same number do not all come back in the same instant).
   */
  function backoffDelay(attempt, serverWaitMs) {
    var ceiling = Math.min(RETRY_MAX_MS, RETRY_BASE_MS * Math.pow(2, attempt));
    var delay = ceiling / 2 + Math.random() * (ceiling / 2);
    if (serverWaitMs > 0) delay = Math.max(delay, serverWaitMs * (1 + 0.25 * Math.random()));
    return delay;
  }

  function logFailure(what, err) {
    if (global.console && global.console.error) global.console.error('EDNexus: ' + what, err);
  }

  /**
   * @param {object} handlers
   * @param {function} handlers.onSnapshot Called with each snapshot (or {unsupported:true}), or null
   *                                       when there is nothing to show.
   * @param {function} handlers.onStatus   Called with 'waiting' | 'live' | 'offline' | 'error'.
   */
  function connect(handlers) {
    var onSnapshot = handlers.onSnapshot || function () {};
    var onStatus = handlers.onStatus || function () {};
    var helper = global.Twitch && global.Twitch.ext;
    var dev = isDevHost();

    // Mock mode: `?mock=1` renders the bundled sample payload with no Twitch and no EBS in the
    // loop, so the card can be developed and reviewed in a plain browser. Local-only: the sample is
    // not part of the hosted bundle, and a viewer must never be shown invented data.
    var mock = dev && /[?&]mock=1\b/.test(global.location.search);
    if (mock) {
      loadMock(onSnapshot, onStatus);
      return;
    }

    // No helper means the page is not running inside Twitch's player (or the helper was blocked).
    // There is nothing to connect to, so draw nothing rather than a card that can never fill in.
    if (!helper) {
      onStatus('offline');
      return;
    }

    onStatus('waiting');

    // Twitch re-runs onAuthorized on every token refresh, not just once. Without these guards each
    // refresh would re-fetch the cached initial state, and a slow response could land after a live
    // broadcast and roll the card back to older data.
    var started = false;
    var liveSeen = false;
    var channelId = null;
    // Production reads no broadcaster configuration, so there is nothing to wait for; development may.
    var configReady = !dev;

    // For the recheck: whether a card is on screen, when the EBS was last heard from (by either
    // route), and a count of broadcasts so a recheck answer that raced one is thrown away.
    var showing = false;
    var lastHeard = Date.now();
    var broadcasts = 0;
    var recheckInFlight = false;
    var recheckNotBefore = 0;

    /**
     * Hands a snapshot to the UI. A rendering bug must never be mistaken for a transport failure
     * (it used to land in the fetch's catch and trigger a refetch), so it is contained here.
     */
    function show(snapshot) {
      showing = !!snapshot;
      lastHeard = Date.now();
      try {
        onSnapshot(snapshot);
      } catch (err) {
        logFailure('card render failed', err);
      }
    }

    function status(value) {
      try {
        onStatus(value);
      } catch (err) {
        logFailure('status handler failed', err);
      }
    }

    helper.onAuthorized(function (auth) {
      channelId = auth.channelId;
      begin();
    });

    // Development only: onAuthorized says nothing about whether the broadcaster's configuration has
    // arrived (Twitch delivers that through configuration.onChanged), and reading a local-EBS
    // override too early silently falls back to the default. Wait for both before the fetch.
    if (dev) {
      if (helper.configuration && typeof helper.configuration.onChanged === 'function') {
        helper.configuration.onChanged(function () { configReady = true; begin(); });
      } else {
        configReady = true;
      }
      // A channel that never opened the config page has no configuration document, so onChanged may
      // never fire. Fall back to the default rather than never showing a card.
      global.setTimeout(function () { configReady = true; begin(); }, CONFIG_WAIT_MS);
    }

    function begin() {
      if (started || !channelId || !configReady) return;
      started = true;
      fetchInitialState(resolveEbsBase(helper), channelId, {
        onSnapshot: function (snapshot) {
          status('live');
          show(snapshot);
        },
        onStatus: status,
        // A fetch that resolves after a live broadcast must not roll the card backwards, and once a
        // broadcast has arrived there is no reason to keep asking.
        shouldStop: function () { return liveSeen; },
      });
      global.setInterval(recheckIfQuiet, RECHECK_TICK_MS);
    }

    function recheckIfQuiet() {
      var now = Date.now();
      if (!showing || recheckInFlight || now < recheckNotBefore || now - lastHeard < RECHECK_AFTER_MS) return;
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
          if (!response.ok) {
            // Rate limited or unwell: honour the server's Retry-After, else try on a later tick.
            recheckNotBefore = Date.now() + retryAfterMs(response);
            return null;
          }
          return response.json().then(function (body) { return { snapshot: normalize(body) }; });
        })
        .then(function (answer) {
          // Timed out already: whatever this says, the next recheck asks again.
          if (!settle()) return;
          // A live broadcast that landed meanwhile is newer than anything this answer says.
          if (!answer || broadcasts !== broadcastsBefore) return;
          if (answer.gone) {
            // Switched off while this viewer missed the "offline" message.
            status('offline');
            show(null);
          } else if (answer.snapshot) {
            status('live');
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
        status('offline');
        show(null);
        return;
      }
      status('live');
      show(snapshot);
    });
  }

  /**
   * The one-time initial-state fetch, retried until it gets an answer. 404 is an answer (the
   * commander has not published yet this session); a network failure, a timeout or a non-2xx is not.
   *
   * @param {string} base
   * @param {string} channelId
   * @param {object} handlers  onSnapshot(snapshot), onStatus(status), shouldStop() -> boolean
   */
  function fetchInitialState(base, channelId, handlers) {
    var attempts = 0;

    function failed(serverWaitMs) {
      attempts++;
      // Say so after a few failures, but keep going: a service that comes back should fill the card
      // in without the viewer reloading.
      if (attempts >= ERROR_AFTER_ATTEMPTS && !handlers.shouldStop()) handlers.onStatus('error');
      global.setTimeout(attempt, backoffDelay(attempts - 1, serverWaitMs));
    }

    function deliver(snapshot) {
      // Only rendering can fail here, which is the UI's problem and must not refetch.
      try {
        handlers.onSnapshot(snapshot);
      } catch (err) {
        logFailure('card render failed', err);
      }
    }

    function attempt() {
      if (handlers.shouldStop()) return;

      var settled = false;
      var controller = typeof global.AbortController === 'function' ? new global.AbortController() : null;
      var timer = global.setTimeout(function () {
        if (controller) controller.abort();
        if (finish()) failed(0);
      }, INITIAL_STATE_TIMEOUT_MS);

      /** True the first time only, so a timeout and a late response cannot both schedule a retry. */
      function finish() {
        if (settled) return false;
        settled = true;
        global.clearTimeout(timer);
        return true;
      }

      var init = { method: 'GET' };
      if (controller) init.signal = controller.signal;

      global.fetch(base + '/api/initial-state/' + encodeURIComponent(channelId), init).then(function (response) {
        if (settled) return;
        // 404 is not a failure: the commander simply has not published yet this session. The card
        // waits for the next PubSub message (the desktop app's heartbeat bounds how long that is).
        if (response.status === 404) {
          if (finish() && !handlers.shouldStop()) handlers.onStatus('offline');
          return;
        }
        if (!response.ok) {
          // 429 and 503 usually say how long to wait; sooner than that just earns another 429.
          if (finish()) failed(retryAfterMs(response));
          return;
        }
        response.json().then(function (body) {
          if (!finish()) return;
          if (handlers.shouldStop()) return;
          var snapshot = normalize(body);
          if (snapshot) deliver(snapshot);
        }, function () {
          // The body never arrived intact: a transport problem, so retry.
          if (finish()) failed(0);
        });
      }, function () {
        if (finish()) failed(0);
      });
    }

    attempt();
  }

  /**
   * `?stale=N` ages the sample by N minutes and `?v=N` stamps it with schema version N, so those two
   * states can be seen without a live EBS. Dev harness only (see connect).
   */
  function loadMock(onSnapshot, onStatus) {
    onStatus('waiting');
    global.fetch('dev/sample-state.json')
      .then(function (response) { return response.json(); })
      .then(function (body) {
        var snapshot = normalize(body);
        if (!snapshot) { onStatus('error'); return; }
        var query = global.location.search;
        var staleMatch = /[?&]stale=(\d+)/.exec(query);
        var versionMatch = /[?&]v=(\d+)/.exec(query);
        var ageMs = staleMatch ? parseInt(staleMatch[1], 10) * 60 * 1000 : 0;
        // The sample is committed with fixed timestamps; move them relative to now so the card
        // doesn't read as stale, or show a carrier jump that departed hours ago.
        snapshot.at = new Date(Date.now() - ageMs).toISOString();
        if (snapshot.carrier && snapshot.carrier.departsAt) {
          snapshot.carrier.departsAt = new Date(Date.now() + 15 * 60 * 1000).toISOString();
        }
        if (versionMatch) snapshot = normalize({ v: parseInt(versionMatch[1], 10) });
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
    isDevHost: isDevHost,
    loopbackOrigin: loopbackOrigin,
    // Exposed for extension/dev/test-state.js.
    _internals: { backoffDelay: backoffDelay, retryAfterMs: retryAfterMs, resolveEbsBase: resolveEbsBase },
  };
})(window);
