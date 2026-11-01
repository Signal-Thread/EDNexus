using EDNexus.Core.Goals;
using Xunit;

namespace EDNexus.Tests.Goals;

public class GoalListTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static GoalList NewList() => new(new GoalsSettings());

    [Fact]
    public void AddGoal_Blank_ReturnsNull()
    {
        var list = NewList();
        Assert.Null(list.AddGoal("   "));
        Assert.Null(list.AddGoal(""));
        Assert.Empty(list.Goals);
    }

    [Fact]
    public void AddStep_Whitespace_ReturnsNull()
    {
        var list = NewList();
        var goal = list.AddGoal("g")!;
        Assert.Null(list.AddStep(goal.Id, " \t"));
        Assert.Empty(goal.Steps);
    }

    [Fact]
    public void FirstGoal_BecomesActive()
    {
        var list = NewList();
        var first = list.AddGoal("first")!;
        var second = list.AddGoal("second")!;
        Assert.Same(first, list.Active);
        Assert.Equal(first.Id, list.Data.ActiveGoalId);
        Assert.NotSame(second, list.Active);
        Assert.Equal(32, first.Id.Length);
    }

    [Fact]
    public void RemoveActive_FallsBackToNextNotDone()
    {
        var list = NewList();
        var a = list.AddGoal("a")!;
        var b = list.AddGoal("b")!;
        var c = list.AddGoal("c")!;
        list.ToggleGoal(b.Id); // b done
        list.RemoveGoal(a.Id);
        Assert.Same(c, list.Active);
        list.RemoveGoal(c.Id);
        Assert.Null(list.Active);
        Assert.Null(list.Data.ActiveGoalId);
    }

    [Fact]
    public void ToggleGoalDone_Reopen_TakesFocusOnlyWhenNothingIsFocused()
    {
        var list = NewList();
        var only = list.AddGoal("only")!;
        list.ToggleGoal(only.Id);
        Assert.Null(list.Active);

        list.ToggleGoal(only.Id);   // reopened with nothing in focus
        Assert.Same(only, list.Active);

        var other = list.AddGoal("other")!;
        list.ToggleGoal(other.Id);
        list.ToggleGoal(other.Id);  // reopened while `only` is focused: focus stays
        Assert.Same(only, list.Active);
    }

    [Fact]
    public void ToggleGoalDone_OnActive_MovesFocus()
    {
        var list = NewList();
        var a = list.AddGoal("a")!;
        var b = list.AddGoal("b")!;
        list.ToggleGoal(a.Id);
        Assert.True(a.IsDone);
        Assert.Same(b, list.Active);

        list.ToggleGoal(a.Id); // un-complete does not move focus
        Assert.False(a.IsDone);
        Assert.Same(b, list.Active);

        list.ToggleGoal(b.Id);
        Assert.Same(a, list.Active);
        list.ToggleGoal(a.Id);
        Assert.Null(list.Active);
    }

    [Fact]
    public void MoveStep_ClampsAtEnds()
    {
        var list = NewList();
        var g = list.AddGoal("g")!;
        var s1 = list.AddStep(g.Id, "1")!;
        var s2 = list.AddStep(g.Id, "2")!;
        var s3 = list.AddStep(g.Id, "3")!;

        list.MoveStep(g.Id, s1.Id, -1);
        Assert.Equal([s1, s2, s3], g.Steps);

        list.MoveStep(g.Id, s1.Id, 1);
        Assert.Equal([s2, s1, s3], g.Steps);

        list.MoveStep(g.Id, s1.Id, 10);
        Assert.Equal([s2, s3, s1], g.Steps);

        list.MoveStep(g.Id, s1.Id, -10);
        Assert.Equal([s1, s2, s3], g.Steps);
    }

    [Fact]
    public void NextStep_SkipsDone()
    {
        var list = NewList();
        var g = list.AddGoal("g")!;
        var s1 = list.AddStep(g.Id, "1")!;
        var s2 = list.AddStep(g.Id, "2")!;
        list.ToggleStep(g.Id, s1.Id);
        Assert.Same(s2, list.NextStep(g));
        list.ToggleStep(g.Id, s2.Id);
        Assert.Null(list.NextStep(g));
    }

    [Fact]
    public void Progress_CountsDone()
    {
        var list = NewList();
        var g = list.AddGoal("g")!;
        var s1 = list.AddStep(g.Id, "1")!;
        list.AddStep(g.Id, "2");
        list.AddStep(g.Id, "3");
        list.ToggleStep(g.Id, s1.Id);
        Assert.Equal((1, 3), list.Progress(g));
    }

    [Fact]
    public void ByUrgency_OrdersOverdueDueSoonOkNoneDone()
    {
        var list = NewList();
        var none = list.AddGoal("none")!;
        var ok = list.AddGoal("ok", Now.AddDays(5))!;
        var soon = list.AddGoal("soon", Now.AddHours(3))!;
        var overdue = list.AddGoal("overdue", Now.AddHours(-1))!;
        var done = list.AddGoal("done", Now.AddHours(-9))!;
        list.ToggleGoal(done.Id);

        Assert.Equal([overdue, soon, ok, none, done], list.ByUrgency(Now));
    }

    [Fact]
    public void ByUrgency_StepUrgencyLiftsGoalAndTiesBreakOnDueThenTitle()
    {
        var list = NewList();
        var b = list.AddGoal("b", Now.AddDays(9))!;
        var a = list.AddGoal("a", Now.AddDays(9))!;
        var early = list.AddGoal("z", Now.AddDays(2))!;
        var lifted = list.AddGoal("lifted")!;
        list.AddStep(lifted.Id, "late", Now.AddMinutes(-5));

        Assert.Equal([lifted, early, a, b], list.ByUrgency(Now));
    }

    [Fact]
    public void Counts_IgnoreDoneGoalsAndDoneSteps()
    {
        var list = NewList();
        var g1 = list.AddGoal("g1", Now.AddHours(-1))!; // overdue
        var s1 = list.AddStep(g1.Id, "s1", Now.AddHours(2))!; // soon
        var s2 = list.AddStep(g1.Id, "s2", Now.AddHours(-2))!; // overdue but done
        list.ToggleStep(g1.Id, s2.Id);
        var g2 = list.AddGoal("g2", Now.AddHours(1))!; // soon
        var g3 = list.AddGoal("g3", Now.AddHours(-3))!; // done goal, ignored
        list.AddStep(g3.Id, "s3", Now.AddHours(-3));
        list.ToggleGoal(g3.Id);

        Assert.Equal((1, 2), list.Counts(Now));
        Assert.NotNull(s1);
        Assert.NotNull(g2);
    }

    [Fact]
    public void Normalize_NullListsDuplicateIdsDanglingActive_DoesNotThrow()
    {
        var data = new GoalsSettings
        {
            ActiveGoalId = "missing",
            Goals =
            [
                null!,
                new Goal { Id = "x", Title = "done", IsDone = true, Steps = null! },
                new Goal
                {
                    Id = "x",
                    Title = "dup",
                    Steps = [null!, new GoalStep { Id = "", Text = "a" }, new GoalStep { Id = "s", Text = "b" }, new GoalStep { Id = "s", Text = "c" }],
                },
                new Goal { Id = " ", Title = "blank" },
            ],
        };

        var list = new GoalList(data);

        Assert.Equal(3, list.Goals.Count);
        Assert.Equal(3, list.Goals.Select(g => g.Id).Distinct().Count());
        Assert.All(list.Goals, g => Assert.False(string.IsNullOrWhiteSpace(g.Id)));
        Assert.NotNull(list.Goals[0].Steps);
        var steps = list.Goals[1].Steps;
        Assert.Equal(3, steps.Count);
        Assert.Equal(3, steps.Select(s => s.Id).Distinct().Count());
        Assert.All(steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Id)));
        Assert.Equal("dup", list.Active!.Title);
        Assert.Same(data, list.Data);

        var nullLists = new GoalList(new GoalsSettings { Goals = null! });
        Assert.Empty(nullLists.Goals);
        Assert.Null(nullLists.Active);
    }
}
