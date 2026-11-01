# Unified Shopping List Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One Shopping List card holding commodities, engineering materials and Odyssey goods, fed by manual entries and live links to the colonisation depot and engineering pins, with a "Find" action that runs the Trade card's buy search.

**Architecture:** A pure projection in `EDNexus.Core/ShoppingList/` (entries + current `CommanderState` → rows), in the style of `EngineeringPlanner`. Persisted state is a `ShoppingListSettings` on `AppSettings`. A `ShoppingListCardViewModel` re-projects each tick; "Find" is routed to `TradeCardViewModel` through `DashboardContext`.

**Tech Stack:** .NET 10, Avalonia 12, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-30-shopping-list-design.md`

## Global Constraints

- Only `StateTracker` mutates `CommanderState`; the shopping list only reads it (AGENTS.md #1).
- Parse defensively; unknown keys/kinds never throw (AGENTS.md #2).
- Every colour comes from `Themes/Theme.axaml` via `{StaticResource ...}`; no hex literals (AGENTS.md #8).
- File-scoped namespaces, nullable enabled, XML docs on public types (AGENTS.md #6).
- Dev tooling stays behind `FeatureFlags.DeveloperTools`; a sample source is registered in `DeveloperMode.Sources` (AGENTS.md #7).
- Nothing is sent over the network by the list itself; "Find" reuses the existing Trade search.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Build/test: `dotnet build EDNexus.slnx`, `dotnet test EDNexus.slnx` — no new warnings.

## Review Focus

- Same item added manually and via an import (e.g. Steel from the depot + 10 manual): one row, summed `Needed`. (Task 1)
- Journal-form cargo names (`"$steel_name;"`, `"lowtemperaturediamond"`) vs display names must hit the same held count. (Task 1)
- Unknown manual name / unknown Odyssey symbol: kept as free text with `Held = 0`, no exception. (Task 2)
- Colonisation link on but no active depot, or engineering link on with nothing pinned: empty contribution, not an error. (Task 2)
- Settings file written by an older build (no `Shopping` section) loads with defaults. (Task 3)

---

### Task 1: Core model and projection

**Files:**
- Create: `src/EDNexus.Core/ShoppingList/ShoppingModels.cs`, `src/EDNexus.Core/ShoppingList/ShoppingListService.cs`
- Test: `tests/EDNexus.Tests/ShoppingList/ShoppingListServiceTests.cs`

**Interfaces:**
- Produces (namespace `EDNexus.Core.ShoppingList`):
  - `enum ShoppingItemKind { Commodity, Raw, Manufactured, Encoded, Component, Data, Item, Consumable }` and `enum ShoppingItemSource { Manual, Colonisation, Engineering }`, both `[JsonConverter(typeof(JsonStringEnumConverter))]`.
  - `sealed record ShoppingEntry(ShoppingItemKind Kind, string Key, string Name, int Needed, ShoppingItemSource Source = ShoppingItemSource.Manual)`
  - `sealed record ShoppingRow(ShoppingItemKind Kind, string Key, string Name, int Needed, int Held, IReadOnlyList<ShoppingItemSource> Sources, string Hint)` with computed `StillNeeded`, `Satisfied`, `Fraction` (0..1), `CanFind` (`Kind == Commodity && !Satisfied`).
  - `static class ShoppingListService` with `static IReadOnlyList<ShoppingRow> Build(IEnumerable<ShoppingEntry> entries, CommanderState state, bool hideCompleted = false)` and `internal static int Held(ShoppingItemKind kind, string key, CommanderState state)`.

- [ ] **Step 1: Write failing tests** in `ShoppingListServiceTests`:
  - `Held_reads_each_inventory`: set `state.Cargo["steel"]=5`, `state.Materials.Raw["iron"]=7`, `state.Materials.Manufactured["shieldemitters"]=2`, `state.Materials.Encoded["shielddensityreports"]=3`, `state.OnFoot.Components["circuitboard"]=4` (and Data/Item/Consumable likewise); assert `Held` returns each.
  - `Commodity_key_matches_journal_and_display_forms`: cargo `"lowtemperaturediamond"=3`; entry key `"Low Temperature Diamonds"` → row `Held == 3`.
  - `Duplicate_entries_merge_and_sum`: `(Commodity,"Steel",100,Colonisation)` + `(Commodity,"steel",10,Manual)` → one row, `Needed == 110`, `Sources` contains both.
  - `Rows_sort_unsatisfied_first_by_shortfall` and `HideCompleted_drops_satisfied_rows`.
  - `Unknown_kind_key_yields_zero_held_without_throwing`.
- [ ] **Step 2: Run** `dotnet test EDNexus.slnx --filter ShoppingListServiceTests` — Expected: FAIL (types missing).
- [ ] **Step 3: Implement** the models and `ShoppingListService.Build`. Normalise commodity keys with `CommodityName.Canonicalize`, other kinds with `Trim().ToLowerInvariant()`; group on `(Kind, normalised key)`; `Held` for commodities uses a canonicalised copy of `state.Cargo` (as `ColonisationSite.BuildHoldLookup` does). `Hint` is empty here (Task 2 fills it).
- [ ] **Step 4: Run** the same command — Expected: PASS.
- [ ] **Step 5: Commit** `feat(shopping): core shopping list projection`.

### Task 2: Importers and name resolution

**Files:**
- Modify: `src/EDNexus.Core/ShoppingList/ShoppingListService.cs`
- Test: `tests/EDNexus.Tests/ShoppingList/ShoppingListImportTests.cs`

**Interfaces:**
- Consumes: Task 1 types; `ColonisationSite.Outstanding` (`Symbol`, `Name`, `Remaining`); `EngineeringTracker.BuildPlan(id, grade, state, rolls)` → `PinnedBlueprintPlan.Materials` (`MaterialRequirement` with `Symbol, Name, Category, Needed, Source`); `BuildSuitUpgradePlan`/`BuildWeaponUpgradePlan` → `Materials` (`UpgradeRequirement` with `Symbol, Name, Category, Needed, Source`); `EngineeringSettings` pin fields.
- Produces:
  - `static IReadOnlyList<ShoppingEntry> FromColonisation(ColonisationSite? site)` — Commodity entries, `Needed = Remaining`, source `Colonisation`; empty for null.
  - `static IReadOnlyList<ShoppingEntry> FromEngineering(EngineeringSettings pin, EngineeringTracker engineering, CommanderState state)` — ship pin at 1 roll plus on-foot pin; category `Raw/Manufactured/Encoded` → those kinds, `Component/Data/Item/Consumable` → those kinds, anything else skipped; source `Engineering`; empty when nothing pinned.
  - `static (string Key, string Name) Resolve(ShoppingItemKind kind, string text)` — match `text` against the engineering `Materials` (Raw/Manufactured/Encoded) or `OdysseyCatalog.Materials` (Component/Data/Item/Consumable) by symbol or name, case-insensitively; commodities resolve via `CommodityName.Canonicalize` keeping `text` as the name; no match returns `(text.Trim().ToLowerInvariant(), text.Trim())`.
  - `Build` now fills `Row.Hint` with the catalog `Source` text for non-commodity rows when short.

- [ ] **Step 1: Write failing tests**: colonisation site (build via `ColonisationTracker` + the `Depot` JSON used in `ColonisationTrackerTests`) yields Aluminium 60 and CMM Composite 500 but not completed Steel; null site → empty; pinned `fsd_increased_range` grade 5 returns entries tagged `Engineering` with correct kinds; empty pin → empty; `Resolve(Raw, "Iron")` returns the catalog symbol; `Resolve(Component, "Nonexistent Thing")` returns free text without throwing.
- [ ] **Step 2: Run** `--filter ShoppingListImportTests` — Expected: FAIL.
- [ ] **Step 3: Implement** the three methods and the hint population as specified.
- [ ] **Step 4: Run** — Expected: PASS; also re-run `ShoppingListServiceTests`.
- [ ] **Step 5: Commit** `feat(shopping): colonisation and engineering importers`.

### Task 3: Settings and persistence wiring

**Files:**
- Create: `src/EDNexus.Core/Settings/ShoppingListSettings.cs`
- Modify: `src/EDNexus.Core/Settings/AppSettings.cs` (add `public ShoppingListSettings Shopping { get; set; } = new();` beside `Colonisation`), `src/EDNexus.App/Bootstrap.cs`, `src/EDNexus.App/ViewModels/Cards/CardViewModel.cs` (`DashboardContext`), `src/EDNexus.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/EDNexus.Tests/Settings/ShoppingListSettingsTests.cs`

**Interfaces:**
- Produces: `sealed class ShoppingListSettings { List<ShoppingEntry> Manual; bool LinkColonisation; bool LinkEngineering; bool HideCompleted; }`; `Bootstrap.ApplyShoppingSettings(ShoppingListSettings)` (assigns `Settings.Shopping`, then `Store.Save`); `DashboardContext.GetShoppingSettings() : ShoppingListSettings` and `SaveShoppingSettings(ShoppingListSettings)` (constructor gains `Func<ShoppingListSettings> getShopping` and `Action<ShoppingListSettings> saveShopping`, passed from `MainWindowViewModel` like the engineering pin delegates).

- [ ] **Step 1: Write failing tests**: JSON round-trip of `AppSettings` with two manual entries and both links preserved (enums as strings); deserialising JSON without a `Shopping` key yields empty defaults.
- [ ] **Step 2: Run** `--filter ShoppingListSettingsTests` — Expected: FAIL.
- [ ] **Step 3: Implement** the settings class, the `AppSettings` property, the `Bootstrap` method and the context delegates. Update the other `new DashboardContext(...)` call sites the build reports (tests construct none today, per grep of `src`).
- [ ] **Step 4: Run** `dotnet build EDNexus.slnx` then the filtered tests — Expected: build clean, PASS.
- [ ] **Step 5: Commit** `feat(shopping): persist shopping list settings`.

### Task 4: Card view model and Find routing

**Files:**
- Create: `src/EDNexus.App/ViewModels/Cards/ShoppingListCardViewModel.cs`
- Modify: `src/EDNexus.App/ViewModels/Cards/TradeCardViewModel.cs`, `src/EDNexus.App/ViewModels/Cards/CardViewModel.cs` (`DashboardContext.FindToBuy`), `src/EDNexus.App/ViewModels/MainWindowViewModel.cs` (register card after `ColonisationCardViewModel`; wire `FindToBuy`)
- Test: `tests/EDNexus.Tests/ShoppingList/ShoppingListCardTests.cs` (only if the App project is referenced by the test project; otherwise cover the logic through Core tests and verify the card by running the app)

**Interfaces:**
- Consumes: Tasks 1–3 (`Build`, `FromColonisation`, `FromEngineering`, `Resolve`, `GetShoppingSettings/SaveShoppingSettings`); `DashboardContext.Host.Colonisation.ActiveSite`, `Host.Engineering`, `GetEngineeringPin()`.
- Produces:
  - `ShoppingListCardViewModel(DashboardContext)` : `CardViewModel` id `"shopping"`, title `"SHOPPING LIST"`, width matching `ColonisationCardViewModel`; `ObservableCollection<ShoppingRowViewModel> Rows`; observable `LinkColonisation`, `LinkEngineering`, `HideCompleted`; add-item inputs `NewKind`, `NewName`, `NewQuantity`; commands `AddItemCommand`, `RemoveManualCommand(ShoppingRowViewModel)`, `FindCommand(ShoppingRowViewModel)`.
  - `ShoppingRowViewModel` exposing row fields plus `Progress`, `SourceText` (e.g. "Depot · Manual"), `CanFind`, `Hint`.
  - `DashboardContext.FindToBuy : Action<string>?` set by the shell; `TradeCardViewModel.BeginBuySearch(string commodity)` sets `TradeCommodity`, `TradeSelling = false`, `IsCollapsed = false`, fills `TradeReferenceSystem` from state when empty, then runs `SearchTradeCommand`.

- [ ] **Step 1: Write failing tests** (Core-level if the App is not testable): adding a manual item persists via `SaveShoppingSettings` and appears as a row; toggling `LinkColonisation` adds/removes depot rows; `Find` on a satisfied row is disabled.
- [ ] **Step 2: Run** the filtered tests — Expected: FAIL.
- [ ] **Step 3: Implement** the view models. `Update(CommanderState)` composes entries = `settings.Manual` + (link flags → importers), calls `Build`, and only repopulates `Rows` when a signature of the projected rows changes (as `MarketCardViewModel` does). `Reset()` clears rows and signature. Adding resolves the name with `Resolve` and merges into an existing manual entry for the same `(Kind, Key)`.
- [ ] **Step 4: Run** `dotnet build EDNexus.slnx` and the filtered tests — Expected: PASS.
- [ ] **Step 5: Commit** `feat(shopping): shopping list card view model and trade-card find link`.

### Task 5: Card view (XAML)

**Files:**
- Modify: `src/EDNexus.App/Views/MainWindow.axaml` (new `DataTemplate DataType="vm:ShoppingListCardViewModel"` after the Colonisation template, following its header/dice/collapse structure); `src/EDNexus.App/Themes/Theme.axaml` only if a needed brush is missing.

- [ ] **Step 1: Implement the template**: header row; link toggles (Depot, Engineering) and Hide-completed; add-item row (kind combo, name, quantity, Add); rows grid (`NAME`, `HELD / NEEDED` with a progress bar, `FROM`, action) where the action is a `Find` button bound to `FindCommand` when `CanFind`, else the `Hint` text; per-row remove for manual rows. Empty state text when there are no rows. Use only `{StaticResource ...}` brushes.
- [ ] **Step 2: Build and run** `dotnet build EDNexus.slnx` — Expected: clean build, no new warnings. Then launch with `dotnet run --project src/EDNexus.App`, enable Settings → Developer Options, and confirm: rows render, a commodity row's Find switches to a populated Trade search, materials rows show hints. (Per the project memory, disable `AutoDownloadUpdates` before launching a local build.)
- [ ] **Step 3: Commit** `feat(shopping): shopping list card view`.

### Task 6: Developer mode, CLI and final verification

**Files:**
- Modify: `src/EDNexus.Core/Dev/SampleSources.cs` (add `ShoppingListSampleSource`, `CardKey` `"shopping"`), `src/EDNexus.Core/Dev/DeveloperMode.cs` (register), `src/EDNexus.Cli/Program.cs` (print merged list after `PrintColonisation`), `tests/EDNexus.Tests/DeveloperModeTests.cs`

**Interfaces:**
- Produces: `ShoppingListSampleSource : JournalSampleSource` emitting valid `Cargo`/`Materials`/`ShipLocker` events so held counts move; CLI section `======== Shopping list ========` built from `ShoppingListService.Build` over the active depot and engineering pin.

- [ ] **Step 1: Write failing test** in `DeveloperModeTests`: every `Sources` entry parses (follow the existing pattern) and one has `CardKey == "shopping"`.
- [ ] **Step 2: Run** `--filter DeveloperModeTests` — Expected: FAIL.
- [ ] **Step 3: Implement** the sample source, registration (inside the existing devtools gating) and CLI output.
- [ ] **Step 4: Run** `dotnet test EDNexus.slnx` — Expected: all green; then `dotnet run --project src/EDNexus.Cli -- --once` and confirm the new section prints against real journal data.
- [ ] **Step 5: Commit** `feat(shopping): dev sample source and CLI output`.
