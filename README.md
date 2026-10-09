# EDNexus — Commander Console for Elite Dangerous

<p align="center">
  <img src="assets/logo/ednexus-lockup.svg" width="460" alt="EDNexus — commander console for Elite Dangerous">
</p>

<p align="center">
  <a href="https://github.com/Signal-Thread/EDNexus/actions/workflows/ci.yml"><img src="https://github.com/Signal-Thread/EDNexus/actions/workflows/ci.yml/badge.svg" alt="CI status"></a>
  <a href="https://github.com/Signal-Thread/EDNexus/releases/latest"><img src="https://img.shields.io/github/v/release/Signal-Thread-LLC/EDNexus" alt="Latest release"></a>
  <a href="https://signal-thread.github.io/EDNexus/"><img src="https://img.shields.io/badge/homepage-signal--thread.github.io%2FEDNexus-F07100" alt="Homepage"></a>
</p>

A single, does-it-all **[Elite Dangerous](https://www.elitedangerous.com/) commander console** — a
free, open-source tool that replaces the sprawl of separate market, trade route, engineering,
exobiology, colonisation, and materials tools with one desktop app for Windows. A Flatpak bundle
for Linux (including Steam Deck) is attached to each release; macOS is not yet supported. The
in-game overlay and voice callouts are Windows-only.

**[📖 Homepage & downloads](https://signal-thread.github.io/EDNexus/)** ·
**[⬇ Latest release](https://github.com/Signal-Thread/EDNexus/releases/latest)** ·
**[🐛 Report an issue](https://github.com/Signal-Thread/EDNexus/issues)**

It works off the game's own data: a watcher tails the journal (`Journal.*.log`) and the sidecar
status files (`Status.json`, `Cargo.json`, `Market.json`, …), turns them into a typed event stream,
and folds that into a single live commander state that every feature reads from.

## Features

- **Colonisation tracker** — construction depot progress, commodity shopping lists, and hauling
  progress for system colonisation.
- **Market & trade route search** — commodity market lookups and profitable trade route plotting,
  powered by [Spansh](https://spansh.co.uk/) and [EDSM](https://www.edsm.net/).
- **Engineering** — blueprint pinning, material shopping lists across every engineering grade and
  roll count, and Odyssey on-foot suit/weapon upgrades.
- **Materials & exobiology** — raw/manufactured/encoded material tracking and exobiology genus/species
  scan data as you play.
- **EDDN & Inara integration** — opt-in, anonymized contributions to the
  [Elite Dangerous Data Network](https://github.com/EDCD/EDDN), and opt-in sync of your commander
  profile to [Inara](https://inara.cz).
- **Missions, community goals & Galnet** — held missions (including massacre stacks), active
  community goals, and the Galnet news feed.
- **Ranks** — Combat, Trade, Exploration, Exobiologist and Mercenary ranks with progress to the next tier.
- **Mining** — prospector results, session yield, a "worth mining" price threshold (prices learned
  from the markets you visit) and remembered surface mining spots.
- **Radio** — a background internet-radio player with station, volume and mute remembered.
- **In-game overlay & voice callouts** — a transparent click-through HUD over the game and spoken
  callouts (fuel low, scan complete, shopping-list item acquired). Windows-only; both are off by default.
- **Discord Rich Presence** — mirrors your system/ship/activity onto your own Discord client, with
  options to hide the system and commander name.
- **Twitch stream card** — publishes a viewer-facing card of your session to a Twitch extension
  through the EDNexus Extension Backend Service (EBS). Opt-in; you choose which sections are shown.
- **Plugin SDK (in development)** — `EDNexus.Plugins.Abstractions` and `EDNexus.Plugins.Hosting`
  define the plugin contract and loader; the desktop app does not load plugins yet.
- **Live journal parsing** — a watcher tails the game's own `Journal.*.log` and status sidecar files
  (no third-party account or API key required to get started).
- **Auto-update (Windows)** — opt-in: downloads the new installer, checks it against the SHA-256
  checksum published with the release, and only then offers to install it. Linux updates come
  through Flatpak.

## Stack

- **.NET 10** — cross-platform (Windows / Linux / macOS)
- **Avalonia 12** — the desktop UI
- **CommunityToolkit.Mvvm** — view models

## Design

The desktop app and the Twitch extension share one dark HUD palette, type scale, and icon set,
documented in the **[EDNexus design system](https://claude.ai/artifact/8G7pW33gGu6SFvVockTpcd)**.
The tokens it documents are wired into the code as shared constants —
`src/EDNexus.App/Themes/Theme.axaml` for the desktop app, `extension/css/tokens.css` for the
extension — rather than duplicated hex values, so changing a color means editing one file, not
every window.

## Projects

| Project | Role |
|---|---|
| `src/EDNexus.Core` | Engine: journal watcher → event bus → commander state, the feature services (colonisation, market, routes, mining, radio, Discord presence, Twitch stream card, …), the reporting bridge and the updater |
| `src/EDNexus.App` | Avalonia dashboard |
| `src/EDNexus.Cli` | Headless harness (`--once` replays the latest journal and prints state) |
| `src/EDNexus.Ebs` | Twitch Extension Backend Service: relays the commander's stream card to viewers (ASP.NET, Docker image) |
| `extension/` | The Twitch extension frontend (vanilla HTML/CSS/JS, no build step) |
| `src/EDNexus.Plugins.Abstractions` | Plugin SDK contract (interfaces a plugin implements) |
| `src/EDNexus.Plugins.Hosting` | Plugin package loader / sandbox host |
| `src/EliteDangerous.Eddn` | Standalone, reusable EDDN upload client (no EDNexus dependency) |
| `src/EliteDangerous.Inara` | Standalone, reusable Inara API client (no EDNexus dependency) |
| `src/EliteDangerous.Spansh` | Standalone Spansh route/search client |
| `src/EliteDangerous.Edsm` | Standalone EDSM client |
| `src/EliteDangerous.Galnet` | Standalone Galnet news-feed client |
| `src/EliteDangerous.RavenColonial` | Standalone RavenColonial (shared colonisation project) client |
| `tests/` | xUnit test projects |

## Running

```sh
# Live dashboard
dotnet run --project src/EDNexus.App

# Headless: replay the latest journal and print current state
dotnet run --project src/EDNexus.Cli -- --once

# What do I still need for grade 5 Increased FSD Range, over 3 rolls?
# (omit the blueprint id to list every blueprint that can be planned)
dotnet run --project src/EDNexus.Cli -- --once --plan fsd_increased_range 5 3
```

The journal folder is auto-detected (Windows Saved Games, and the Steam/Proton prefix on Linux).
Override it with the `EDNEXUS_JOURNAL_DIR` environment variable.

Run the unit tests with:

```sh
dotnet test EDNexus.slnx
```

## Privacy & crash reporting

EDNexus can send **anonymized** crash and error reports (via [Sentry](https://sentry.io)) so bugs
get found and fixed. It is **opt-in**: nothing is sent until you agree to the first-run prompt, and
you can change your mind any time in **Settings**.

**What is sent** (only with your consent):
- App version, operating system, and the error with its stack trace
- A random *install id* generated on your machine — not linked to your commander, account, or OS user

**What is never sent:**
- Your commander name, systems visited, or any journal contents
- Your OS/user name, or file paths that contain it (scrubbed before sending — see `PiiScrubber`)

The Sentry DSN is **not stored in this repository**. It is injected at release-build time from a CI
secret (`SENTRY_DSN`), so source builds have no DSN and reporting stays disabled. Developers can set
`EDNEXUS_SENTRY_DSN` locally to test.

## Everything EDNexus sends over the network

EDNexus reads the game's journal locally and only talks to the services below. Nothing is sent
that is not listed here.

| What | Where | Sent when | Contents |
|---|---|---|---|
| Crash reports | Sentry | **Opt-in** (first-run prompt / Settings) | See above |
| Market/scan/travel data | EDDN | **Opt-in**, live events only | Anonymized game-world data |
| Commander sync | Inara | **Opt-in**, needs your Inara API key | Identity, credits, ranks, travel (see below) |
| Stream card | EDNexus EBS (`ednexus.signal-and-thread.com`) → Twitch viewers | **Opt-in**: you log in with Twitch and switch the card on | A public snapshot of the sections you enable: commander name and rank, ship, location, carrier, exobiology, mining, missions, cargo. The credit balance is off by default. Cleared when you switch it off or sign out |
| Discord Rich Presence | Your own Discord client (local IPC, not an upload by EDNexus) | When enabled in Settings | System/ship/activity; options hide the system and the commander name |
| Colonisation lookups (read) | Raven Colonial | When you dock at a construction depot, unless switched off in Settings → Data reporting | The depot's system name and market id, then the matched project's id |
| Colonisation deliveries (write) | Raven Colonial | **Opt-in, default off**; each live delivery you make at a depot that has a shared project, and only while the lookup above is also on | **Your commander name**, the project id, and the commodities and tons you delivered (see below) |
| Route & market search | Spansh, EDSM | When you run a search | The search/route parameters (systems, ranges, commodities) |
| News | Galnet | When the Galnet card refreshes | A plain request for the public RSS feed |
| Update check | GitHub (api.github.com / github.com) | Only if **Automatically download updates on startup** is on, or you press **Check for updates now** | A request for the latest release; no identifying data and no token is sent |

Replaying an old journal (e.g. the CLI `--once` harness) never uploads anything.

**Sharing your colonisation deliveries (Raven Colonial).** Turning on *Also share my deliveries with that
project* (Settings → Data reporting) reports each delivery you make at a construction depot to the shared
[Raven Colonial](https://ravencolonial.com) project for that depot, so squadmates see the remaining need fall.
Two caveats, both on Raven Colonial's side rather than something EDNexus can fix:

- **It is unauthenticated.** The commander is just a name in the request URL, so Raven Colonial cannot tell your
  delivery from one somebody else posted under your name (or you under theirs). Only enable it for projects you
  are comfortable being open to that.
- **It can double-count.** Raven Colonial adds up whatever it is sent. If another tool that also reports
  deliveries (SrvSurvey, for example) is running against the same project, each delivery is counted twice.
  Use one reporter per project.

Only live deliveries are sent (never a replayed journal, never developer-mode data), commodities the project does
not list are skipped, and a failed send is logged and not queued for a later re-send.

## Data reporting (EDDN & Inara)

EDNexus can feed the two community services every commander tool is expected to. Both are **opt-in,
default off**, and are toggled in **Settings**:

- **EDDN** — contributes **anonymized** market, scan, and travel data to the
  [Elite Dangerous Data Network](https://github.com/EDCD/EDDN). The relay obfuscates the uploader id,
  and messages carry only game-world data (no personal identity). Uploads happen live as you play.
- **Inara** — syncs **your** commander (identity, credits, ranks, and travel) to your
  [Inara](https://inara.cz) profile using your personal Inara API key. To respect Inara's rate
  guidance, it only sends on session start, docking, FSD jumps, and session end — never continuously.

Neither reporter sends anything until you enable it. Replaying an old journal (e.g. the CLI `--once`
harness) never transmits — only live events are reported.

The two clients live in **standalone libraries** (`EliteDangerous.Eddn`, `EliteDangerous.Inara`) with
no dependency on the EDNexus engine, so they can be reused by other tools or split out later. EDNexus
drives them through a small bridge in `EDNexus.Core/Reporting`.

> Filling gaps the journal misses via the **Frontier CAPI** is planned but not yet implemented — it
> needs an approved Frontier developer client id. The reporting layer leaves a clean seam for it.

## Installation

Installers are self-contained (no separate .NET install needed).

- **Windows** — run `EDNexus-<version>-setup.exe`. Installs to
  `C:\Program Files\Signal & Thread\EDNexus\` (path is changeable in the wizard). Built with
  [Inno Setup](https://jrsoftware.org/isinfo.php). Requires Windows 10 (1607) or newer, 64-bit.
- **Linux (including Steam Deck)** — install the `EDNexus-<version>.flatpak` bundle attached to the
  release (`flatpak install --user ./EDNexus-<version>.flatpak`); see
  [`packaging/flatpak/`](packaging/flatpak/). There is no in-app self-update on Linux; reinstall the
  newer bundle or update through Flathub once listed. The overlay and voice callouts are not
  available on Linux, and the radio needs the system's LibVLC.

macOS is not yet supported.

Preferences are stored in **`%LOCALAPPDATA%\EDNexus`**, alongside the logs — never in the install
directory, so the app folder can stay read-only.
They deliberately do **not** live in Documents: that folder is commonly cloud-synced (OneDrive
Known Folder Move), which would copy the settings — including your Inara API key — off-machine, and
invite sync conflicts on a file that is rewritten every time you tweak the dashboard. An older
install's settings are migrated out of `Documents\EDNexus` automatically on first run.

To carry your **dashboard arrangement** between machines, use **Settings → Dashboard → Export…**
and **Import…**. That file contains only the card layout — never your API key — so it is safe to
sync, share or keep in version control.

### Building the installers locally

```powershell
# Windows (needs Inno Setup 6: choco install innosetup)
installer\windows\build.ps1 -Version 0.1.0
```
```sh
# Linux (self-contained payload the Flatpak consumes; prints the tarball + sha256)
packaging/flatpak/build-flatpak.sh 0.1.0
```

## Releases

Tagging a release (`git tag v0.1.0 && git push --tags`) triggers `.github/workflows/release.yml`,
(after the test suite passes) builds the Windows installer, the Linux self-contained tarball (the
payload the Flatpak consumes) and the `.flatpak` bundle, with a `.sha256` checksum beside every file —
injecting the DSN from the `SENTRY_DSN` secret (and uploading debug symbols when
`SENTRY_AUTH_TOKEN` is set) — and attaches them to a GitHub Release. See the workflow header for
the required Actions secrets. The Windows installer is not yet Authenticode-signed (the workflow has
an optional signing step that activates when signing secrets are configured), so Windows SmartScreen
may warn on first run.

## Roadmap

- [x] Journal engine (watcher, event bus, commander state)
- [x] Avalonia dashboard shell
- [x] Colonisation tracker (construction depots, shopping lists, hauling progress)
- [x] Market / trade search + route plotting (Spansh, EDSM)
- [x] Engineering (blueprint pinning, material guidance, Odyssey on-foot upgrades)
- [x] Materials & exobiology tracking
- [x] Missions, community goals & Galnet news
- [x] Progression & rank trackers
- [x] In-game overlay + voice callouts (Windows)
- [x] Mining, radio, Discord Rich Presence
- [x] Twitch stream card (extension + EBS)
- [ ] Plugin loading in the desktop app (SDK and host exist)
- [ ] Signed Windows installer
- [ ] macOS build
