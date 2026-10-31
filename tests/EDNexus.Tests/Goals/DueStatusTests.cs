using EDNexus.Core.Goals;
using Xunit;

namespace EDNexus.Tests.Goals;

public class DueStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void For_ExactlyNow_IsOverdue() =>
        Assert.Equal(DueState.Overdue, DueStatus.For(Now, false, Now));

    [Fact]
    public void For_24hAhead_IsDueSoon() =>
        Assert.Equal(DueState.DueSoon, DueStatus.For(Now.AddHours(24), false, Now));

    [Fact]
    public void For_24hPlus1sAhead_IsOk() =>
        Assert.Equal(DueState.Ok, DueStatus.For(Now.AddHours(24).AddSeconds(1), false, Now));

    [Fact]
    public void For_Done_IsNone() =>
        Assert.Equal(DueState.None, DueStatus.For(Now.AddHours(-5), true, Now));

    [Fact]
    public void For_NullDue_IsNone() =>
        Assert.Equal(DueState.None, DueStatus.For(null, false, Now));

    [Fact]
    public void For_SameInstantDifferentOffsets_Match()
    {
        var due = Now.AddHours(10);
        var plus10 = due.ToOffset(TimeSpan.FromHours(10));
        var minus5 = due.ToOffset(TimeSpan.FromHours(-5));
        Assert.Equal(DueStatus.For(plus10, false, Now), DueStatus.For(minus5, false, Now));
        Assert.Equal(DueState.DueSoon, DueStatus.For(plus10, false, Now));
    }

    [Fact]
    public void Effective_OverdueStep_MakesGoalOverdue()
    {
        var goal = new Goal
        {
            Due = Now.AddDays(10),
            Steps = { new GoalStep { Due = Now.AddHours(-1) } },
        };
        Assert.Equal(DueState.Overdue, DueStatus.Effective(goal, Now));
    }

    [Fact]
    public void Effective_AllStepsDoneButGoalDuePast_IsOverdue()
    {
        var goal = new Goal
        {
            Due = Now.AddHours(-1),
            Steps = { new GoalStep { IsDone = true, Due = Now.AddHours(-2) } },
        };
        Assert.Equal(DueState.Overdue, DueStatus.Effective(goal, Now));
    }

    [Fact]
    public void Effective_DoneGoal_IsNone()
    {
        var goal = new Goal
        {
            IsDone = true,
            Due = Now.AddHours(-1),
            Steps = { new GoalStep { Due = Now.AddHours(-1) } },
        };
        Assert.Equal(DueState.None, DueStatus.Effective(goal, Now));
    }

    [Fact]
    public void EndOfDay_Is235959Local()
    {
        var result = DueStatus.EndOfDay(new DateTime(2026, 10, 30, 8, 15, 0));
        var local = result.LocalDateTime;
        Assert.Equal(new DateTime(2026, 10, 30, 23, 59, 59), local);
    }
}
