using EDNexus.Core.Goals;
using EDNexus.Core.Settings;
using Xunit;

namespace EDNexus.Tests.Settings;

public class GoalsSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ednexus-goals-settings-").FullName;
    private string Path => System.IO.Path.Combine(_root, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void RoundTrip_PreservesGoalsStepsDuesAndActive()
    {
        var due = new DateTimeOffset(2026, 11, 2, 18, 30, 0, TimeSpan.Zero);
        var stepDue = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var store = new SettingsStore(Path);
        var settings = store.Load();
        settings.Goals = new GoalsSettings
        {
            ActiveGoalId = "g2",
            Goals =
            {
                new Goal { Id = "g1", Title = "Reach Colonia", IsDone = true },
                new Goal
                {
                    Id = "g2", Title = "Build FSD", Notes = "Need selenium", Due = due,
                    Steps =
                    {
                        new GoalStep { Id = "s1", Text = "Mine selenium", IsDone = true },
                        new GoalStep { Id = "s2", Text = "Unlock engineer", Due = stepDue },
                    },
                },
            },
        };
        store.Save(settings);

        var goals = new SettingsStore(Path).Load().Goals;

        Assert.Equal("g2", goals.ActiveGoalId);
        Assert.Equal(2, goals.Goals.Count);
        Assert.True(goals.Goals[0].IsDone);
        var g2 = goals.Goals[1];
        Assert.Equal("Build FSD", g2.Title);
        Assert.Equal("Need selenium", g2.Notes);
        Assert.Equal(due, g2.Due);
        Assert.Equal(2, g2.Steps.Count);
        Assert.True(g2.Steps[0].IsDone);
        Assert.Equal(stepDue, g2.Steps[1].Due);
    }

    [Fact]
    public void Load_JsonWithoutGoalsSection_GivesEmpty()
    {
        File.WriteAllText(Path, "{ \"AutoDownloadUpdates\": false }");

        var goals = new SettingsStore(Path).Load().Goals;

        Assert.Empty(goals.Goals);
        Assert.Null(goals.ActiveGoalId);
    }

    [Fact]
    public void Load_GoalWithoutDueFields_Loads()
    {
        File.WriteAllText(Path, "{ \"Goals\": { \"Goals\": [ { \"Id\": \"a\", \"Title\": \"T\", \"Steps\": [ { \"Id\": \"s\", \"Text\": \"x\" } ] } ] } }");

        var goal = Assert.Single(new SettingsStore(Path).Load().Goals.Goals);

        Assert.Null(goal.Due);
        Assert.Null(Assert.Single(goal.Steps).Due);
    }

    [Fact]
    public void Load_NullStepsList_DoesNotThrow()
    {
        File.WriteAllText(Path, "{ \"Goals\": { \"Goals\": [ { \"Id\": \"a\", \"Title\": \"T\", \"Steps\": null } ] } }");

        var settings = new SettingsStore(Path).Load();
        var list = new GoalList(settings.Goals);

        Assert.Single(list.Goals);
        Assert.NotNull(list.Goals[0].Steps);
    }
}
