namespace EDNexus.Core.Goals;

/// <summary>How urgent a goal or step is, ordered so <see cref="Overdue"/> is the greatest.</summary>
public enum DueState
{
    /// <summary>No due date, or already done.</summary>
    None,

    /// <summary>Due more than 24 hours from now.</summary>
    Ok,

    /// <summary>Due within the next 24 hours.</summary>
    DueSoon,

    /// <summary>Due now or already past.</summary>
    Overdue,
}

/// <summary>One step toward completing a <see cref="Goal"/>.</summary>
public sealed class GoalStep
{
    /// <summary>Stable identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What needs doing.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>True once the step is finished.</summary>
    public bool IsDone { get; set; }

    /// <summary>Optional deadline, as an absolute instant.</summary>
    public DateTimeOffset? Due { get; set; }
}

/// <summary>A commander-authored goal with optional notes, deadline and steps.</summary>
public sealed class Goal
{
    /// <summary>Stable identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Short title shown in the list.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Free-form notes.</summary>
    public string? Notes { get; set; }

    /// <summary>True once the goal is finished.</summary>
    public bool IsDone { get; set; }

    /// <summary>Optional deadline, as an absolute instant.</summary>
    public DateTimeOffset? Due { get; set; }

    /// <summary>Ordered steps toward the goal.</summary>
    public List<GoalStep> Steps { get; set; } = [];
}

/// <summary>The persisted Goals card state.</summary>
public sealed class GoalsSettings
{
    /// <summary>All goals, in display order.</summary>
    public List<Goal> Goals { get; set; } = [];

    /// <summary>Id of the goal highlighted on the card, if any.</summary>
    public string? ActiveGoalId { get; set; }
}
