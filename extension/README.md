# EDNexus Twitch Extension — commander card

The viewer-facing half of EDNexus's Twitch integration: a **flyout card** drawn over the stream.
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

## Layout

| Path | Role |
|---|---|
| `video_overlay.html` | The overlay itself — the handle plus the panel. |
| `config.html` | Broadcaster config page (which EBS this channel reads from). |
| `css/card.css` | Overlay styling. Click-through everywhere the card is not. |
| `css/config.css` | Config page styling. |
| `js/state.js` | Transport: initial-state fetch + PubSub subscription. With a card on screen and nothing heard for 5 minutes, it asks `/api/initial-state` again, so a viewer who missed the "offline" broadcast drops the card (404) or picks up a newer one. No rendering. |
| `js/card.js` | Rendering and flyout behaviour. No network. |
| `dev/sample-state.json` | A representative payload for local development. Not needed in the upload. |
| `dev/serve-https.py` | Serves this folder over TLS for Twitch local testing. Not needed in the upload. |

## Developing locally

No Twitch and no EBS required — `?mock=1` renders the bundled sample payload with neither in the
loop. Use the flag explicitly: both views load Twitch's helper from its CDN, so `window.Twitch.ext`
exists even outside Twitch and the card would otherwise wait forever for an `onAuthorized` that
never comes.

```bash
python -m http.server 8931 --directory extension
```

Then open <http://127.0.0.1:8931/video_overlay.html?mock=1>.

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

# Terminal 1 — the extension assets, matching the console's Base URI.
python extension/dev/serve-https.py            # https://localhost:8080/

# Terminal 2 — the EBS.
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "https://localhost:8787"
dotnet run --project src/EDNexus.Ebs --no-launch-profile
```

Then in the extension's developer console set **Base URI** `https://localhost:8080/`, **Video —
Fullscreen Viewer Path** `video_overlay.html`, **Configuration Path** `config.html`; and in the
desktop app set **Settings → Twitch → Backend service** to `https://localhost:8787`.

`src/EDNexus.Ebs/appsettings.Development.json` (git-ignored) already allows `https://localhost:8080`
as a frontend origin, so `GET /api/initial-state/{channelId}` passes CORS. Add the
[Twitch Developer Rig](https://dev.twitch.tv/docs/extensions/rig/)'s origin there too if you use it.

## Publishing to Twitch

The extension is configured in the [Twitch Developer Console](https://dev.twitch.tv/console/extensions),
not by a file in this folder. Settings that matter:

- **Type**: **Video — Fullscreen**, and only that one — the type that hands the extension the whole
  video area as a transparent canvas, which is what `video_overlay.html` is written against. Leave
  Panel, Video — Component and Mobile unticked: this folder ships one viewer path, and Twitch's
  review exercises every type a version declares. Note the cost — a video extension does not render
  for mobile app viewers, who will see nothing until a Panel view exists.
- **Viewer path**: `video_overlay.html` (the name predates the console's current type labels).
  **Config path**: `config.html`.
- **Testing base URI**: your local server while developing; the asset upload is used once hosted.
- **Required Broadcaster Abilities**: none. The card only reads.
- The extension's **Client ID** and **Secret** go into the EBS as `Twitch:ExtensionId` and
  `Twitch:ExtensionSecret` — see `src/EDNexus.Ebs/README.md`.

**Don't zip this by hand.** The `Twitch extension bundle` workflow builds it on every change to
`extension/` and attaches `ednexus-twitch-extension.zip` to the run as an artifact — download that
and upload it as the extension's asset bundle. Tagged releases get it attached to the release too.

The workflow also guards the two mistakes that only surface as 404s *after* upload: it fails if the
viewer paths are missing, if a view has lost the Twitch helper script, if `dev/` leaked in, or if the
files end up inside a wrapping folder instead of at the archive root.

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
  "loc":      { "system", "body", "docked", "station", "stationType" },
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
never assume a field is present. `dev/sample-state.json` is a complete example.

### Changing the contract

`v` is the compatibility gate. A frontend that sees a `v` higher than `SUPPORTED_SCHEMA` (in
`js/state.js`) renders a "newer EDNexus" notice rather than a half-parsed card — viewers update
whenever the broadcaster does, but the reverse is not true, so:

- **adding** an optional field needs no version bump (older frontends ignore it);
- **removing or repurposing** one is a bump, in both `StreamCardSchema.Version` and
  `SUPPORTED_SCHEMA`.

## Conventions

- **Every view loads the Twitch helper first.** `https://extension-files.twitch.tv/helper/v1/twitch-ext.min.js`
  defines `window.Twitch.ext`, which carries auth, the configuration service and the PubSub
  subscription. Without it a view still renders its static HTML but is inert: the config page cannot
  read or save anything, and the overlay never receives a snapshot.
- **No inline script or style.** Twitch's extension CSP rejects both; everything lives in `css/` and
  `js/`.
- **No `innerHTML` with payload data.** The snapshot carries commander-authored strings (ship name,
  carrier name, mission targets) that originate in the game and pass through the broadcaster's
  machine before landing in every viewer's browser. `js/card.js` builds nodes and sets
  `textContent`; keep it that way.
- **The overlay must stay click-through.** The iframe covers the whole player, so `body` is
  `pointer-events: none` and only the handle and the open panel re-enable it.
- **Vanilla JS, no build step.** What is in this folder is what gets uploaded.
