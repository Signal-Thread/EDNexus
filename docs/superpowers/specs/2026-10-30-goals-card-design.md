# Goals card — design

## Purpose

Commanders lose track of what they are doing in-game. This adds a dashboard card holding **goals**
(e.g. "Get an Asp Explorer"), each with an ordered checklist of **steps**, so a glance shows what is
next. Goals and steps carry optional due dates; yellow marks "due within 24 hours", red marks
"overdue". A separate window provides full management; the card stays compact and glanceable.

Unlike other cards this one is driven by user input, not the journal.

## Scope

In: goals with steps, due dates (date + time), urgency highlighting, a compact card with a switcher
strip and urgency badge, a Goals window for full editing and focus selection, persistence, a dev
sample source, tests.

Out: journal auto-ticking of steps, notifications/sounds on overdue, recurring items, Twitch
extension and overlay exposure.

## Data model (`EDNexus.Core/Goals`)

- `GoalStep { Id, Text, IsDone, Due? }`
- `Goal { Id, Title, Notes?, IsDone, Due?, Steps[] }`
- `GoalsSettings { Goals[], ActiveGoalId? }` — a new section of the persisted settings, plain JSON,
  no secrets. Missing/unknown fields load defensively (saved JSON without `Due` still loads).
- `Due` is `DateTimeOffset?`. When the user picks only a date, it defaults to the end of that local day.

### `GoalList` (pure service, no UI dependencies)

Add/remove/rename goals; add/remove/rename/reorder steps (goals are not reorderable — the window sorts them by urgency); toggle done, set active goal, progress (`done/total`),
next unchecked step. Deleting the active goal falls back to the first not-done goal, else none.

### `DueStatus` (pure)

`DueStatus.For(due, isDone, now)` returns `None | Ok | DueSoon | Overdue`:

- done or no due date → `None`
- `due <= now` → `Overdue`
- `due - now <= 24h` → `DueSoon`
- otherwise → `Ok`

The clock is a parameter; nothing reads wall time inside the calculation.

A goal's **effective status** is the worst of its own status and its unfinished steps' statuses
(`Overdue` > `DueSoon` > `Ok` > `None`).

## UI (`EDNexus.App`)

### `GoalsCardViewModel` (card id `goals`)

Registered in `MainWindowViewModel.Cards`. `Update(CommanderState)` only re-evaluates `DueStatus`
against the current time (so colours change as time passes); it reads no journal state.

Compact card:

- Focused goal: title, progress (e.g. 3/7), due, the step checklist with per-step due and
  highlighting; the next unchecked step is emphasised. Checkboxes toggle on click; an "add step"
  box adds on Enter.
- Switcher strip: the other goals with progress and a yellow/red marker from their effective status,
  so an unfocused goal that is overdue or due soon still shows. Clicking one focuses it.
- Header badge: count of overdue/due-soon items across all goals; red if anything is overdue, yellow
  if only due-soon, hidden if none.
- A "Manage goals" button opens the window.

### `GoalsWindow`

Opened from the card with `DataContext` = the card view model (as `GalnetWindow` is), so card and
window share state and edits show in both immediately.

- Left pane: all goals, sorted by effective urgency (overdue, due-soon, ok, none) then due date, with
  completed goals in a collapsed "Done" group. Rows show progress and status colour.
- Right pane: detail for the selected goal — rename, notes, due-date + time pickers, add/edit/delete/
  reorder steps, per-step due pickers, mark done, delete goal.
- "Focus on this goal" sets `ActiveGoalId`, which is what the card shows.

### Colours

`Warning` brush for DueSoon, `Bad` for Overdue, from `Themes/Theme.axaml` via `{StaticResource ...}`.
No literal hex colours. Highlight applies to the due-date text and a thin row edge/dot, not the whole row.

## Persistence and wiring

`DashboardContext` gains `GetGoals` / `SaveGoals` delegates, the same shape as the saved route, backed
by `SettingsStore`. The card saves after every edit.

## Developer mode

`JournalSampleSource`/`DeveloperMode.Sources` only emit journal-event JSON, and goals have no
journal event. So, as with the Route, Galnet and Mining cards, the card branches on
`Context.DevEnabled`: while developer mode is on it swaps to an **in-memory** goal list built by
`SampleGoals` (a mix of overdue, due-soon and comfortable dates) and never saves. Turning developer
mode off reloads the user's saved goals, which were never touched. `Bootstrap.ApplyGoals` also
refuses to save while developer mode is on, as `LearnCommodityPrices` does.

## Testing

xUnit in `EDNexus.Tests`:

- `DueStatus`: exactly now, just under/over 24h, exactly 24h, done items, null due.
- Effective status rolling up from steps.
- `GoalList`: ordering, toggle, progress, next step, active-goal fallback on delete.
- Urgency sort order used by the window list.
- Settings round-trip; saved JSON lacking `Due`, empty JSON and malformed entries load without throwing.

Manual: run the app in developer mode and check each colour state on the card, switcher strip,
badge and window. Disable `AutoDownloadUpdates` before launching a locally built app.

## Definition of done

Clean build with no new warnings, tests green, behaviour verified in the running app, and the
conventions in `AGENTS.md` honoured (theme tokens only, defensive parsing, dev source registered).
