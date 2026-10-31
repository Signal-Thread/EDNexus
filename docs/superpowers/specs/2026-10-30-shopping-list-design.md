# Unified Shopping List — Design

Date: 2026-10-30 · Branch: `claude/commodity-shopping-list-a62968`

## Goal

One shopping list that holds any item the commander needs to acquire — commodities, engineering
materials (Raw / Manufactured / Encoded) and Odyssey goods (Components, Data, Items, Consumables) —
shows what is still missing against live holdings, can be filled manually or imported from existing
goals (colonisation depot, engineering pin/queue), and links commodity rows to the Trade card's
"where to buy" search.

## Background

Two narrow lists exist today and share nothing:

- `ColonisationSite.BuildShoppingList` — commodities vs. cargo (`ShoppingListItem`).
- `EngineeringPlan.Shopping` — materials vs. inventory (`MaterialRequirement`).

The Market card shows only the docked station's market. The "find a place to buy" search is the
**Trade card** (`TradeCardViewModel.SearchTrade`, Spansh-backed, buy mode via `TradeSelling=false`).
Links therefore target the Trade card, not the Market card.

## Non-goals

Trade-route planning, price totals, overlay/carrier views of the list, buy-search for materials or
Odyssey items (Spansh has no market for them).

## Design

### 1. Core model (`EDNexus.Core/ShoppingList/`, no UI deps)

- `ShoppingItemKind`: `Commodity, Raw, Manufactured, Encoded, Component, Data, Item, Consumable`.
- `ShoppingItemSource`: `Manual, Colonisation, Engineering`.
- `ShoppingEntry` (persisted): `Kind`, `Key` (canonical; `CommodityName.Canonicalize` for
  commodities, catalog symbol otherwise), `Name`, `Needed`, `Source`.
- `ShoppingRow` (derived, not persisted): entry + `Held`, `StillNeeded`, `Satisfied`, `Fraction`.
- `ShoppingListService` owns the entries and projects rows from `CommanderState`
  (cargo → Commodity; `Materials.Raw/Manufactured/Encoded`; `OnFootInventory.*`). It never mutates
  `CommanderState` (convention 1). Held lookups parse defensively and treat unknown keys as 0.

Merge rule: entries are unique by `(Kind, Key)`. Manual quantities and each import source
contribute separately and the effective `Needed` is their sum, so removing one source's
contribution does not disturb the others.

### 2. Imports (live links)

- **Colonisation**: `ImportColonisation(site)` registers a live link; contribution = each
  outstanding resource's `Remaining`. Recomputed from the tracker on each tick; rows disappear when
  the depot resource is complete or the site completes.
- **Engineering**: `ImportEngineering(plan)` links to the pinned blueprint/queue
  (`EngineeringPlanner.Plan`) and the on-foot pin; contribution = per-material `Needed`
  (held is computed by the list itself, not taken from the plan, to avoid double counting).
- Links are stored as `(Source, sourceId)` in settings and re-evaluated against current state;
  a link whose source no longer exists is dropped. `Unlink(source)` removes it on demand.

### 3. Where-to-buy

- Commodity rows expose a **Find** command. `DashboardContext`/`MainWindowViewModel` routes it to
  `TradeCardViewModel`: set `TradeCommodity`, `TradeSelling=false`, default the reference system to
  the current system if empty, run `SearchTrade`, and bring the card into view (un-collapse it).
- Raw/Manufactured/Encoded rows show `MaterialInfo.Source` text and, when short, the top
  `EngineeringPlanner.TradeOptions` swap. Odyssey rows show catalog source text if present, else none.

### 4. Persistence

New `ShoppingListSettings` on `AppSettings` (`Manual` entries, `Links`, `HideCompleted`),
saved through `SettingsStore` like `EngineeringSettings`. Nothing is sent over the network.

### 5. UI (`EDNexus.App`)

`ShoppingListCardViewModel : CardViewModel` (id `shopping`), registered in `MainWindowViewModel.Cards`
and a matching section in `MainWindow.axaml`. Rows: kind icon, name, `held/needed`, source badge,
Find/hint action. Controls: add-item (kind picker + name + qty, name autocompleted from the
engineering/Odyssey catalogs and known commodities), import buttons, hide-completed toggle.
All colours via `Theme.axaml` `{StaticResource}` (convention 8).

### 6. Developer mode & CLI

- `ShoppingListSampleSource : JournalSampleSource`, registered in `DeveloperMode.Sources`
  (convention 7), gated by `FeatureFlags.DeveloperTools`.
- CLI `--once` prints the merged list after the depot/shopping output.

### 7. Testing

xUnit in `EDNexus.Tests`: merge/sum across sources, held lookup per kind, unlink isolation,
import idempotency (re-import does not double), completion drop-off, unknown-key tolerance,
settings round-trip. Validate against real journal via `--once`.

## Risks / open points

- Odyssey item name→key mapping depends on `OdysseyCatalog` coverage; unmatched manual names are
  kept as free-text with `Held = 0` rather than rejected.
- Live-link persistence must tolerate a colonisation site that was never re-seen after restart
  (link kept, rows hidden until the depot is seen again).
