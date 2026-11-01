# AGENTS.md

Operating guide for humans and AI agents working on **EDNexus**. Read this before making changes.

## What this is

A single cross-platform desktop app that replaces the sprawl of Elite Dangerous companion tools
(market, routes, exobiology, colonisation, materials). It is driven entirely off the game's own
data — no memory reading, no injection.

## Build / run / test

```sh
# Build everything (note: .NET 10 uses the .slnx solution format)
dotnet build EDNexus.slnx

# Run the desktop app
dotnet run --project src/EDNexus.App

# Headless engine harness — replays the latest journal and prints commander state
# (plus the active colonisation depot + shopping list, when one is in range).
# This is the fastest way to validate engine/feature changes against real data.
dotnet run --project src/EDNexus.Cli -- --once

# Cost an engineering roll against the live inventory (shopping list + trader hints).
# Omit the blueprint id to list every plannable blueprint.
dotnet run --project src/EDNexus.Cli -- --once --plan fsd_increased_range 5 3

# Print the exact payload the Twitch extension would show viewers, from the live journal.
# Add --show-credits to include the balance (withheld by default, as in the app).
dotnet run --project src/EDNexus.Cli -- --twitch-card

# Load plugins (EDNEXUS_PLUGINS_DIR or the default folder) before the replay and print a
# per-plugin summary; `--plugin <folder>` loads just one plugin folder. All declared
# capabilities are granted headlessly; UI registrations are counted, not rendered.
dotnet run --project src/EDNexus.Cli -- --once --plugins

# Unit tests (xUnit).
dotnet test EDNexus.slnx
```

The CLI `--once` harness is the primary way to validate against real journal data; `EDNexus.Tests`
covers the pure engine logic. Both should stay green.

## Architecture

```
Journal.*.log + *.json  ──►  JournalWatcher  ──►  JournalEventBus  ──►  StateTracker  ──►  CommanderState
                                                        │                                        │
                                             feature modules subscribe                 UI / overlay / voice read
```

| Project | Role |
|---|---|
| `src/EDNexus.Core` | Engine + feature services. No UI dependencies. |
| `src/EDNexus.App` | Avalonia 12 desktop UI (MVVM via CommunityToolkit.Mvvm). |
| `src/EDNexus.Cli` | Headless harness for validation. |
| `src/EDNexus.Ebs` | Twitch Extension Backend Service: relays commander state to viewers. |
| `extension/` | The Twitch extension frontend (vanilla HTML/CSS/JS, no build step). |

Key types live in `src/EDNexus.Core`: `JournalWatcher`, `JournalEntry`, `JournalEventBus`,
`StateTracker`, `CommanderState`, `EngineHost`.

## Design system

The palette, type scale, spacing/radius, component patterns and icon/logo assets shared by the
desktop app and the Twitch extension are documented in the **EDNexus design system**:
<https://claude.ai/artifact/8G7pW33gGu6SFvVockTpcd>. It is the source of truth for both surfaces —
consult it before adding a color, font size, spacing value, or icon, rather than picking a new one
ad hoc. The actual token values live in code, not just the doc:

- Desktop: `src/EDNexus.App/Themes/Theme.axaml` (a global `ResourceDictionary` merged in
  `App.axaml`) — reference brushes with `{StaticResource ...}`, never a literal hex color.
- Extension: `extension/css/tokens.css`, imported by both `extension/css/card.css` and
  `extension/css/config.css` — reference `var(--ednx-...)`, never a literal hex color.
- Brand assets (logo, app icon, the overlay's outline icon set) live under `assets/` in this repo
  and are mirrored into the design system doc as real files, not descriptions.

## Conventions (do not violate without discussion)

1. **One writer.** Only `StateTracker` mutates `CommanderState`. Feature modules and the UI *read*
   it. New event handling goes through `StateTracker` or a dedicated feature service that owns its
   own derived state.
2. **Parse defensively.** Journal events change between game updates. Read fields off the raw
   `JsonElement` via `JournalEntry` helpers; never assume a field exists. Unknown events must not
   throw — the bus isolates handler errors, but don't rely on that.
3. **Prefer `_Localised` names** for anything shown to the user (`GetLocalised(...)`).
4. **Cross-platform first.** Target behaviour that works on Windows, Linux, and macOS. Anything
   OS-specific (overlay, native TTS) goes behind an interface with a no-op fallback.
5. **Never commit** real journal files, tokens, or `bin/`/`obj/`.
6. **Match surrounding style** — nullable enabled, file-scoped namespaces, XML docs on public types.
7. **Developer mode.** Every card is exercisable without the game running via `EDNexus.Core.Dev`:
   each feature inherits a `JournalSampleSource` that emits random-but-valid journal events through
   the real bus (so it also exercises the parsers). When you add a card, add a matching sample
   source and register it in `DeveloperMode.Sources`. It's enabled from **Settings → Developer
   Options** (off every launch, not persisted) and the whole subsystem is gated by
   `FeatureFlags.DeveloperTools` — set the `DISABLE_DEVTOOLS` build symbol or `EDNEXUS_DEVTOOLS=false`
   to strip it. Keep dev tooling behind that flag.
8. **No hardcoded colors.** Every color in `src/EDNexus.App` comes from `Themes/Theme.axaml` via
   `{StaticResource ...}`; every color in `extension/` comes from `extension/css/tokens.css` via
   `var(--ednx-...)`. See **Design system** above before introducing a new one.

## How work is organised

- The roadmap lives in the **Epic** (issue #7) and per-phase issues, each tied to a milestone.
- Each phase ships on a branch → PR → `main`. Reference the issue with `Closes #N`.
- Tasks labelled **`agent-ready`** are scoped tightly enough to hand to an agent; use the
  **🤖 Agent task** issue form to create more.

## Definition of done

- Builds clean with no new warnings.
- Validated against real journal data (via the app or `--once` harness).
- Honours the conventions above.
- PR description says what was verified and how.
