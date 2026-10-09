# EDNexus Flatpak (Flathub-ready)

Flatpak is the packaging target for **SteamOS / Steam Deck** — the Deck's root filesystem is
immutable, so `.deb`/`pacman` installs don't stick, and Flathub is preconfigured in Desktop
Mode's Discover store. The same build also runs on Ubuntu, Debian, Fedora, Arch, etc.

## Files

| File | Purpose |
|------|---------|
| `io.github.Signal_Thread_LLC.EDNexus.yml` | The Flatpak manifest |
| `io.github.Signal_Thread_LLC.EDNexus.metainfo.xml` | AppStream metadata (**required** by Flathub) |
| `io.github.Signal_Thread_LLC.EDNexus.desktop` | Desktop entry |
| `ednexus` | In-sandbox launcher (`/app/bin/ednexus` → the .NET apphost) |
| `build-flatpak.sh` | Publishes the payload tarball **and** the standalone `.flatpak` bundle |

The app ID is `io.github.Signal_Thread_LLC.EDNexus` (hyphens aren't allowed in Flatpak IDs,
so the `Signal-Thread-LLC` org becomes `Signal_Thread_LLC`). If you'd rather verify the
`signal-and-thread.org` domain on Flathub, rename everything to `org.signal_and_thread.EDNexus`.

## Two ways to ship

1. **Standalone `.flatpak` bundle** — a single file users download from the GitHub Release and
   sideload. No Flathub account or review needed; good for immediate distribution.
   ```sh
   flatpak install --user ./EDNexus-<version>.flatpak
   flatpak run io.github.Signal_Thread_LLC.EDNexus
   ```
   The bundle carries the app, not the runtime; `flatpak` pulls `org.freedesktop.Platform`
   from Flathub on install (Steam Deck has Flathub preconfigured). Trade-off vs. Flathub:
   **no automatic updates** — the user reinstalls to upgrade.

2. **Flathub** — listed in software centres (incl. the Deck's Discover) with automatic updates.
   Requires a submission (see below) and consumes the payload tarball.

Both artifacts are produced by `build-flatpak.sh` and attached to each GitHub Release by
`.github/workflows/release.yml`.

## Why a pre-built payload (not build-from-source)

Flathub's builders block network access during the build, so `dotnet restore` can't run there.
Since EDNexus already publishes a **self-contained** bundle (its own .NET runtime, no ICU via
`InvariantGlobalization`), the manifest consumes that bundle as a checksummed `archive` source.
The Windows installer is likewise built from a self-contained publish, so each platform ships
one pre-built artifact.

## Local build & test

```sh
# 1. Publish the payload (creates ./publish and ./out/*.tar.gz, prints the sha256)
./build-flatpak.sh 0.1.0

# 2. For a fast local loop, swap the manifest's `archive` source for the publish dir:
#      - type: dir
#        path: publish
#        dest: app-payload
#    (leave the other file: sources as-is), then:
flatpak install -y flathub org.freedesktop.Platform//24.08 org.freedesktop.Sdk//24.08
flatpak-builder --user --install --force-clean build-dir \
  io.github.Signal_Thread_LLC.EDNexus.yml
flatpak run io.github.Signal_Thread_LLC.EDNexus
```

Validate the metadata the way Flathub's CI does:

```sh
flatpak run --command=flatpak-builder-lint org.flatpak.Builder manifest \
  io.github.Signal_Thread_LLC.EDNexus.yml
appstreamcli validate io.github.Signal_Thread_LLC.EDNexus.metainfo.xml
```

## Submitting to Flathub

1. Replace the manifest's `archive` source with the real Release URL + the `sha256` that
   `build-flatpak.sh` printed (revert the local `dir` swap).
2. Add a `<screenshots>` entry with at least one **publicly reachable** image URL to the
   `.metainfo.xml` (Flathub rejects submissions without one; none is hosted yet, so the element
   is currently omitted) and set the correct `<project_license>`.
3. Open a PR at [github.com/flathub/flathub](https://github.com/flathub/flathub) adding a repo
   named after the app ID, with the manifest + these sibling files.

## Sandbox permissions

Besides the Steam/Proton journal paths below, the manifest grants `--share=network` (EDDN, Inara,
EDSM, Spansh, Galnet, RavenColonial, Twitch), `--socket=pulseaudio` (radio audio) and
`--filesystem=xdg-run/discord-ipc-0` � `-9` and `xdg-run/app/com.discordapp.Discord` (Discord Rich Presence; Flatpak permissions do not accept wildcards). Note the radio card needs LibVLC,
which is not part of `org.freedesktop.Platform`; without it the radio reports an error state
rather than crashing.

## Journal detection under the sandbox (read this)

The manifest grants **read-only** access to the Steam/Proton journal locations:

- `~/.local/share/Steam` and `~/.steam` — the default Deck/desktop libraries
- `/run/media` — games moved to a Steam Deck microSD card

Inside a Flatpak, `$HOME` is redirected to the app's private dir
(`~/.var/app/io.github.Signal_Thread_LLC.EDNexus`), whereas the grants above expose the real
journals at their host paths (e.g. `/home/deck/.local/share/Steam/...`).
[`JournalPaths`](../../src/EDNexus.Core/Journal/JournalPaths.cs) handles this: when `$FLATPAK_ID`
is set it strips the `.var/app/<app-id>` suffix off `$HOME` to recover the real host home before
building the Steam/Proton candidates, and it also scans `/run/media` for microSD Steam libraries.
So auto-detection works out of the box under the sandbox — no manual folder picking needed.

Users can still override with `EDNEXUS_JOURNAL_DIR` for non-standard setups. App settings are
unaffected: they write to the private `$HOME`, which is persistent and writable.
