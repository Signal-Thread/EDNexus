# Goals Card Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A dashboard card of goals, each with an ordered step checklist and optional due dates (yellow within 24h, red overdue), plus a Goals window for full editing and choosing the focused goal.

**Architecture:** Pure Core logic (`DueStatus`, `GoalList`) over a `GoalsSettings` section persisted by `SettingsStore`. One `GoalsCardViewModel` drives both the compact card and `GoalsWindow` (the Galnet pattern). In developer mode the card swaps to an in-memory sample list and never saves (the Route/Mining pattern).

**Tech Stack:** .NET 10, Avalonia 12, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-30-goals-card-design.md`

## Global Constraints

- Nullable enabled, file-scoped namespaces, XML docs on public types; match surrounding style (AGENTS.md).
- No literal colours in `src/EDNexus.App`: use `{StaticResource Warning}` (due soon) and `{StaticResource Bad}` (overdue) from `Themes/Theme.axaml`.
- Parse defensively: saved goals JSON may lack fields, contain nulls, duplicate ids, or a dangling `ActiveGoalId`; loading must never throw.
- Due thresholds: `Overdue` when `due <= now`; `DueSoon` when `due - now <= 24h`; done items and null due are `None`.
- Due dates are `DateTimeOffset?`; a date picked without a time becomes 23:59:59 local that day.
- Cross-platform; no OS-specific code. Dev tooling stays behind `FeatureFlags.DeveloperTools`.
- Never write the user's settings file while developer mode is on.
- Commits end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Build: `dotnet build EDNexus.slnx` (no new warnings). Tests: `dotnet test EDNexus.slnx`.

## Review Focus

1. Two instants equal under different UTC offsets (travel / DST) classify identically — pinned in Task 1.
2. Blank or whitespace goal/step text is rejected, never creating an empty row — Task 2.
3. Saved JSON with null `Goals`/`Steps`, duplicate or empty ids, or an `ActiveGoalId` naming no goal loads cleanly — Task 2 (normalize) and Task 3 (round-trip).
4. A goal whose steps are all done but whose own due date is past still shows red; a done goal never shows a colour — Task 1.
5. Developer mode never overwrites saved goals, and leaving it restores them — Tasks 3 and 5.

---

### Task 1: Model and `DueStatus`

**Files:**
- Create: `src/EDNexus.Core/Goals/GoalModels.cs`, `src/EDNexus.Core/Goals/DueStatus.cs`
- Test: `tests/EDNexus.Tests/Goals/DueStatusTests.cs`

**Interfaces:**
- Produces: `enum DueState { None, Ok, DueSoon, Overdue }` (ordered so `Overdue` is greatest).
  `GoalStep { string Id; string Text; bool IsDone; DateTimeOffset? Due }`.
  `Goal { string Id; string Title; string? Notes; bool IsDone; DateTimeOffset? Due; List<GoalStep> Steps }`.
  `GoalsSettings { List<Goal> Goals; string? ActiveGoalId }`.
  `static class DueStatus { DueState For(DateTimeOffset? due, bool isDone, DateTimeOffset now); DueState Effective(Goal goal, DateTimeOffset now); DateTimeOffset EndOfDay(DateTime localDate); }`
  `Effective` = worst of the goal's own state and each unfinished step's state; `None` if `goal.IsDone`.

- [ ] **Step 1: Write failing tests** in `DueStatusTests`: `For_ExactlyNow_IsOverdue`, `For_24hAhead_IsDueSoon`, `For_24hPlus1sAhead_IsOk`, `For_Done_IsNone`, `For_NullDue_IsNone`, `For_SameInstantDifferentOffsets_Match` (due `+10:00` vs `-05:00` for one instant), `Effective_OverdueStep_MakesGoalOverdue`, `Effective_AllStepsDoneButGoalDuePast_IsOverdue`, `Effective_DoneGoal_IsNone`, `EndOfDay_Is235959Local`.
- [ ] **Step 2:** `dotnet test tests/EDNexus.Tests --filter DueStatusTests` — expect FAIL (types missing).
- [ ] **Step 3: Implement** the types and `DueStatus` per the thresholds in Global Constraints. Compare instants, not clock times.
- [ ] **Step 4:** same command — expect PASS.
- [ ] **Step 5: Commit** `Goals: model and due-date status`.

### Task 2: `GoalList` service

**Files:**
- Create: `src/EDNexus.Core/Goals/GoalList.cs`
- Test: `tests/EDNexus.Tests/Goals/GoalListTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces: `sealed class GoalList` — `GoalList(GoalsSettings data)` (normalizes: drops null goals/steps, null lists become empty, blank or duplicate ids reassigned, dangling `ActiveGoalId` falls back to first not-done goal else null); `GoalsSettings Data`; `IReadOnlyList<Goal> Goals`; `Goal? Active`;
  `Goal? AddGoal(string title, DateTimeOffset? due = null)` (null if blank; first goal becomes active);
  `void RemoveGoal(string id)`, `void RenameGoal(string id, string title)`, `void SetGoalDue(string id, DateTimeOffset? due)`, `void SetNotes(string id, string? notes)`, `void ToggleGoal(string id)`, `void SetActive(string id)`;
  `GoalStep? AddStep(string goalId, string text, DateTimeOffset? due = null)` (null if blank), `void RemoveStep(string goalId, string stepId)`, `void RenameStep(...)`, `void SetStepDue(string goalId, string stepId, DateTimeOffset? due)`, `void ToggleStep(string goalId, string stepId)`, `void MoveStep(string goalId, string stepId, int delta)` (clamped);
  `(int Done, int Total) Progress(Goal g)`; `GoalStep? NextStep(Goal g)` (first not-done); `IReadOnlyList<Goal> ByUrgency(DateTimeOffset now)` (not-done goals by `Effective` descending then earliest own `Due` then title, done goals last); `(int Overdue, int DueSoon) Counts(DateTimeOffset now)` (own state of each not-done goal plus each unfinished step of not-done goals).
  Removing or completing the active goal moves `ActiveGoalId` to the first remaining not-done goal, else null. Un-completing does not change the active goal.

- [ ] **Step 1: Write failing tests**: `AddGoal_Blank_ReturnsNull`, `AddStep_Whitespace_ReturnsNull`, `FirstGoal_BecomesActive`, `RemoveActive_FallsBackToNextNotDone`, `ToggleGoalDone_OnActive_MovesFocus`, `MoveStep_ClampsAtEnds`, `NextStep_SkipsDone`, `Progress_CountsDone`, `ByUrgency_OrdersOverdueDueSoonOkNoneDone` (fixed clock), `Counts_IgnoreDoneGoalsAndDoneSteps`, `Normalize_NullListsDuplicateIdsDanglingActive_DoesNotThrow`.
- [ ] **Step 2:** `dotnet test tests/EDNexus.Tests --filter GoalListTests` — expect FAIL.
- [ ] **Step 3: Implement** `GoalList` mutating `Data` in place; ids are `Guid.NewGuid().ToString("N")`.
- [ ] **Step 4:** run again — expect PASS.
- [ ] **Step 5: Commit** `Goals: GoalList service`.

### Task 3: Persistence and wiring

**Files:**
- Modify: `src/EDNexus.Core/Settings/AppSettings.cs` (add `public GoalsSettings Goals { get; set; } = new();`), `src/EDNexus.App/Bootstrap.cs` (after `ApplySavedRoute`), `src/EDNexus.App/ViewModels/Cards/CardViewModel.cs` (`DashboardContext`), `src/EDNexus.App/ViewModels/MainWindowViewModel.cs:64-80`
- Test: `tests/EDNexus.Tests/Settings/GoalsSettingsTests.cs`, `tests/EDNexus.App.Tests/DeveloperModePersistenceTests.cs`

**Interfaces:**
- Produces: `void Bootstrap.ApplyGoals(GoalsSettings goals)` — returns without saving when `Dev.Enabled`, else `Settings.Goals = goals; Store.Save(Settings)`.
  `DashboardContext` ctor gains two **optional trailing** params `Func<GoalsSettings>? getGoals = null, Action<GoalsSettings>? saveGoals = null` (optional so `CardFaultTests`' positional construction keeps compiling); members `GoalsSettings GetGoals()` (new empty instance when no delegate) and `void SaveGoals(GoalsSettings goals)` (no-op when no delegate).
  `MainWindowViewModel` passes `() => _boot.Settings.Goals` and `g => _boot.ApplyGoals(g)`.

- [ ] **Step 1: Write failing tests**: in `GoalsSettingsTests` — `RoundTrip_PreservesGoalsStepsDuesAndActive` (via `SettingsStore` on a temp path), `Load_JsonWithoutGoalsSection_GivesEmpty`, `Load_GoalWithoutDueFields_Loads`, `Load_NullStepsList_DoesNotThrow` (then `new GoalList(settings.Goals)` normalizes). In `DeveloperModePersistenceTests` — `ApplyGoals_Live_IsPersisted` and `ApplyGoals_InDeveloperMode_IsNotSaved` (assert settings file absent), mirroring the existing mining tests.
- [ ] **Step 2:** `dotnet test EDNexus.slnx --filter "GoalsSettingsTests|ApplyGoals"` — expect FAIL.
- [ ] **Step 3: Implement** the setting, `ApplyGoals`, context members and wiring.
- [ ] **Step 4:** run again plus `dotnet build EDNexus.slnx` — expect PASS, no warnings.
- [ ] **Step 5: Commit** `Goals: persist in settings`.

### Task 4: `SampleGoals` for developer mode

**Files:**
- Create: `src/EDNexus.Core/Dev/SampleGoals.cs`
- Test: `tests/EDNexus.Tests/Dev/SampleGoalsTests.cs`

**Interfaces:**
- Produces: `static GoalsSettings SampleGoals.Create(Random rng, DateTimeOffset now)` — 3–4 goals with 3–6 steps each, dates spread so at least one goal is `Overdue`, one `DueSoon`, one `Ok`, one with no due; some steps done; `ActiveGoalId` set.

- [ ] **Step 1: Write failing tests**: `Create_CoversEveryDueState` (over several seeds, via `DueStatus.Effective`), `Create_IsDeterministicPerSeed`, `Create_ActiveGoalExists`.
- [ ] **Step 2:** `dotnet test tests/EDNexus.Tests --filter SampleGoalsTests` — expect FAIL.
- [ ] **Step 3: Implement** using a fixed pool of Elite-flavoured goal/step titles; offsets relative to `now`.
- [ ] **Step 4:** run again — expect PASS.
- [ ] **Step 5: Commit** `Goals: developer-mode sample data`.

### Task 5: `GoalsCardViewModel` and the compact card

**Files:**
- Create: `src/EDNexus.App/ViewModels/Cards/GoalsCardViewModel.cs`
- Modify: `src/EDNexus.App/ViewModels/MainWindowViewModel.cs` (register `new GoalsCardViewModel(_context)` in `Cards`, after Missions), `src/EDNexus.App/Views/MainWindow.axaml` (add a `DataTemplate DataType="vm:GoalsCardViewModel"` after the Ranks template, same header/body structure)
- Test: `tests/EDNexus.App.Tests/GoalsCardViewModelTests.cs`

**Interfaces:**
- Consumes: `GoalList`, `DueStatus`, `SampleGoals`, `DashboardContext.GetGoals/SaveGoals/DevEnabled/Rng`.
- Produces: `sealed partial class GoalsCardViewModel : CardViewModel` — ctor `(DashboardContext context, Func<DateTimeOffset>? clock = null)`, id `"goals"`, title `"GOALS"`, width 452.
  Row types `GoalRow { Goal Goal; string Title; string ProgressText; string DueText; DueState State; bool IsActive; bool IsOverdue; bool IsDueSoon }` and `StepRow { GoalStep Step; string Text; bool IsDone; string DueText; DueState State; bool IsNext; bool IsOverdue; bool IsDueSoon }` (the bool flags drive style classes bound to the theme brushes).
  Observable: `GoalRow? Focused`, `ObservableCollection<StepRow> Steps`, `ObservableCollection<GoalRow> Others`, `ObservableCollection<GoalRow> AllByUrgency`, `GoalRow? Selected`, `int OverdueCount`, `int DueSoonCount`, `bool HasAttention`, `bool BadgeIsOverdue`, `bool HasGoals`.
  Commands (all save via `Context.SaveGoals` unless in dev): `AddGoal(string)`, `AddStep(string)`, `ToggleStep(StepRow)`, `Focus(GoalRow)`, plus window operations `RenameGoal`, `SetGoalDue`, `SetNotes`, `ToggleGoal`, `DeleteGoal`, `RenameStep`, `SetStepDue`, `MoveStep(StepRow, int)`, `DeleteStep(StepRow)`, `OpenManage` (Task 6).
  `Update(CommanderState)` ignores state; recomputes statuses against the clock and rebuilds rows only when a signature of (ids, done flags, due states, active id) changes. On a change of `Context.DevEnabled` it swaps between the saved list and `SampleGoals.Create(Context.Rng, now)`.
- Card template: header with title, progress and due of `Focused` plus the badge (`BadgeIsOverdue` → `Bad`, else `Warning`; hidden unless `HasAttention`); step list with checkbox, text, due text, next-step emphasis; an add-step `TextBox` committing on Enter; the `Others` strip (title, progress, status dot); "Manage goals" button.

- [ ] **Step 1: Write failing tests** (context built with a fake clock, `getGoals`/`saveGoals` stubs, `devEnabled` flag): `Focused_IsActiveGoal_WithNextStepFlagged`, `ToggleStep_SavesAndAdvancesNext`, `Others_ExcludesFocused_AndCarriesStatus` (an unfocused overdue goal has `IsOverdue`), `Counts_AndBadgeColourFlag`, `Update_AfterClockPassesDue_FlipsToOverdue`, `Focus_ChangesFocusedAndPersistsActiveId`, `AddGoal_Blank_DoesNothing`, `DevMode_UsesSampleGoals_AndNeverSaves`, `LeavingDevMode_RestoresSavedGoals` (saved list untouched, same ids).
- [ ] **Step 2:** `dotnet test tests/EDNexus.App.Tests --filter GoalsCardViewModelTests` — expect FAIL.
- [ ] **Step 3: Implement** the view model and the template; register the card.
- [ ] **Step 4:** run again plus `dotnet build EDNexus.slnx` — expect PASS.
- [ ] **Step 5: Commit** `Goals: dashboard card`.

### Task 6: `GoalsWindow`

**Files:**
- Create: `src/EDNexus.App/Views/GoalsWindow.axaml`, `src/EDNexus.App/Views/GoalsWindow.axaml.cs`
- Modify: `GoalsCardViewModel.cs` (`OpenManage`)

**Interfaces:**
- Consumes: Task 5's view model as `DataContext` (`x:DataType="vm:GoalsCardViewModel"`).
- Produces: `GoalsWindow : Window`; `OpenManage` shows it owned by the main window, or activates the one already open, copying `GalnetCardViewModel.OpenReader`.
- Layout: left pane `AllByUrgency` (title, progress, status colour, Done group collapsed); right pane for `Selected` — title and notes boxes, due `CalendarDatePicker` + `TimePicker` with a clear button, step list with per-step due pickers, up/down/delete, add-step box, "Focus on this goal", "Mark done", "Delete goal", and "Add goal" in the left pane. Theme brushes only; header styled like `GalnetWindow`. A date without a time uses `DueStatus.EndOfDay`.

- [ ] **Step 1:** Implement the window and `OpenManage`.
- [ ] **Step 2:** `dotnet build EDNexus.slnx` — expect success, no warnings.
- [ ] **Step 3: Commit** `Goals: management window`.

### Task 7: Verify in the running app

- [ ] **Step 1:** `dotnet test EDNexus.slnx` — expect all green.
- [ ] **Step 2:** Set `AutoDownloadUpdates` to false in `%LOCALAPPDATA%\EDNexus\settings.json` (a locally built app otherwise installs the latest release and exits), then `dotnet run --project src/EDNexus.App`.
- [ ] **Step 3:** Enable Settings → Developer Options. Confirm on the card: yellow and red due text, the badge colour, an unfocused overdue goal marked in the strip, focus switching. Open the window and confirm editing, the urgency sort, and focus. Screenshot each state.
- [ ] **Step 4:** Turn developer mode off; confirm saved goals are intact. Add a real goal, restart, confirm it persisted.
- [ ] **Step 5:** Run `dotnet run --project src/EDNexus.Cli -- --once` to confirm the engine harness still works. Report what was and was not verified in the PR description.
