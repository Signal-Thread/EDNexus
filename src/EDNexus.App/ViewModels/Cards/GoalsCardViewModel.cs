using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.Core.Dev;
using EDNexus.Core.Goals;
using EDNexus.Core.State;

namespace EDNexus.App.ViewModels;

/// <summary>
/// Goals: the commander's own to-do list. The card shows the focused goal's step checklist, a strip of
/// the other goals with their due-date status, and a badge counting what is overdue or due soon. The
/// management window (opened from the card) shares this view model, so edits show in both at once.
/// </summary>
/// <remarks>
/// Nothing here comes from the journal: the tick only re-reads the clock so colours change as deadlines
/// approach. In developer mode the card works on an in-memory sample list and never saves; the real
/// goals are reloaded, untouched, when developer mode is switched off.
/// </remarks>
public sealed partial class GoalsCardViewModel : CardViewModel
{
    private readonly Func<DateTimeOffset> _clock;

    // The list every edit goes through: the saved goals, or the sample set while in developer mode.
    private GoalList? _list;
    private bool _listIsSample;
    private string _signature = "";

    public GoalsCardViewModel(DashboardContext context, Func<DateTimeOffset>? clock = null)
        : base(context, "goals", "GOALS", 452)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>No journal sample source feeds this card; developer mode swaps in sample goals instead.</summary>
    public override bool CanRandomize => false;

    /// <summary>The goal the card shows, or null when there are none (or all are done).</summary>
    [ObservableProperty] private GoalRow? _focused;

    /// <summary>The goal open in the management window.</summary>
    [ObservableProperty] private GoalRow? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttention), nameof(BadgeIsOverdue), nameof(AttentionCount))]
    private int _overdueCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttention), nameof(AttentionCount))]
    private int _dueSoonCount;

    [ObservableProperty] private bool _hasGoals;

    /// <summary>Text in the card's add-step box; cleared once the step is added.</summary>
    [ObservableProperty] private string _newStepText = "";

    /// <summary>The focused goal's steps, in order.</summary>
    public ObservableCollection<StepRow> Steps { get; } = new();

    /// <summary>Unfinished goals other than the focused one, most urgent first, for the switcher strip.</summary>
    public ObservableCollection<GoalRow> Others { get; } = new();

    /// <summary>Every goal, most urgent first with done goals last, for the management window.</summary>
    public ObservableCollection<GoalRow> AllByUrgency { get; } = new();

    /// <summary>Overdue plus due-soon items, shown on the header badge.</summary>
    public int AttentionCount => OverdueCount + DueSoonCount;

    /// <summary>Whether the header badge shows at all.</summary>
    public bool HasAttention => AttentionCount > 0;

    /// <summary>True when the badge should be red (something is overdue) rather than yellow.</summary>
    public bool BadgeIsOverdue => OverdueCount > 0;

    private DateTimeOffset Now => _clock();

    /// <summary>Ignores <paramref name="state"/>; re-evaluates due dates and rebuilds only on a change.</summary>
    public override void Update(CommanderState state)
    {
        EnsureSource();
        Rebuild(force: false);
    }

    public override void Reset()
    {
        _list = null;
        _signature = "";
    }

    /// <summary>Adds a goal; blank titles are ignored.</summary>
    [RelayCommand]
    public void AddGoal(string? title)
    {
        EnsureSource();
        if (_list!.AddGoal(title ?? "") is not null) Commit();
    }

    /// <summary>Adds a step to the focused goal; blank text is ignored.</summary>
    [RelayCommand]
    public void AddStep(string? text)
    {
        EnsureSource();
        if (_list!.Active is not { } goal) return;
        if (_list.AddStep(goal.Id, text ?? "") is null) return;
        NewStepText = "";
        Commit();
    }

    /// <summary>Ticks or unticks a step.</summary>
    [RelayCommand]
    public void ToggleStep(StepRow? row)
    {
        if (!EnsureSource() || row is null || GoalOf(row) is not { } goal) return;
        _list!.ToggleStep(goal.Id, row.Step.Id);
        Commit();
    }

    /// <summary>Makes a goal the one the card shows.</summary>
    [RelayCommand]
    public void Focus(GoalRow? row)
    {
        if (!EnsureSource() || row is null) return;
        _list!.SetActive(row.Goal.Id);
        Commit();
    }

    /// <summary>Marks a goal done, or not done.</summary>
    [RelayCommand]
    public void ToggleGoal(GoalRow? row)
    {
        if (!EnsureSource() || row is null) return;
        _list!.ToggleGoal(row.Goal.Id);
        Commit();
    }

    /// <summary>Deletes a goal.</summary>
    [RelayCommand]
    public void DeleteGoal(GoalRow? row)
    {
        if (!EnsureSource() || row is null) return;
        _list!.RemoveGoal(row.Goal.Id);
        Commit();
    }

    /// <summary>Deletes a step.</summary>
    [RelayCommand]
    public void DeleteStep(StepRow? row)
    {
        if (!EnsureSource() || row is null || GoalOf(row) is not { } goal) return;
        _list!.RemoveStep(goal.Id, row.Step.Id);
        Commit();
    }

    /// <summary>Renames a goal; a blank title is ignored.</summary>
    public void RenameGoal(GoalRow row, string title)
    {
        if (!EnsureSource()) return;
        _list!.RenameGoal(row.Goal.Id, title);
        Commit();
    }

    /// <summary>Sets or clears a goal's deadline.</summary>
    public void SetGoalDue(GoalRow row, DateTimeOffset? due)
    {
        if (!EnsureSource()) return;
        _list!.SetGoalDue(row.Goal.Id, due);
        Commit();
    }

    /// <summary>Sets a goal's notes.</summary>
    public void SetNotes(GoalRow row, string? notes)
    {
        if (!EnsureSource()) return;
        _list!.SetNotes(row.Goal.Id, notes);
        Commit();
    }

    /// <summary>Renames a step; blank text is ignored.</summary>
    public void RenameStep(StepRow row, string text)
    {
        if (!EnsureSource() || GoalOf(row) is not { } goal) return;
        _list!.RenameStep(goal.Id, row.Step.Id, text);
        Commit();
    }

    /// <summary>Sets or clears a step's deadline.</summary>
    public void SetStepDue(StepRow row, DateTimeOffset? due)
    {
        if (!EnsureSource() || GoalOf(row) is not { } goal) return;
        _list!.SetStepDue(goal.Id, row.Step.Id, due);
        Commit();
    }

    /// <summary>Moves a step up (negative) or down (positive).</summary>
    public void MoveStep(StepRow row, int delta)
    {
        if (!EnsureSource() || GoalOf(row) is not { } goal) return;
        _list!.MoveStep(goal.Id, row.Step.Id, delta);
        Commit();
    }

    /// <summary>Opens the management window.</summary>
    [RelayCommand]
    private void OpenManage()
    {
        // The window arrives with GoalsWindow; until then the button does nothing.
    }

    /// <summary>
    /// Points <see cref="_list"/> at the right goals for the current mode. Run before every edit, not
    /// just on the tick: <see cref="DashboardContext.GetGoals"/> hands back the live settings object and
    /// <see cref="GoalList"/> edits in place, so the live list must be dropped the moment developer mode
    /// is on. Returns false when it had to swap, because rows built from the old list are now stale.
    /// </summary>
    private bool EnsureSource()
    {
        var dev = Context.DevEnabled;
        if (_list is not null && dev == _listIsSample) return true;

        var hadList = _list is not null;
        _listIsSample = dev;
        _list = new GoalList(dev ? SampleGoals.Create(Context.Rng, Now) : Context.GetGoals());
        Rebuild(force: true);
        return !hadList;
    }

    /// <summary>The goal in the current list that owns the row's step, if it is still there.</summary>
    private Goal? GoalOf(StepRow row) =>
        _list!.Goals.FirstOrDefault(g => g.Steps.Any(s => s.Id == row.Step.Id));

    /// <summary>Persists the edit (never in developer mode) and refreshes the rows.</summary>
    private void Commit()
    {
        if (!_listIsSample && !Context.DevEnabled) Context.SaveGoals(_list!.Data);
        Rebuild(force: true);
    }

    private void Rebuild(bool force)
    {
        var list = _list!;
        var now = Now;
        var activeId = list.Active?.Id;

        // The dashboard ticks four times a second; only rebuild when a status or the shape changed.
        var signature = string.Join("|", list.Goals.Select(g =>
            $"{g.Id}:{g.IsDone}:{DueStatus.For(g.Due, g.IsDone, now)}:" +
            string.Join(",", g.Steps.Select(s => $"{s.Id}:{s.IsDone}:{DueStatus.For(s.Due, s.IsDone, now)}"))))
            + "#" + activeId;
        if (!force && signature == _signature) return;
        _signature = signature;

        var selectedId = Selected?.Goal.Id;

        AllByUrgency.Clear();
        Others.Clear();
        foreach (var goal in list.ByUrgency(now))
        {
            var row = new GoalRow(goal, list.Progress(goal), goal.Id == activeId, now);
            AllByUrgency.Add(row);
            if (!row.IsActive && !goal.IsDone) Others.Add(row);
        }

        Focused = AllByUrgency.FirstOrDefault(r => r.IsActive);

        Steps.Clear();
        if (list.Active is { } active)
        {
            var next = list.NextStep(active);
            foreach (var step in active.Steps) Steps.Add(new StepRow(step, ReferenceEquals(step, next), now));
        }

        (OverdueCount, DueSoonCount) = list.Counts(now);
        HasGoals = list.Goals.Count > 0;
        Selected = AllByUrgency.FirstOrDefault(r => r.Goal.Id == selectedId) ?? Focused;
    }

    /// <summary>Short local due date for a row, or empty when there is none.</summary>
    internal static string FormatDue(DateTimeOffset? due) =>
        due is { } d ? "due " + d.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture) : "";
}

/// <summary>One goal as the card and window render it.</summary>
public sealed class GoalRow
{
    public GoalRow(Goal goal, (int Done, int Total) progress, bool isActive, DateTimeOffset now)
    {
        Goal = goal;
        Title = goal.Title;
        ProgressText = progress.Total == 0 ? "" : $"{progress.Done}/{progress.Total}";
        DueText = GoalsCardViewModel.FormatDue(goal.Due);
        State = DueStatus.Effective(goal, now);
        IsActive = isActive;
    }

    /// <summary>The goal this row shows.</summary>
    public Goal Goal { get; }

    /// <summary>The stored (trimmed) title.</summary>
    public string Title { get; }

    /// <summary>Done/total steps, e.g. "3/7"; empty when the goal has no steps.</summary>
    public string ProgressText { get; }

    /// <summary>The goal's own deadline, or empty.</summary>
    public string DueText { get; }

    /// <summary>Worst status across the goal's deadline and its unfinished steps.</summary>
    public DueState State { get; }

    /// <summary>True for the goal the card is focused on.</summary>
    public bool IsActive { get; }

    /// <summary>Drives the red highlight.</summary>
    public bool IsOverdue => State == DueState.Overdue;

    /// <summary>Drives the yellow highlight.</summary>
    public bool IsDueSoon => State == DueState.DueSoon;
}

/// <summary>One step of the focused goal as the card renders it.</summary>
public sealed class StepRow
{
    public StepRow(GoalStep step, bool isNext, DateTimeOffset now)
    {
        Step = step;
        Text = step.Text;
        IsDone = step.IsDone;
        DueText = GoalsCardViewModel.FormatDue(step.Due);
        State = DueStatus.For(step.Due, step.IsDone, now);
        IsNext = isNext;
    }

    /// <summary>The step this row shows.</summary>
    public GoalStep Step { get; }

    /// <summary>The stored (trimmed) text.</summary>
    public string Text { get; }

    /// <summary>True once the step is ticked.</summary>
    public bool IsDone { get; }

    /// <summary>The step's deadline, or empty.</summary>
    public string DueText { get; }

    /// <summary>The step's own due status (none once done).</summary>
    public DueState State { get; }

    /// <summary>True for the first unfinished step, which the card emphasises.</summary>
    public bool IsNext { get; }

    /// <summary>Drives the red highlight.</summary>
    public bool IsOverdue => State == DueState.Overdue;

    /// <summary>Drives the yellow highlight.</summary>
    public bool IsDueSoon => State == DueState.DueSoon;
}
