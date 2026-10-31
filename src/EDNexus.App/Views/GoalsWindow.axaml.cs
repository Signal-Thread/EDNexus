using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EDNexus.App.ViewModels;

namespace EDNexus.App.Views;

/// <summary>
/// The goals manager. Opened from the Goals card and bound to the same
/// <see cref="GoalsCardViewModel"/>, so edits here and on the dashboard stay in step. The code-behind is
/// only the glue the view model cannot take: pushing text-box and picker edits into it, keeping the two
/// goal lists and the pickers in step with <see cref="GoalsCardViewModel.Selected"/>, and making sure typed
/// text is never lost.
/// </summary>
public partial class GoalsWindow : Window
{
    private GoalsCardViewModel? _vm;

    // True while the code fills a control from the view model, so that is not mistaken for an edit.
    private bool _syncing;

    // The goal whose title and notes the boxes currently show, and the step edits typed but not yet saved.
    // Text commits on Enter or focus loss; these also let it be saved when the window closes or another goal
    // is selected, and kept (not overwritten) when a rebuild recreates the rows under the cursor.
    private GoalRow? _shownRow;
    private bool _flushing;
    private readonly Dictionary<string, (StepRow Row, string Text)> _pendingSteps = new();

    public GoalsWindow() => InitializeComponent();

    private GoalsCardViewModel? Vm => DataContext as GoalsCardViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = Vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        OnSelectedChanged();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        FlushPending(_shownRow);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
        base.OnClosed(e);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GoalsCardViewModel.Selected) && !_flushing) OnSelectedChanged();
    }

    private void OnSelectedChanged()
    {
        var previous = _shownRow;
        var row = Vm?.Selected;

        // Another goal: save whatever was typed for the old one before the boxes switch over.
        if (previous is not null && row?.Goal.Id != previous.Goal.Id)
        {
            FlushPending(previous);
            row = Vm?.Selected;   // the flush can rebuild the rows
        }

        _shownRow = row;
        var sameGoal = previous is not null && row is not null && row.Goal.Id == previous.Goal.Id;
        Fill(() =>
        {
            // A rebuild of the same goal keeps what is being typed; the boxes only reload when unfocused.
            if (!(sameGoal && TitleBox.IsKeyboardFocusWithin)) TitleBox.Text = row?.Title ?? "";
            if (!(sameGoal && NotesBox.IsKeyboardFocusWithin)) NotesBox.Text = row?.Goal.Notes ?? "";
            ActiveList.SelectedItem = row is not null && Vm!.ActiveGoals.Contains(row) ? row : null;
            DoneList.SelectedItem = row is not null && Vm!.DoneGoals.Contains(row) ? row : null;
            GoalDate.SelectedDate = row?.DueDate;
            GoalTime.SelectedTime = row?.DueTime;
        });
    }

    private void SyncGoalPickers() => Fill(() =>
    {
        GoalDate.SelectedDate = Vm?.Selected?.DueDate;
        GoalTime.SelectedTime = Vm?.Selected?.DueTime;
    });

    private void Fill(Action set)
    {
        var was = _syncing;
        _syncing = true;
        try { set(); }
        finally { _syncing = was; }
    }

    private void OnGoalListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Rebuilds clear the lists (removals only); only a click that adds an item picks a goal.
        if (_syncing || Vm is not { } vm) return;
        if (e.AddedItems.OfType<GoalRow>().FirstOrDefault() is { } row) vm.Selected = row;
    }

    /// <summary>Saves any typed-but-uncommitted title, notes and step text.</summary>
    private void FlushPending(GoalRow? row)
    {
        if (Vm is not { } vm || _flushing) return;
        _flushing = true;
        try
        {
            if (row is not null)
            {
                CommitTitle(row);
                CommitNotes(row);
            }
            var pending = _pendingSteps.Values.ToList();
            _pendingSteps.Clear();
            foreach (var (step, text) in pending) vm.RenameStep(step, text);
        }
        finally { _flushing = false; }
    }

    // ---- Goal: add -------------------------------------------------------------------------------------

    private void OnNewGoalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddGoalFromBox();
        e.Handled = true;
    }

    private void OnAddGoalClick(object? sender, RoutedEventArgs e) => AddGoalFromBox();

    private void AddGoalFromBox()
    {
        if (Vm is not { } vm || string.IsNullOrWhiteSpace(NewGoalBox.Text)) return;
        vm.AddGoal(NewGoalBox.Text);
        NewGoalBox.Text = "";
    }

    // ---- Goal: title, notes, deadline ------------------------------------------------------------------

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitTitle(_shownRow);
        e.Handled = true;
    }

    private void OnTitleCommit(object? sender, RoutedEventArgs e) => CommitTitle(_shownRow);

    private void CommitTitle(GoalRow? row)
    {
        if (row is null || Vm is not { } vm) return;
        var text = TitleBox.Text?.Trim() ?? "";
        // A blank title is ignored by the list; put the stored one back so the box does not lie.
        if (text.Length == 0) Fill(() => TitleBox.Text = row.Goal.Title);
        else if (text != row.Goal.Title) vm.RenameGoal(row, text);
    }

    private void OnNotesCommit(object? sender, RoutedEventArgs e) => CommitNotes(_shownRow);

    private void CommitNotes(GoalRow? row)
    {
        if (row is null || Vm is not { } vm) return;
        var text = NotesBox.Text ?? "";
        if (text != (row.Goal.Notes ?? "")) vm.SetNotes(row, text);
    }

    private void OnGoalDateChanged(object? sender, SelectionChangedEventArgs e) => CommitGoalDue();

    private void OnGoalTimeChanged(object? sender, TimePickerSelectedValueChangedEventArgs e) => CommitGoalDue();

    private void CommitGoalDue()
    {
        if (_syncing || Vm is not { Selected: { } row } vm) return;
        var due = GoalsCardViewModel.Combine(GoalDate.SelectedDate, GoalTime.SelectedTime);
        if (due == row.Goal.Due) return;
        // No date yet (a time alone), or a date outside what a deadline can be: nothing to save.
        if (due is null && (GoalDate.SelectedDate is not null || GoalTime.SelectedTime is not null)) return;
        vm.SetGoalDue(row, due);
    }

    private void OnGoalDueClear(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Selected: { } row } vm) return;
        vm.SetGoalDue(row, null);
        SyncGoalPickers();
    }

    // ---- Goal: add step --------------------------------------------------------------------------------

    private void OnNewStepKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddStepFromBox();
        e.Handled = true;
    }

    private void OnAddStepClick(object? sender, RoutedEventArgs e) => AddStepFromBox();

    private void AddStepFromBox()
    {
        if (Vm is not { Selected: { } row } vm || string.IsNullOrWhiteSpace(NewStepBox.Text)) return;
        // Into the selected goal, which need not be the focused one the card's own box adds to.
        vm.AddStepTo(row, NewStepBox.Text);
        NewStepBox.Text = "";
    }

    // ---- Steps -----------------------------------------------------------------------------------------

    private static StepRow? StepOf(object? sender) => (sender as Control)?.DataContext as StepRow;

    private void OnStepTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitStepText(sender);
        e.Handled = true;
    }

    private void OnStepTextCommit(object? sender, RoutedEventArgs e) => CommitStepText(sender);

    private void CommitStepText(object? sender)
    {
        if (sender is not TextBox box || StepOf(box) is not { } step || Vm is not { } vm) return;
        _pendingSteps.Remove(step.Step.Id);
        var text = box.Text?.Trim() ?? "";
        if (text.Length == 0) Fill(() => box.Text = step.Step.Text);
        else if (text != step.Step.Text) vm.RenameStep(step, text);
    }

    // The step rows are recreated by every rebuild; typed text survives that by being parked here and put
    // back when the new row's box appears.
    private void OnStepTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing || sender is not TextBox box || StepOf(box) is not { } step) return;
        var text = box.Text ?? "";
        if (text.Trim() == step.Step.Text) _pendingSteps.Remove(step.Step.Id);
        else _pendingSteps[step.Step.Id] = (step, text);
    }

    private void OnStepTextContext(object? sender, EventArgs e)
    {
        if (sender is not TextBox box || StepOf(box) is not { } step) return;
        if (_pendingSteps.TryGetValue(step.Step.Id, out var pending))
        {
            _pendingSteps[step.Step.Id] = (step, pending.Text);
            Fill(() => box.Text = pending.Text);
        }
        else Fill(() => box.Text = step.Text);
    }

    private void OnStepUp(object? sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is { } step) Vm?.MoveStep(step, -1);
    }

    private void OnStepDown(object? sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is { } step) Vm?.MoveStep(step, 1);
    }

    // A step row's pickers are filled when their DataContext (the row) arrives. The row is rebuilt after
    // every edit, so this also refreshes them; _syncing keeps the fill from counting as an edit.

    private void OnStepDateContext(object? sender, EventArgs e)
    {
        if (sender is CalendarDatePicker picker) Fill(() => picker.SelectedDate = StepOf(picker)?.DueDate);
    }

    private void OnStepTimeContext(object? sender, EventArgs e)
    {
        if (sender is TimePicker picker) Fill(() => picker.SelectedTime = StepOf(picker)?.DueTime);
    }

    private void OnStepDateChanged(object? sender, SelectionChangedEventArgs e) => CommitStepDue(sender);

    private void OnStepTimeChanged(object? sender, TimePickerSelectedValueChangedEventArgs e) => CommitStepDue(sender);

    private void CommitStepDue(object? sender)
    {
        if (_syncing || sender is not Control control || StepOf(control) is not { } step || Vm is not { } vm) return;
        if (control.Parent is not Panel panel) return;

        var date = panel.Children.OfType<CalendarDatePicker>().FirstOrDefault()?.SelectedDate;
        var time = panel.Children.OfType<TimePicker>().FirstOrDefault()?.SelectedTime;
        var due = GoalsCardViewModel.Combine(date, time);
        if (due == step.Step.Due) return;
        if (due is null && (date is not null || time is not null)) return;   // no date yet, or an unusable one
        vm.SetStepDue(step, due);
    }

    private void OnStepDueClear(object? sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is { } step) Vm?.SetStepDue(step, null);
    }
}
