using EDNexus.Core.Goals;

namespace EDNexus.Core.Dev;

/// <summary>
/// Offline sample data for the Goals card in developer mode: four Elite-flavoured goals, one per
/// <see cref="DueState"/> (overdue, due soon, comfortably ok, undated), with a mix of done steps.
/// Every call returns a brand-new object graph, never shared with the real settings.
/// </summary>
public static class SampleGoals
{
    private sealed record Template(string Title, string Notes, string[] Steps);

    private static readonly Template[] Templates =
    [
        new("Unlock Felicity Farseer",
            "Grade 5 FSD boost is worth the grind.",
            ["Reach Friendly with Farseer", "Collect 10 Datamined Wake Exceptions", "Scan 5 Guardian structures",
             "Hand in Soontill Relics", "Visit Deciat", "Engineer a Grade 1 FSD"]),
        new("Build the Colonisation Depot",
            "Haul commodities to the construction site.",
            ["Buy 800t of Steel", "Fetch Titanium from Hutton Orbital", "Load up Aluminium", "Fit the fleet carrier cargo bay",
             "Deliver the first load", "Deliver the final load"]),
        new("Road to Elite in Exploration",
            "Aim for the Colonia bubble.",
            ["Fit a detailed surface scanner", "Plot a neutron route to Colonia", "Scan 50 unmapped worlds", "Sell data at Colonia Hub",
             "Refuel at a scoopable star", "Land on a Biological signal"]),
        new("Prepare Fleet Carrier Upkeep",
            "Keep the tritium and credits topped up.",
            ["Check the weekly upkeep bill", "Refill the tritium depot", "Reschedule the jump", "Update the market orders",
             "Top up the carrier balance", "Confirm crew services"]),
    ];

    /// <summary>
    /// Builds sample goals relative to <paramref name="now"/>. The same <paramref name="rng"/> seed and
    /// <paramref name="now"/> always yield the same result, and every <see cref="DueState"/> is represented.
    /// </summary>
    public static GoalsSettings Create(Random rng, DateTimeOffset now)
    {
        // Own-deadline offsets per goal: overdue, due soon, comfortably ok, undated.
        TimeSpan?[] goalOffsets =
        [
            -TimeSpan.FromHours(rng.Next(6, 72)),
            TimeSpan.FromHours(rng.Next(2, 20)),
            TimeSpan.FromDays(rng.Next(3, 11)),
            null,
        ];

        var settings = new GoalsSettings();
        for (var g = 0; g < Templates.Length; g++)
        {
            var template = Templates[g];
            var goal = new Goal
            {
                Id = $"sample-goal-{g + 1}",
                Title = template.Title,
                Notes = template.Notes,
                Due = goalOffsets[g] is { } offset ? now + offset : null,
            };

            var stepCount = rng.Next(3, 7);
            var doneCount = rng.Next(1, stepCount);
            for (var s = 0; s < stepCount; s++)
            {
                goal.Steps.Add(new GoalStep
                {
                    Id = $"sample-step-{g + 1}-{s + 1}",
                    Text = template.Steps[s],
                    IsDone = s < doneCount,
                });
            }

            // One unfinished step carries its own deadline, never worse than the goal's own state.
            if (goal.Due is not null && goal.Steps[^1] is { IsDone: false } last)
            {
                last.Due = g switch
                {
                    1 => now + TimeSpan.FromHours(rng.Next(1, 20)),
                    2 => now + TimeSpan.FromDays(rng.Next(2, 7)),
                    _ => now - TimeSpan.FromHours(rng.Next(1, 12)),
                };
            }

            settings.Goals.Add(goal);
        }

        settings.ActiveGoalId = settings.Goals[rng.Next(settings.Goals.Count)].Id;
        return settings;
    }
}
