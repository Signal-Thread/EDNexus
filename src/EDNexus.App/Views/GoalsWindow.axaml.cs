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
/// only the glue the view model cannot take: pushing text-box and picker edits into it, and filling the
/// pickers from the current goal or step.
/// </summary>
public partial class GoalsWindow : Window
{
    private GoalsCardViewModel? _vm;

    // True while the code fills a picker from the view model, so that is not mistaken for an edit.
    private bool _syncing;

    public GoalsWindow() => InitializeComponent();

    private GoalsCardViewModel? Vm => DataContext as GoalsCardViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = Vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        SyncGoalPickers();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
        base.OnClosed(e);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GoalsCardViewModel.Selected)) SyncGoalPickers();
    }

    private void SyncGoalPickers()
    {
        _syncing = true;
        try
        {
            GoalDate.SelectedDate = Vm?.Selected?.DueDate;
            GoalTime.SelectedTime = Vm?.Selected?.DueTime;
        }
        finally { _syncing = false; }
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
        CommitTitle();
        e.Handled = true;
    }

    private void OnTitleCommit(object? sender, RoutedEventArgs e) => CommitTitle();

    private void CommitTitle()
    {
        if (Vm is not { Selected: { } row } vm) return;
        var text = TitleBox.Text?.Trim() ?? "";
        // A blank title is ignored by the list; put the stored one back so the box does not lie.
        if (text.Length == 0) TitleBox.Text = row.Title;
        else if (text != row.Title) vm.RenameGoal(row, text);
    }

    private void OnNotesCommit(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Selected: { } row } vm) return;
        var text = NotesBox.Text ?? "";
        if (text != (row.Goal.Notes ?? "")) vm.SetNotes(row, text);
    }

    private void OnGoalDateChanged(object? sender, SelectionChangedEventArgs e) => CommitGoalDue();

    private void OnGoalTimeChanged(object? sender, TimePickerSelectedValueChangedEventArgs e) => CommitGoalDue();

    private void CommitGoalDue()
    {
        if (_syncing || Vm is not { Selected: { } row } vm) return;
        var due = GoalsCardViewModel.Combine(GoalDate.SelectedDate, GoalTime.SelectedTime);
        // A time with no date means nothing yet; and an unchanged deadline needs no write.
        if (due == row.Goal.Due) return;
        if (due is null && GoalDate.SelectedDate is null && GoalTime.SelectedTime is not null) return;
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
        var text = box.Text?.Trim() ?? "";
        if (text.Length == 0) box.Text = step.Text;
        else if (text != step.Text) vm.RenameStep(step, text);
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

    private void Fill(Action set)
    {
        var was = _syncing;
        _syncing = true;
        try { set(); }
        finally { _syncing = was; }
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
        if (due is null && time is not null) return;   // a time with no date means nothing yet
        vm.SetStepDue(step, due);
    }

    private void OnStepDueClear(object? sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is { } step) Vm?.SetStepDue(step, null);
    }
}
