using System.Text.Json;
using EDNexus.App.ViewModels;
using EDNexus.Core.Goals;
using EDNexus.Core.State;

namespace EDNexus.App.Tests;

public sealed class GoalsCardViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private GoalsSettings _saved = Fixture();
    private readonly List<GoalsSettings> _saves = [];
    private bool _dev;
    private DateTimeOffset _now = Start;

    /// <summary>
    /// g1 (active): s1 done, s2 open, s3 due in 5h. g2: own due 2h ago, one open step. g3: due in 3 days.
    /// </summary>
    private static GoalsSettings Fixture() => new()
    {
        ActiveGoalId = "g1",
        Goals =
        [
            new Goal
            {
                Id = "g1",
                Title = "Unlock Farseer",
                Steps =
                [
                    new GoalStep { Id = "s1", Text = "Reach Friendly", IsDone = true },
                    new GoalStep { Id = "s2", Text = "Collect exceptions" },
                    new GoalStep { Id = "s3", Text = "Engineer the FSD", Due = Start.AddHours(5) },
                ],
            },
            new Goal
            {
                Id = "g2",
                Title = "Haul steel",
                Due = Start.AddHours(-2),
                Steps = [new GoalStep { Id = "s4", Text = "Buy steel" }],
            },
            new Goal { Id = "g3", Title = "Explore Colonia", Due = Start.AddDays(3) },
        ],
    };

    // Only the dev flag, RNG and goals delegates are used; the host and every other service are absent.
    private DashboardContext Context() => new(
        null!, () => _dev, new Random(3), null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
        getGoals: () => _saved,
        saveGoals: g => _saves.Add(g));

    private GoalsCardViewModel Card()
    {
        var card = new GoalsCardViewModel(Context(), () => _now);
        card.Update(new CommanderState());
        return card;
    }

    private string SavedJson() => JsonSerializer.Serialize(_saved);

    [Fact]
    public void Card_HasGoalsIdentity()
    {
        var card = Card();
        Assert.Equal("goals", card.Id);
        Assert.Equal("GOALS", card.Title);
        Assert.Equal(452, card.Width);
    }

    [Fact]
    public void Focused_IsActiveGoal_WithNextStepFlagged()
    {
        var card = Card();

        Assert.True(card.HasGoals);
        Assert.Equal("g1", card.Focused!.Goal.Id);
        Assert.True(card.Focused.IsActive);
        Assert.Equal("1/3", card.Focused.ProgressText);
        Assert.Equal(["s1", "s2", "s3"], card.Steps.Select(s => s.Step.Id));
        Assert.Equal([false, true, false], card.Steps.Select(s => s.IsNext));
        Assert.True(card.Steps[0].IsDone);
        Assert.True(card.Steps[2].IsDueSoon);
    }

    [Fact]
    public void ToggleStep_SavesAndAdvancesNext()
    {
        var card = Card();

        card.ToggleStep(card.Steps[1]);

        Assert.Single(_saves);
        Assert.Same(_saved, _saves[0]);
        Assert.True(_saved.Goals[0].Steps[1].IsDone);
        Assert.Equal([false, false, true], card.Steps.Select(s => s.IsNext));
        Assert.Equal("2/3", card.Focused!.ProgressText);
    }

    [Fact]
    public void Others_ExcludesFocused_AndCarriesStatus()
    {
        var card = Card();

        Assert.DoesNotContain(card.Others, r => r.Goal.Id == "g1");
        var g2 = Assert.Single(card.Others, r => r.Goal.Id == "g2");
        Assert.True(g2.IsOverdue);
        Assert.Equal(DueState.Overdue, g2.State);
        var g3 = Assert.Single(card.Others, r => r.Goal.Id == "g3");
        Assert.False(g3.IsOverdue);
        Assert.False(g3.IsDueSoon);
    }

    [Fact]
    public void Counts_AndBadgeColourFlag()
    {
        var card = Card();

        Assert.Equal(1, card.OverdueCount);
        Assert.Equal(1, card.DueSoonCount);
        Assert.True(card.HasAttention);
        Assert.True(card.BadgeIsOverdue);

        card.ToggleGoal(card.Others.Single(r => r.Goal.Id == "g2"));

        Assert.Equal(0, card.OverdueCount);
        Assert.Equal(1, card.DueSoonCount);
        Assert.True(card.HasAttention);
        Assert.False(card.BadgeIsOverdue);

        card.ToggleStep(card.Steps[2]);

        Assert.False(card.HasAttention);
    }

    [Fact]
    public void Update_AfterClockPassesDue_FlipsToOverdue()
    {
        var card = Card();
        Assert.True(card.Steps[2].IsDueSoon);

        _now = Start.AddHours(6);
        card.Update(new CommanderState());

        Assert.True(card.Steps[2].IsOverdue);
        Assert.False(card.Steps[2].IsDueSoon);
        Assert.True(card.Focused!.IsOverdue);
        // g1 has no deadline of its own, so the header's own-state highlight stays off.
        Assert.Equal(DueState.None, card.Focused.OwnState);
        Assert.False(card.Focused.OwnIsOverdue);
        Assert.Equal(2, card.OverdueCount);

        // g2's own deadline is past, so its own state is overdue too.
        Assert.True(card.Others.Single(r => r.Goal.Id == "g2").OwnIsOverdue);
    }

    [Fact]
    public void Update_WithNothingChanged_KeepsTheSameRows()
    {
        var card = Card();
        var row = card.Steps[0];

        _now = Start.AddMinutes(1);
        card.Update(new CommanderState());

        Assert.Same(row, card.Steps[0]);
    }

    [Fact]
    public void Focus_ChangesFocusedAndPersistsActiveId()
    {
        var card = Card();

        card.Focus(card.Others.Single(r => r.Goal.Id == "g3"));

        Assert.Equal("g3", card.Focused!.Goal.Id);
        Assert.Equal("g3", _saved.ActiveGoalId);
        Assert.Single(_saves);
        Assert.Empty(card.Steps);
        Assert.DoesNotContain(card.Others, r => r.Goal.Id == "g3");
    }

    [Fact]
    public void AddGoal_Blank_DoesNothing()
    {
        var card = Card();

        card.AddGoal("   ");

        Assert.Equal(3, _saved.Goals.Count);
        Assert.Empty(_saves);
    }

    [Fact]
    public void AddStep_ShowsTheStoredTrimmedText()
    {
        var card = Card();

        card.AddStep("  Visit Deciat  ");

        Assert.Single(_saves);
        Assert.Equal("Visit Deciat", card.Steps[^1].Text);
    }

    [Fact]
    public void DevMode_UsesSampleGoals_AndNeverSaves()
    {
        _dev = true;
        var before = SavedJson();
        var card = Card();

        Assert.StartsWith("sample-", card.Focused!.Goal.Id);
        Assert.All(card.AllByUrgency, r => Assert.StartsWith("sample-", r.Goal.Id));

        card.ToggleStep(card.Steps[0]);
        card.AddStep("Dev step");
        card.AddGoal("Dev goal");
        card.Focus(card.Others[0]);
        card.RenameGoal(card.Focused!, "Renamed");
        card.SetGoalDue(card.Focused!, Start);
        card.SetNotes(card.Focused!, "notes");
        card.RenameStep(card.Steps[0], "Renamed step");
        card.SetStepDue(card.Steps[0], Start);
        card.MoveStep(card.Steps[0], 1);
        card.DeleteStep(card.Steps[0]);
        card.ToggleGoal(card.Focused!);
        card.DeleteGoal(card.AllByUrgency[0]);
        card.Update(new CommanderState());

        Assert.Empty(_saves);
        Assert.Equal(before, SavedJson());
    }

    // Each case is one edit made with rows from the live list; every one would change the live goals
    // (or save) if it ran against them.
    private static readonly Dictionary<string, Action<GoalsCardViewModel, GoalRow, GoalRow, StepRow>> LiveEdits = new()
    {
        ["AddGoal"] = (c, _, _, _) => c.AddGoal("Dev goal"),
        ["AddStep"] = (c, _, _, _) => c.AddStep("Dev step"),
        ["AddStepTo"] = (c, g, _, _) => c.AddStepTo(g, "Dev step"),
        ["ToggleStep"] = (c, _, _, s) => c.ToggleStep(s),
        ["Focus"] = (c, _, other, _) => c.Focus(other),
        ["ToggleGoal"] = (c, g, _, _) => c.ToggleGoal(g),
        ["DeleteGoal"] = (c, g, _, _) => c.DeleteGoal(g),
        ["DeleteStep"] = (c, _, _, s) => c.DeleteStep(s),
        ["RenameGoal"] = (c, g, _, _) => c.RenameGoal(g, "Renamed"),
        ["SetGoalDue"] = (c, g, _, _) => c.SetGoalDue(g, Start),
        ["SetNotes"] = (c, g, _, _) => c.SetNotes(g, "notes"),
        ["RenameStep"] = (c, _, _, s) => c.RenameStep(s, "Renamed step"),
        ["SetStepDue"] = (c, _, _, s) => c.SetStepDue(s, Start),
        ["MoveStep"] = (c, _, _, s) => c.MoveStep(s, 1),
    };

    public static TheoryData<string> LiveEditNames() => new(LiveEdits.Keys);

    [Theory]
    [MemberData(nameof(LiveEditNames))]
    public void DevMode_TurnedOnBetweenTicks_EditNeverTouchesTheLiveList(string edit)
    {
        var before = SavedJson();
        var card = Card();
        var liveGoal = card.Focused!;
        var otherGoal = card.Others.Single(r => r.Goal.Id == "g3");
        var liveStep = card.Steps[1];

        // Developer mode flips on, and this edit arrives before the next tick.
        _dev = true;
        LiveEdits[edit](card, liveGoal, otherGoal, liveStep);

        Assert.Empty(_saves);
        Assert.Equal(before, SavedJson());
        Assert.StartsWith("sample-", card.Focused!.Goal.Id);
    }

    [Fact]
    public void AddStepTo_AddsToThatGoal_WithoutChangingFocus()
    {
        var card = Card();

        card.AddStepTo(card.Others.Single(r => r.Goal.Id == "g3"), "  Sell data  ");

        Assert.Single(_saves);
        Assert.Equal("Sell data", Assert.Single(_saved.Goals[2].Steps).Text);
        Assert.Equal("g1", card.Focused!.Goal.Id);
        Assert.Equal("g1", _saved.ActiveGoalId);
    }

    [Fact]
    public void AddGoal_SelectsTheNewGoal_AndPersists()
    {
        var card = Card();

        card.AddGoal("  Reach Colonia  ");

        Assert.Single(_saves);
        Assert.Equal(4, _saved.Goals.Count);
        Assert.Equal("Reach Colonia", card.Selected!.Title);
        Assert.Same(_saved.Goals[3], card.Selected.Goal);
        Assert.Equal("g1", card.Focused!.Goal.Id);
    }

    [Fact]
    public void FocusedHeader_UsesTheGoalsOwnDeadline_NotItsSteps()
    {
        _saved.Goals[2].Steps.Add(new GoalStep { Id = "s5", Text = "Late step", Due = Start.AddHours(-1) });
        _saved.ActiveGoalId = "g3";
        var card = Card();

        Assert.Equal(DueState.Ok, card.Focused!.OwnState);
        Assert.False(card.Focused.OwnIsOverdue);
        Assert.False(card.Focused.OwnIsDueSoon);
        Assert.Equal(DueState.Overdue, card.Focused.State);
        Assert.True(card.Focused.IsOverdue);
    }

    [Fact]
    public void LeavingDevMode_RestoresSavedGoals()
    {
        var before = SavedJson();
        var card = Card();

        _dev = true;
        card.Update(new CommanderState());
        card.ToggleStep(card.Steps[0]);
        card.AddGoal("Dev goal");

        _dev = false;
        card.Update(new CommanderState());

        Assert.Equal(before, SavedJson());
        Assert.Empty(_saves);
        Assert.Equal(["g1", "g2", "g3"], card.AllByUrgency.Select(r => r.Goal.Id).Order());
        Assert.Equal("g1", card.Focused!.Goal.Id);

        // Back on live data, edits persist again.
        card.ToggleStep(card.Steps[1]);
        Assert.Single(_saves);
    }
}
