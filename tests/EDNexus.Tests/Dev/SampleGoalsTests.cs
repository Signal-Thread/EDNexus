using EDNexus.Core.Dev;
using EDNexus.Core.Goals;
using Xunit;

namespace EDNexus.Tests.Dev;

public class SampleGoalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_CoversEveryDueState()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var data = SampleGoals.Create(new Random(seed), Now);
            var states = data.Goals.Select(g => DueStatus.Effective(g, Now)).ToList();

            Assert.InRange(data.Goals.Count, 3, 4);
            Assert.All(data.Goals, g => Assert.InRange(g.Steps.Count, 3, 6));
            Assert.Contains(DueState.Overdue, states);
            Assert.Contains(DueState.DueSoon, states);
            Assert.Contains(DueState.Ok, states);
            Assert.Contains(data.Goals, g => g.Due is null);
            Assert.Contains(data.Goals, g => g.Steps.Any(s => s.IsDone));
        }
    }

    [Fact]
    public void Create_IsDeterministicPerSeed()
    {
        var a = SampleGoals.Create(new Random(7), Now);
        var b = SampleGoals.Create(new Random(7), Now);

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b));
        Assert.NotSame(a, b);
        Assert.NotSame(a.Goals[0], b.Goals[0]);
    }

    [Fact]
    public void Create_ActiveGoalExists()
    {
        var data = SampleGoals.Create(new Random(3), Now);

        Assert.NotNull(data.ActiveGoalId);
        Assert.Contains(data.Goals, g => g.Id == data.ActiveGoalId);
    }
}
