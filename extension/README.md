# EDNexus Twitch Extension — commander card

The viewer-facing half of EDNexus's Twitch integration: the commander card, in every extension view
type Twitch offers (see [Views](#views)). The headline one is a **flyout card** drawn over the stream.
Collapsed, it is a small tab on the left edge showing one line ("Docked at Jameson Memorial").
Clicking it slides out a panel with the commander's ship, location, ranks, exobiology progress,
missions, mining and cargo; closing it puts the stream back in full view. Collapsed is the default,
and each viewer's choice is remembered locally.

```
EDNexus desktop app                 EDNexus.Ebs                    this extension
─────────────────────               ───────────                    ──────────────
TwitchStreamCardService  ──POST──►  /api/update-state  ──PubSub──►  js/state.js ──► js/card.js
  (StreamCardSnapshot)               (relays to Twitch)             /api/initial-state (on load,
                                                                      and again when quiet)
```

Sections: [Views](#views) · [Layout](#layout) · [What viewers see](#what-viewers-see-and-when) ·
[Developing locally](#developing-locally) · [Submission & go-live checklist](#submission--go-live-checklist) ·
[Broadcaster setup](#broadcaster-setup) · [The payload](#the-payload) ·
[Publishing contract](#publishing-contract-with-the-desktop-app) · [Privacy](#privacy-notes) ·
[Troubleshooting](#troubleshooting) · [Conventions](#conventions)

## Views

One renderer, several shells: every view loads the same transport, model, renderer and session code,
and differs only in how it frames the card. All of them read the same public snapshot, so the
desktop app needs no setting for any of them.

| View | Entry page | Where viewers see it | Shows | When there is no live card |
|---|---|---|---|---|
| **Video overlay** | `video_overlay.html` | Over the whole player | A small tab that opens into the full card. **Movable** (see below) | Nothing |
| **Panel** | `panel.html` | Always-on, under the stream (a fixed narrow width, 318 px when this was written; verify in console) | The full card, scrolling inside the panel | A one-line note: the panel's space is already allocated, so a blank box would be worse |
| **Video component** | `component.html` | A box the broadcaster places and sizes on the stream | A compact summary (place, ship and fuel, one rank bar) that expands to the full card | Nothing |
| **Mobile** | `mobile.html` | The Twitch mobile app | The full card as one touch-sized column; each section folds behind a 44 px button, the first two start open | A one-line note |

### Moving the card

Only the **video overlay** is movable, because it is the one view that owns a whole canvas (the
player). Twitch itself places and sizes the panel, the video component and the mobile view, and an
extension's iframe cannot leave its box, so there is nothing for the extension to move there:
a broadcaster positions those in Twitch's own dashboard.

- **The viewer** drags the tab (or the open card's header), or focuses the Move button and uses the
  arrow keys (Shift moves further, Home puts it back). A small button appears to reset it. The choice
  is kept in the viewer's browser (`localStorage`), and takes priority over the broadcaster's default.
- **The broadcaster** sets where it *starts* on the config page (a preview of the stream: drag the
  card, click a spot, pick one of nine, or use the arrow keys), so it does not begin on top of a
  webcam or the game's interface. It is saved to Twitch's configuration service (`broadcaster`
  segment) as `{ "position": { "x", "y" } }`, shared with the development form's `ebsBaseUrl`
  (every save merges, so neither erases the other), and is public to viewers.
- With neither, the card is where it has always been: flush left, halfway down.

A position is two fractions of the *free* space (the player minus the card), not pixels, so it holds
through theatre mode, fullscreen and resizing and can never be off screen; flush to a side the card
is drawn square against it, and floating free it is rounded. Both stored values are validated
(`js/position.js`): neither the broadcaster's document nor the viewer's storage can put a string or
an out-of-range number into the layout.

The per-view rules (hide when idle, fold sections, how many start open) live in one place,
`EDNexusModel.layoutFor` in `js/model.js`, and `dev/test-views.js` tests them.

## Layout

| Path | Role |
|---|---|
| `video_overlay.html` | The overlay itself — the handle plus the flyout. |
| `panel.html` · `component.html` · `mobile.html` | The other viewer views (see [Views](#views)). |
| `config.html` | Broadcaster config page. Setup guidance only; nothing to configure (see below). |
| `css/tokens.css` | Every colour and the type scale, shared by every view (AGENTS.md rule 8). The mobile-only touch sizes are defined here too. |
| `css/sections.css` | The card's contents (rows, meters, ranks), shared by every view. A view's own stylesheet frames it. |
| `css/card.css` | Overlay framing. Click-through everywhere the card is not. |
| `css/page.css` · `css/mobile.css` · `css/component.css` | Framing for the panel (and, with `mobile.css` on top, the mobile view) and the video component. |
| `css/config.css` | Config page styling. |
| `js/state.js` | Transport: initial-state fetch (with Retry-After, backoff and jitter, retried for as long as the page is open) plus the PubSub subscription. With a card on screen and nothing heard for 12 minutes it asks `/api/initial-state` again, so a viewer who missed the "offline" broadcast drops the card (404) or picks up a newer one. No rendering. |
| `js/model.js` | Everything that is not DOM: formatting, what a viewer sees in a given state (`deriveView`), staleness, the compact summary and the per-view layout. No network, no `document`, so it runs under Node. |
| `js/render.js` | Builds the card's sections (and the compact summary) as DOM. The only place a payload string reaches the page, always via `textContent`. |
| `js/session.js` | Connects the transport to a view: status and snapshot in, `deriveView` and staleness out, screen-reader announcements, the 30 s tick. |
| `js/position.js` | Where the overlay card sits: the fraction maths, snapping, the broadcaster's default (read from and merged into Twitch's configuration document) and the viewer's saved choice, all validated. No `document`, so it runs under Node. Used by the overlay and the config page. |
| `js/card.js` · `js/page.js` · `js/component.js` | The shells: the overlay's flyout; the panel and mobile pages (`data-view` picks which); the video component. Framing only. |
| `js/config.js` | Config page: only the localhost-only developer form. |
| `dev/sample-state.json` | A representative payload for local development. Not in the upload. |
| `dev/serve-https.py` | Serves this folder over TLS for Twitch local testing. Not in the upload. |
| `dev/test-state.js` | Dependency-free Node checks for `js/state.js`. Not in the upload; CI runs it. |
| `dev/test-views.js` | Dependency-free Node checks for `js/model.js`, `js/position.js` and the view entry pages (script order, files exist, no inline script or hand-picked colours). Not in the upload; CI runs it. |

## What viewers see, and when

The extension is installed on a channel whether or not the broadcaster is playing Elite, so the
overlay and the video component draw **nothing** unless there is something to say. (The panel and
mobile views already own their space, so they show a one-line note instead of a blank box.)
`js/model.js` derives the view from the transport's status and the last snapshot; the table below
describes the overlay, and the other views follow the same rules:

| Situation | Viewer sees |
|---|---|
| A live card | The tab; the panel when opened. |
| The card is older than 25 minutes (`STALE_AFTER_MS`) | The tab turns amber and reads "Last update N min ago"; the panel footer says the same; assistive tech is told once. |
| Broadcaster not publishing (404), switched the card off, or exited EDNexus | Nothing at all. |
| Connecting | Nothing for 2 seconds, then a "Connecting…" tab (so a fast 404 never flashes a tab). |
| EBS unreachable or rate limited, three failures in | A muted red "Card unavailable" tab. It keeps retrying on its own, slowly, so it recovers without a reload. |
| A snapshot with a newer schema than `SUPPORTED_SCHEMA` | A notice that the commander is running a newer EDNexus. |

Everything but the first row is unobtrusive on purpose: the stream comes first.

## Developing locally

No Twitch and no EBS required — `?mock=1` renders the bundled sample payload with neither in the
loop. The flag works **only when the page is served from localhost**, never from Twitch's hosted
bundle. Use the flag explicitly: both views load Twitch's helper from its CDN, so `window.Twitch.ext`
exists even outside Twitch and the card would otherwise wait forever for an `onAuthorized` that
never comes.

```bash
python -m http.server 8931 --directory extension
```

Then open <http://127.0.0.1:8931/video_overlay.html?mock=1>, or any other view the same way
(`panel.html`, `component.html`, `mobile.html`). `&default=0.9,0.1` stands in for a broadcaster's
default position (x, y as fractions, 0 to 1) on the overlay, on a localhost page only. Two more flags show the states that
are otherwise hard to reach: `&stale=40` ages the sample by 40 minutes, and `&v=2` stamps it with a
schema newer than the frontend understands.

Look at each view at the size it will really have: the panel is 318 px wide, the mobile view is a
phone (about 375 px), and the video component is whatever the broadcaster draws, from roughly
250 px wide up. The browser's device toolbar (or `resize_window`) is enough.

Run the transport checks (retry/backoff, Retry-After, render errors, EBS override rules) and the
view checks (what each view shows, staleness, the compact summary, per-view layout, entry-page wiring):

```bash
node extension/dev/test-state.js
node extension/dev/test-views.js
```

### The EBS the extension reads from

Production viewers **always** read from `DEFAULT_EBS` in `js/state.js`
(`https://ednexus.signal-and-thread.com`). It is deliberately not something a broadcaster can
configure: the viewer's browser fetches that origin, so a channel-controlled value would let any
channel point its viewers at a host of its choosing. The one override is for development: when the
page itself is served from localhost, the config page shows a form (open
`https://localhost:8080/config.html`) that stores a **loopback** EBS URL such as
`https://localhost:8787`, and `js/state.js` honours it only on a localhost page. Anyone running their
own EBS for real viewers must publish their own extension build with `DEFAULT_EBS` changed.

### Chrome will not load these assets from localhost

Chrome 142+ enforces [Local Network Access](https://developer.chrome.com/blog/local-network-access):
a request from a public origin (Twitch) to loopback (`localhost`) needs a user permission that is
delegated through Permissions Policy, so **every** frame in the hierarchy must carry
`allow="local-network-access"`. An extension renders inside Twitch's iframe, and Twitch does not set
that attribute — so no site setting, header or server change on our side can unblock it. Chrome
reports the failure as a "CORS error", which sends you hunting in the wrong place.

Two ways round it:

- **Develop in Firefox**, which has not implemented LNA. The local recipe below works there.
- **Stop using a local Base URI**: upload the folder as a hosted asset bundle so Twitch serves it
  from their own CDN, and deploy the EBS behind a public origin. Both halves then live on public
  origins and LNA never applies. This is what release needs anyway.

### Against the real Twitch console (HTTPS)

Twitch only loads extension assets from an **https** Base URI, and the page it loads is then
forbidden from fetching an `http://` EBS — the browser blocks it as mixed content. So local testing
needs TLS on *both* servers. Both use the ASP.NET Core development certificate:

```powershell
# Once per machine. Without this the extension iframe fails silently — an iframe gets no
# certificate interstitial to click through, it simply does not load.
dotnet dev-certs https --trust

# Terminal 1 — the extension assets, matching the console's Testing Base URI.
python extension/dev/serve-https.py            # https://localhost:8080/

# Terminal 2 — the EBS.
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "https://localhost:8787"
dotnet run --project src/EDNexus.Ebs --no-launch-profile
```

Then in the extension's developer console set **Testing Base URI** `https://localhost:8080/`,
**Video — Fullscreen Viewer Path** `video_overlay.html`, **Config Path** `config.html`; open
`https://localhost:8080/config.html` once in the extension's config view and save
`https://localhost:8787` in the developer form; and in the desktop app set **Settings → Twitch →
Backend service** to `https://localhost:8787`.

`src/EDNexus.Ebs/appsettings.Development.json` (git-ignored) already allows `https://localhost:8080`
as a frontend origin, so `GET /api/initial-state/{channelId}` passes CORS. The EBS setting is
`Ebs__AdditionalAllowedFrontendOrigins__0`; add the
[Twitch Developer Rig](https://dev.twitch.tv/docs/extensions/rig/)'s origin there too if you use it.
Production needs no entry: `*.ext-twitch.tv` is always allowed.

## Submission & go-live checklist

For the maintainer, once Twitch has approved the extension project. The console is the source of
truth and its labels move around; anything marked **(verify in console)** is from memory of the
current UI rather than something this repository can check. Deployment of the EBS itself — the
container, volumes and secrets — lives in [`src/EDNexus.Ebs/README.md`](../src/EDNexus.Ebs/README.md)
and is not repeated here.

### 0. Before touching the console

- [ ] The EBS is deployed and reachable at `https://ednexus.signal-and-thread.com` over valid TLS
      (`GET /healthz` returns `status: "ok"`, plus `pubsub: "unknown"`, `"accepted"` or `"rejected"`: a `rejected` means Twitch is refusing the EBS's credentials and viewers get no live updates, see the EBS README's credential self-check), and the same origin is what the desktop app's
      `AppSettings.Twitch.EbsBaseUrl` defaults to.
- [ ] The desktop app build that publishes a **heartbeat** has shipped (see
      [Publishing contract](#publishing-contract-with-the-desktop-app)). Without it, an idle docked
      commander's card turns amber after 25 minutes.
- [ ] The EBS lets the extension read `Retry-After`: its CORS policy must list it in
      `Access-Control-Expose-Headers`. Without that, browsers hide the header from `js/state.js` and
      only the (still correct) backoff applies.
- [ ] `node extension/dev/test-state.js` passes, and the overlay has been looked at with
      `?mock=1`, `?mock=1&stale=40` and `?mock=1&v=2`, and the overlay has been dragged, moved with the
      keyboard and reset, and checked with a broadcaster default (`&default=0.9,0.1`).

### 1. Extension type and views

- [ ] **Extension type, first submission**: **Video — Fullscreen** only. It hands the extension the
      whole video area as a transparent canvas, which is what `video_overlay.html` is written
      against. Leave Panel, Video — Component and Mobile unticked for the *first* version: Twitch's
      review exercises every type a version declares, so a problem in a newer view must not hold up
      the first release. The bundle already contains the other views' files (they are inert until a
      version declares a path for them). The cost: a video extension does not render for mobile-app
      viewers until the Mobile view is declared. **(verify in console)**
- [ ] **Later versions: add the other views** once the first is approved and live. Each is a new
      version (and a new review) that declares a path; tick them together or one at a time:
      **Panel** `panel.html` (viewer height: pick within the console's range; the card scrolls inside
      it, so a shorter panel still works); **Video — Component** `component.html` (the broadcaster
      sizes it; the layout is fluid down to about 250 px wide); **Mobile** `mobile.html`. Check each
      with the console's hosted test and its own listing screenshot (section 6). **(verify in console)**
- [ ] **Video — Fullscreen Viewer Path**: `video_overlay.html` (the file name predates the console's
      current type labels).
- [ ] **Config Path**: `config.html`. **Live Config Path**: leave empty.
- [ ] **Requires Configuration**: **No**. There is nothing for a broadcaster to configure on Twitch's
      side — the card is chosen and fed from the desktop app — so the extension should be activatable
      straight after install. (If it were Yes, a broadcaster would have to open the config page and
      the page would have to call `Twitch.ext.configuration.set(...)` to mark it configured; the page
      no longer does.) The config page is still shipped as setup guidance. **(verify in console)**
- [ ] **Required Broadcaster Abilities**: none. The card only reads the viewer's own view of the
      stream. Do not request Identity Linking, Bits, subscription support or chat.
- [ ] **Viewer Height/Width**: not applicable to Video — Fullscreen.

### 2. Allowlists (Capabilities tab) **(verify in console)**

- [ ] **Allowlist for URL Fetching Domains**: `https://ednexus.signal-and-thread.com` — the EBS
      origin, exactly `DEFAULT_EBS` in `js/state.js`. This is the one place the extension makes a
      request; Twitch's CSP blocks `fetch` to anything not listed. If the EBS ever moves, change this
      and `DEFAULT_EBS` (and the desktop `EbsBaseUrl` default) together.
- [ ] Allowlist for Config Domains / Panel Image Domains / anything else: leave empty.
- [ ] **OAuth redirect URL allowlist** (the login flow is the EBS's, not the extension's):
      `https://ednexus.signal-and-thread.com/oauth/callback`. It must match the EBS setting
      `Twitch__OAuthRedirectUri` **character for character, scheme included**; Twitch rejects the
      login outright on any mismatch. If the extension's console page does not offer OAuth redirect
      URLs, register a regular application in the console's Applications tab instead and use *its*
      Client ID and secret for `Twitch__ClientId` / `Twitch__ClientSecret`; `Twitch__ExtensionId`
      always stays the extension's own Client ID. (`.env.example` sets both to the extension's id.)

### 3. Credentials, and where they go

All of these are set on the EBS, never committed (names exactly as the EBS reads them; the full list
with defaults is in [the EBS README](../src/EDNexus.Ebs/README.md#running-locally)):

| Value | Console location **(verify in console)** | EBS environment variable |
|---|---|---|
| Extension Client ID | Extension's Settings page | `Twitch__ExtensionId` (and `Twitch__ClientId` unless a separate application is used) |
| Client Secret | Same page, "New Secret" | `Twitch__ClientSecret` |
| Extension Secret (**base64**; signs the PubSub JWT) | Extension's Settings → Extension Secrets | `Twitch__ExtensionSecret` |
| OAuth redirect | Allowlist above | `Twitch__OAuthRedirectUri` |
| Persistent state | — | `Ebs__DataDirectory` (SQLite `ebs.db`; mount a volume) |
| Token-encryption key ring | — | `Ebs__DataProtectionKeysDirectory` (separate volume; losing it logs every broadcaster out) |
| Oldest card still served | — | `Ebs__ChannelStateMaxAgeHours` (default 24; values under 12 are refused at startup) |
| Dev rig / local asset origin | — | `Ebs__AdditionalAllowedFrontendOrigins__0` (not needed in production) |

The Client Secret and Extension Secret are different values; the EBS refuses to start unless the
extension secret is valid base64.

### 4. The asset bundle (Asset Hosting)

Twitch serves the uploaded archive's contents directly under the extension's Base URI, so the views
must sit at the **root of the zip**. Do not zip this folder by hand.

The **`Twitch extension bundle`** workflow (`.github/workflows/extension-package.yml`) builds it:

- It zips `extension/` **excluding `dev/` and `README.md`**, after checking that the viewer paths
  exist, every view loads the Twitch helper, no inline script/style or link other than the project-site one slipped in, and
  `dev/` did not leak. It runs on every change to `extension/` (push to `main`, tags `v*`, pull
  requests) and on demand (Actions → *Twitch extension bundle* → *Run workflow*).
- **To get the zip**, open the latest successful run on `main` (or the run for your release tag),
  download the artifact `ednexus-twitch-extension`, and **unzip once**: GitHub wraps every artifact in
  its own zip, and the file to upload is the inner `ednexus-twitch-extension.zip`. A `v*` tag also
  attaches that file to the GitHub release, which needs no unwrapping. From a terminal:

  ```powershell
  gh run download --repo Signal-Thread/EDNexus --name ednexus-twitch-extension
  ```
- **Check the layout before uploading** — `video_overlay.html` must be at the root, with no wrapping
  folder, no `dev/`, no `README.md`:

  ```powershell
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [IO.Compression.ZipFile]::OpenRead("$PWD\ednexus-twitch-extension.zip").Entries.FullName
  # expect: config.html, video_overlay.html, css/..., js/...
  ```
- [ ] Upload it under **Asset Hosting** for the version you are about to test **(verify in console)**.

### 5. Version bumps

- Each bundle you upload is a new **console version** (Versions tab → *Create new version*, e.g.
  `0.0.2`); an approved or released version is immutable, so a fix is always a new version, a new
  upload and, once released, a new review. The console version is independent of the EDNexus app's
  version; note which app build it was tested against in the release notes.
- **If `StreamCardSchema.Version` (`src/EDNexus.Core/Twitch/StreamCardSnapshot.cs`) changes, bump
  `SUPPORTED_SCHEMA` in `js/state.js` in the same change.** Adding an optional field needs no bump;
  removing or repurposing one does. The app and the extension release on different clocks, so a viewer
  can be on the old bundle while a broadcaster is on a new app; a mismatch shows the "newer EDNexus"
  notice rather than a broken card. See [Changing the contract](#changing-the-contract).
- Changing `DEFAULT_EBS`, the staleness window or the heartbeat interval likewise touches the app
  (`AppSettings.Twitch.EbsBaseUrl`, the republish interval); change both sides together.

### 6. Listing: entered in the console, **not** in the zip **(verify in console)**

None of these ship in the bundle, so they are easy to forget until review asks:

- [ ] Name, **summary**, **description** (what it shows, that it needs the EDNexus desktop app and
      Elite Dangerous, and that it is a video overlay), and the category/keywords.
- [ ] **Icon(s)** and **screenshots** at the sizes the console states. Take screenshots from the
      real overlay (`?mock=1` shows every section) and use the brand assets under `assets/`
      (`assets/icons`, `assets/logo`) — see the design system linked in `AGENTS.md`.
- [ ] **Privacy policy URL** and **Terms of Service URL**. Neither exists as a hosted page yet; the
      only text in the repository is the root `README.md` "Privacy & crash reporting" section, which
      is about the desktop app. Write one that also covers what the extension does (see
      [Privacy notes](#privacy-notes)).
- [ ] **Support email** (and a support/issues URL — the GitHub issues page works).
- [ ] The data questions Twitch asks (what data the extension collects, whether it uses Twitch user
      identity). Answer from [Privacy notes](#privacy-notes): the extension itself requests no
      identity and sets no cookies; viewers' IP addresses reach the EBS as with any web request.
- [ ] **Testing instructions for the reviewer.** Important and easy to miss: the overlay draws
      *nothing* unless a broadcaster is publishing, and a reviewer will not have Elite Dangerous.
      Keep a test channel publishing for the whole review window — run EDNexus with **Settings →
      Developer Options → Developer mode** on (random-but-valid journal events through the real bus)
      and the stream card on (the events go through the real bus, so the card should publish: confirm
      this on your own channel first), and give the reviewer that channel and the steps in
      [Broadcaster setup](#broadcaster-setup). Say plainly in the notes that an idle channel shows no
      tab by design.

### 7. Test plan

1. **Local**: `node extension/dev/test-state.js`; `?mock=1`, `&stale=40`, `&v=2` in a browser.
2. **Hosted test**: upload the bundle, move the version to **Hosted Test**, install it on a test
   channel (your own channel, or one you add as a tester) **(verify in console)**.
3. **Publish from the desktop app**: Settings → Twitch → Sign in with Twitch, switch the card on,
   (Developer mode if the game is not running). Open the channel as a viewer in a second browser
   profile, signed out of the broadcaster account. Expect the tab within a couple of seconds, with the
   one-line headline, and the panel to open and close; reload and check the card is there immediately
   (initial state), then change something in the app and watch it update (PubSub).
4. **Offline state**: switch the card off in the app (or sign out / close EDNexus). Expect the tab to
   disappear without a reload (the `offline` broadcast). A viewer who opens the channel afterwards sees
   no tab at all.
5. **Stale state**: stop the app uncleanly (end the process) with the card on. Within ~25 minutes the
   tab turns amber with "Last update N min ago"; within ~12 minutes a viewer's recheck finds the card
   gone or aged. Restart the app and expect it to recover.
6. **Schema-too-new state**: temporarily lower `SUPPORTED_SCHEMA` to `0` in a throwaway bundle (or use
   `?mock=1&v=2` locally) and check the "newer EDNexus" notice, with no stale note.
7. **EBS down**: stop the EBS and load the channel. After three failed attempts the muted "Card
   unavailable" tab appears; bring the EBS back and expect it to fill in without a reload.
8. Check the browser console is clean, and that the Network panel shows requests to **only** the
   Twitch helper script and the EBS origin.

### 8. Review-readiness self-check

- [ ] No network requests other than the Twitch helper and the EBS (the workflow does not check this;
      look in the Network panel, step 7.8).
- [ ] CSP-safe: no inline `<script>`, no `<style>`, no `style=` attributes, no `eval`. (CI rejects the
      first three; `js/card.js` sets `element.style.width` through the CSSOM, which is allowed.)
- [ ] The only link out is the EDNexus mark (overlay, panel and mobile footers, and the install step on
      the config page), to the project site <https://signal-thread.github.io/EDNexus/>, in a new tab
      with `rel="noopener noreferrer"`. CI rejects any other `<a>` or `target=`. It was removed once as
      a review risk and restored deliberately: if review objects to a link in a viewer view, drop it
      from the footers (the config page is the broadcaster's and is much less likely to matter).
      **(verify in console)**
- [ ] Every view that a version declares has been looked at with `?mock=1`, `&stale=40` and `&v=2` at
      its real size, and with no commander publishing (panel and mobile show their note; overlay and
      component show nothing).
- [ ] The overlay is click-through everywhere but the tab and the open panel; the tab and panel are
      keyboard operable (Tab, Enter, Escape), the scrolling list is focusable, and state changes are
      announced through the live region.
- [ ] Text is at least 12px and passes 4.5:1 on the panel; colours come only from `css/tokens.css`.
- [ ] Nothing from the payload reaches `innerHTML`.
- [ ] The listing, privacy policy and ToS describe what the extension does and what the desktop app
      sends (see [Privacy notes](#privacy-notes)).

### 9. Go live

- [ ] Submit the version for review **(verify in console)**; keep the test channel publishing.
- [ ] On approval: **Release**, then **Activate** on your own channel as a *Video overlay* slot and
      repeat test-plan steps 3–4 against the released version.
- [ ] Only now announce it, and point broadcasters at [Broadcaster setup](#broadcaster-setup).
- [ ] Watch the EBS logs for 429s and for `initial-state` 404s that never resolve.

## Broadcaster setup

What to tell a streamer (the same text is on the config page):

1. Install **EDNexus** on the PC you play on, and start it.
2. **Settings → Twitch → Sign in with Twitch.** Your browser opens Twitch's own sign-in page, the EDNexus
   backend does the handshake, and the app stores only its own token for your channel — never your
   Twitch password or a raw Twitch token.
3. Tick **Show my session to viewers**, and choose the sections to share under it. Your credit balance
   is off until you turn it on; the rest start on.
4. In the Twitch dashboard, **Extensions → My Extensions**, activate *EDNexus commander card* in the
   slots you want: as a **video overlay** (a tab over the stream), a **panel** (always visible under
   the stream), a **video component** (a small summary box you place on the stream), or any
   combination. Each is switched on or off independently in Twitch, and none needs a setting in
   the desktop app: they all show the same card.

Viewers then see the card in each slot you activated while you play (the overlay and the video
component show nothing otherwise; the panel shows a short note), and the Twitch mobile app shows
it too once the mobile view is live. The card
is removed when you switch it off, sign out, or close EDNexus. There is nothing to configure on the
extension's own settings page.

## The payload

Produced by `EDNexus.Core.Twitch.StreamCardSnapshot` and relayed verbatim. Twitch caps a PubSub
message at 5 KiB, so keys are short and lists are capped app-side.

```jsonc
{
  "v": 1,                       // schema version — StreamCardSchema.Version
  "at": "2026-09-15T18:20:00Z", // when the snapshot was taken; drives the "stale" note
  "headline": "Nervi / Nervi 2 a",
  "subline": "Flying Krait Phantom (PH-01)",
  "cmdr":     { "name": …, "credits": …, "ranks": [{ "label", "name", "pct", "elite" }] },
  "ship":     { "type", "name", "ident", "fuel", "fuelMax", "cargo", "jump" },
  "loc":      { "system", "body", "docked", "station", "stationType", "mode", "status" },
  "carrier":  { "name", "callsign", "fuel", "jump", "pendingSystem", "departsAt" },
  "exo":      { "pendingValue", "pendingCount", "soldValue", "soldCount", "firsts",
                "genus", "species", "samples", "body", "signals" },
  "mining":   { "content", "motherlode", "remaining", "materials": […], "prospected", "refined" },
  "missions": { "active", "cap", "reward", "stacks": [{ "target", "faction", "count", "kills", "reward" }] },
  "cargo":    [ { "name", "t" } ],
  "cargoMore": 0               // lots beyond those listed, so the card can say "+7 more"
}
```

**Every section is optional.** A broadcaster opts in per section in the desktop app
(Settings → Twitch), and anything switched off is never serialized — it does not leave their
machine, so there is nothing for this frontend to hide. Treat a missing section as "not shown", and
never assume a field is present or well-formed: a `null` list entry or a missing number is skipped,
never printed as `null`. `dev/sample-state.json` is a complete example.

### What the commander is doing (`loc.mode`, `loc.status`)

`loc.mode` is a stable token for the frontend to branch on; `loc.status` is the same thing in words,
shown as the location section's Status row, and the headline leads with it ("Supercruise · Nervi",
"Landed · Nervi / Nervi 2 a", "Jumping to Colonia"). Both come from the game's `Status.json` flags,
re-read every quarter second, so an undock, a supercruise entry or a touchdown reaches viewers within
the app's publish interval (5 s) rather than waiting for the next journal event.

| `mode` | `status` | Meaning |
|---|---|---|
| `docked` | Docked | On a landing pad |
| `landed` | Landed | On a planet's surface in the ship |
| `space` | Flying in normal space | |
| `surface` | Flying low over the surface | Close to a planet (the game reports a latitude and longitude) |
| `supercruise` | In supercruise | |
| `hyperspace` | Jumping to *system* / In hyperspace | Mid-jump; the destination is withheld with the rest of Location |
| `srv` | Driving the SRV | |
| `foot` | On foot | Odyssey, in a station or on a planet |

Both are optional and absent until the game has said (before the first `Status.json`), and a frontend
that does not know them ignores them and keeps using `docked`, so the schema version is unchanged. They
belong to the **Location** section: hiding it hides these, and the jump destination, with it.

### Changing the contract

`v` is the compatibility gate. A frontend that sees a `v` higher than `SUPPORTED_SCHEMA` (in
`js/state.js`) renders a "newer EDNexus" notice rather than a half-parsed card — viewers update
whenever the broadcaster does, but the reverse is not true, so:

- **adding** an optional field needs no version bump (older frontends ignore it);
- **removing or repurposing** one is a bump, in both `StreamCardSchema.Version` and
  `SUPPORTED_SCHEMA` — **in the same change**.

## Publishing contract with the desktop app

The frontend's timing is sized from this, so it is a contract rather than an implementation detail:

- While the game is running and the stream card is on, the app **re-publishes at least every 10
  minutes** even if nothing changed (a heartbeat, with a fresh `at`).
- The app **clears the card on exit** (`DELETE /api/update-state`, which broadcasts `offline`) and when
  the broadcaster switches it off or signs out.

From that, `js/state.js` flags a card as stale after **25 minutes** without an `at` refresh (two missed
heartbeats plus slack) and rechecks the EBS after **12 minutes** of silence. The constants
(`HEARTBEAT_INTERVAL_MS`, `STALE_AFTER_MS`, `RECHECK_AFTER_MS`) live together at the top of the file.

> **Needs the desktop app to match.** `TwitchStreamCardService.DefaultRefreshInterval` is currently six
> hours, which predates this contract: until it is shortened to ten minutes or less, a docked, idle
> commander's card will correctly turn amber after 25 minutes. The EBS's `Ebs__ChannelStateMaxAgeHours`
> (default 24, minimum 12) is a separate backstop for a card whose clear never arrived.

A viewer who opens the channel while the broadcaster is not publishing gets a 404 and shows nothing;
the next heartbeat (at most ten minutes away) reaches them over PubSub.

## Privacy notes

Worth stating in the listing and the privacy policy, and worth knowing before you promise anything:

- **The card is public and real-time.** `GET /api/initial-state/{channelId}` is unauthenticated
  (the channel id is not a secret), and PubSub broadcast messages go to every viewer. So anyone who
  knows a channel id can poll what the card currently shows, whether or not they are watching — which
  makes **stream sniping** possible if the broadcaster shares location. The mitigation is the
  broadcaster's: switch off the sections they would not want a sniper to see.
- **Location off does not hide everything about where you are.** The *System, body & station* switch
  controls `loc` and the one-line headline. The **fleet carrier** section is separate and carries the
  carrier's name and callsign, and a booked jump's destination (`pendingSystem`) and time, so a
  carrier owner who hides their location but leaves the carrier on is still identifiable and, during a
  jump, locatable.
- **What the extension itself collects: nothing.** It requests no Twitch identity, sets no cookies,
  and stores one value in the viewer's `localStorage` (whether the panel is open). The only requests
  are to the Twitch helper script and the EBS (a viewer who clicks the EDNexus mark leaves for the
  project site in a new tab, without the channel page as referrer), which sees the viewer's IP address and the channel id
  like any web server. The EBS retains each channel's last card until it is cleared or ages out
  (`Ebs__ChannelStateMaxAgeHours`).
- This is documentation of the current behaviour, not a change to it: the contract is unchanged.

## Troubleshooting

**The browser console says `Fetch API cannot load https://ednexus.signal-and-thread.com/api/initial-state/…
Refused to connect because it violates the document's Content Security Policy`, and the card shows
"Card unavailable".** Twitch builds the extension's CSP from the console's allowlist, so a host that
is not listed is blocked *before any request is made* — the EBS never sees it, and the EBS being
up does not help. In the developer console, on the version you are testing, **Capabilities → Allowlist
for URL Fetching Domains**, add `https://ednexus.signal-and-thread.com` (exactly `DEFAULT_EBS`; no
trailing slash or path). Twitch locks that field once a version leaves *Local Test*, so for a
version already in Hosted Test or later you may need a new version. Then reload the channel page.
**(verify in console)**

The `postMessage … target origin ('https://supervisor.ext-twitch.tv') does not match … ('https://www.twitch.tv')`
line that often sits beside it is Twitch's own page talking to itself and is harmless.

**The page loads but the card never appears, with no CSP error.** `curl -i
https://ednexus.signal-and-thread.com/api/initial-state/<channelId>`: `404` means the broadcaster is
not publishing (EDNexus closed, the card switched off, or not signed in to Twitch); `200` with
an `access-control-allow-origin` for your extension's `*.ext-twitch.tv` origin means the EBS is
fine. Local Test from `https://localhost:8080` only passes CORS against a local EBS configured as
described under *Against the real Twitch console*.

**A broadcaster saved a position and viewers still see the card on the left.** Twitch's
`configuration.set` gives no result, and a write it ignores looks like one it accepted, so the config
page waits a few seconds for Twitch to echo the value back and says "Twitch has not confirmed the
save yet" if it does not. Reload the config page: a position that is still there was saved. Viewers
who have already moved the card keep their own place by design (their Reset button returns them to
the broadcaster's).

## Conventions

- **Every view loads the Twitch helper first.** `https://extension-files.twitch.tv/helper/v1/twitch-ext.min.js`
  defines `window.Twitch.ext`, which carries auth, the configuration service and the PubSub
  subscription. Without it a view still renders its static HTML but is inert: the overlay stays hidden
  and never receives a snapshot.
- **No inline script or style.** Twitch's extension CSP rejects both; everything lives in `css/` and
  `js/`. CI enforces it.
- **No hardcoded colours.** Every colour is a `--ednx-*` token in `css/tokens.css` (AGENTS.md rule 8),
  and the type scale follows the design system's Label (12px) and Body (13px) steps.
- **No `innerHTML` with payload data.** The snapshot carries commander-authored strings (ship name,
  carrier name, mission targets) that originate in the game and pass through the broadcaster's
  machine before landing in every viewer's browser. `js/card.js` builds nodes and sets
  `textContent`; keep it that way.
- **One link out, and no requests except the Twitch helper and the EBS.** The link is the EDNexus
  mark to the project site, `target="_blank"` with `rel="noopener noreferrer"` (so it neither
  replaces the extension frame nor passes the channel page along), and it is a user click, not a
  request the page makes. The bundle workflow and `dev/test-views.js` both enforce exactly that link.
- **The overlay must stay click-through.** The iframe covers the whole player, so `body` is
  `pointer-events: none` and only the handle and the open panel re-enable it.
- **Vanilla JS, no build step.** What is in this folder is what gets uploaded.
