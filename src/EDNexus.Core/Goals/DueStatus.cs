namespace EDNexus.Core.Goals;

/// <summary>Due-date rules for goals and steps. Compares instants, never wall-clock times.</summary>
public static class DueStatus
{
    private static readonly TimeSpan SoonWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// State of a single due date: <see cref="DueState.None"/> when done or undated,
    /// <see cref="DueState.Overdue"/> when <paramref name="due"/> is at or before <paramref name="now"/>,
    /// <see cref="DueState.DueSoon"/> within 24 hours, otherwise <see cref="DueState.Ok"/>.
    /// </summary>
    public static DueState For(DateTimeOffset? due, bool isDone, DateTimeOffset now)
    {
        if (isDone || due is not { } d)
        {
            return DueState.None;
        }

        var remaining = d - now;
        if (remaining <= TimeSpan.Zero)
        {
            return DueState.Overdue;
        }

        return remaining <= SoonWindow ? DueState.DueSoon : DueState.Ok;
    }

    /// <summary>
    /// Worst state across the goal's own due date and each unfinished step; <see cref="DueState.None"/>
    /// when the goal is done.
    /// </summary>
    public static DueState Effective(Goal goal, DateTimeOffset now)
    {
        if (goal.IsDone)
        {
            return DueState.None;
        }

        var worst = For(goal.Due, false, now);
        foreach (var step in goal.Steps)
        {
            var state = For(step.Due, step.IsDone, now);
            if (state > worst)
            {
                worst = state;
            }
        }

        return worst;
    }

    /// <summary>23:59:59 local time on the given calendar date.</summary>
    public static DateTimeOffset EndOfDay(DateTime localDate)
    {
        var local = DateTime.SpecifyKind(localDate.Date.AddHours(23).AddMinutes(59).AddSeconds(59), DateTimeKind.Local);
        return new DateTimeOffset(local);
    }
}
