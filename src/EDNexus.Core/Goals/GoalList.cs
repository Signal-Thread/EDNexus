namespace EDNexus.Core.Goals;

/// <summary>
/// Editing and querying operations over a <see cref="GoalsSettings"/>. Mutates the settings in place
/// so the caller can persist <see cref="Data"/> after each change.
/// </summary>
public sealed class GoalList
{
    /// <summary>Wraps <paramref name="data"/>, repairing nulls, blank or duplicate ids and a dangling active goal.</summary>
    public GoalList(GoalsSettings data)
    {
        Data = data;
        Normalize();
    }

    /// <summary>The underlying settings, mutated in place.</summary>
    public GoalsSettings Data { get; }

    /// <summary>All goals in display order.</summary>
    public IReadOnlyList<Goal> Goals => Data.Goals;

    /// <summary>The highlighted goal, or null when none.</summary>
    public Goal? Active => Find(Data.ActiveGoalId);

    /// <summary>Adds a goal; returns null (and adds nothing) when <paramref name="title"/> is blank.</summary>
    public Goal? AddGoal(string title, DateTimeOffset? due = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var goal = new Goal { Id = NewId(), Title = title.Trim(), Due = due };
        Data.Goals.Add(goal);
        if (Active is null)
        {
            Data.ActiveGoalId = goal.Id;
        }

        return goal;
    }

    /// <summary>Removes a goal, moving focus on if it was active.</summary>
    public void RemoveGoal(string id)
    {
        if (Find(id) is not { } goal)
        {
            return;
        }

        Data.Goals.Remove(goal);
        if (Data.ActiveGoalId == id)
        {
            Data.ActiveGoalId = FirstNotDoneId();
        }
    }

    /// <summary>Renames a goal; a blank title is ignored.</summary>
    public void RenameGoal(string id, string title)
    {
        if (Find(id) is { } goal && !string.IsNullOrWhiteSpace(title))
        {
            goal.Title = title.Trim();
        }
    }

    /// <summary>Sets or clears a goal's deadline.</summary>
    public void SetGoalDue(string id, DateTimeOffset? due)
    {
        if (Find(id) is { } goal)
        {
            goal.Due = due;
        }
    }

    /// <summary>Sets a goal's notes; blank notes are stored as null.</summary>
    public void SetNotes(string id, string? notes)
    {
        if (Find(id) is { } goal)
        {
            goal.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        }
    }

    /// <summary>Flips a goal's done flag. Completing the active goal moves focus; un-completing does not.</summary>
    public void ToggleGoal(string id)
    {
        if (Find(id) is not { } goal)
        {
            return;
        }

        goal.IsDone = !goal.IsDone;
        if (goal.IsDone && Data.ActiveGoalId == id)
        {
            Data.ActiveGoalId = FirstNotDoneId();
        }
    }

    /// <summary>Makes an existing goal the highlighted one.</summary>
    public void SetActive(string id)
    {
        if (Find(id) is not null)
        {
            Data.ActiveGoalId = id;
        }
    }

    /// <summary>Adds a step to a goal; returns null when the goal is unknown or <paramref name="text"/> is blank.</summary>
    public GoalStep? AddStep(string goalId, string text, DateTimeOffset? due = null)
    {
        if (Find(goalId) is not { } goal || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var step = new GoalStep { Id = NewId(), Text = text.Trim(), Due = due };
        goal.Steps.Add(step);
        return step;
    }

    /// <summary>Removes a step.</summary>
    public void RemoveStep(string goalId, string stepId)
    {
        if (FindStep(goalId, stepId) is { } step)
        {
            Find(goalId)!.Steps.Remove(step);
        }
    }

    /// <summary>Renames a step; blank text is ignored.</summary>
    public void RenameStep(string goalId, string stepId, string text)
    {
        if (FindStep(goalId, stepId) is { } step && !string.IsNullOrWhiteSpace(text))
        {
            step.Text = text.Trim();
        }
    }

    /// <summary>Sets or clears a step's deadline.</summary>
    public void SetStepDue(string goalId, string stepId, DateTimeOffset? due)
    {
        if (FindStep(goalId, stepId) is { } step)
        {
            step.Due = due;
        }
    }

    /// <summary>Flips a step's done flag.</summary>
    public void ToggleStep(string goalId, string stepId)
    {
        if (FindStep(goalId, stepId) is { } step)
        {
            step.IsDone = !step.IsDone;
        }
    }

    /// <summary>Moves a step by <paramref name="delta"/> positions, clamped to the list ends.</summary>
    public void MoveStep(string goalId, string stepId, int delta)
    {
        if (Find(goalId) is not { } goal || FindStep(goalId, stepId) is not { } step)
        {
            return;
        }

        var from = goal.Steps.IndexOf(step);
        var to = Math.Clamp(from + delta, 0, goal.Steps.Count - 1);
        if (to == from)
        {
            return;
        }

        goal.Steps.RemoveAt(from);
        goal.Steps.Insert(to, step);
    }

    /// <summary>Done and total step counts for a goal.</summary>
    public (int Done, int Total) Progress(Goal g) => (g.Steps.Count(s => s.IsDone), g.Steps.Count);

    /// <summary>The first step that is not done, if any.</summary>
    public GoalStep? NextStep(Goal g) => g.Steps.FirstOrDefault(s => !s.IsDone);

    /// <summary>
    /// Not-done goals by worst <see cref="DueStatus.Effective"/> state descending, then earliest own
    /// deadline (undated last), then title; done goals follow.
    /// </summary>
    public IReadOnlyList<Goal> ByUrgency(DateTimeOffset now) =>
        Data.Goals
            .OrderBy(g => g.IsDone)
            .ThenByDescending(g => DueStatus.Effective(g, now))
            .ThenBy(g => g.Due is null)
            .ThenBy(g => g.Due)
            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Overdue and due-soon tallies across each not-done goal's own deadline and each of its unfinished steps.
    /// </summary>
    public (int Overdue, int DueSoon) Counts(DateTimeOffset now)
    {
        var overdue = 0;
        var soon = 0;
        foreach (var goal in Data.Goals.Where(g => !g.IsDone))
        {
            Tally(DueStatus.For(goal.Due, false, now));
            foreach (var step in goal.Steps)
            {
                Tally(DueStatus.For(step.Due, step.IsDone, now));
            }
        }

        return (overdue, soon);

        void Tally(DueState state)
        {
            if (state == DueState.Overdue)
            {
                overdue++;
            }
            else if (state == DueState.DueSoon)
            {
                soon++;
            }
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private Goal? Find(string? id) =>
        id is null ? null : Data.Goals.FirstOrDefault(g => g.Id == id);

    private GoalStep? FindStep(string goalId, string stepId) =>
        Find(goalId)?.Steps.FirstOrDefault(s => s.Id == stepId);

    private string? FirstNotDoneId() => Data.Goals.FirstOrDefault(g => !g.IsDone)?.Id;

    private void Normalize()
    {
        Data.Goals ??= [];
        Data.Goals.RemoveAll(g => g is null);

        var goalIds = new HashSet<string>();
        foreach (var goal in Data.Goals)
        {
            goal.Title ??= string.Empty;
            if (string.IsNullOrWhiteSpace(goal.Id) || !goalIds.Add(goal.Id))
            {
                do
                {
                    goal.Id = NewId();
                }
                while (!goalIds.Add(goal.Id));
            }

            goal.Steps ??= [];
            goal.Steps.RemoveAll(s => s is null);
            var stepIds = new HashSet<string>();
            foreach (var step in goal.Steps)
            {
                step.Text ??= string.Empty;
                if (string.IsNullOrWhiteSpace(step.Id) || !stepIds.Add(step.Id))
                {
                    do
                    {
                        step.Id = NewId();
                    }
                    while (!stepIds.Add(step.Id));
                }
            }
        }

        if (Data.ActiveGoalId is not null && Find(Data.ActiveGoalId) is null)
        {
            Data.ActiveGoalId = FirstNotDoneId();
        }
    }
}
