using System.Globalization;
using EDNexus.Core.CommunityGoals;
using EDNexus.Core.Exobio;
using EDNexus.Core.Journal;
using EDNexus.Core.Missions;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.State;

public class CommanderSwitchTests
{
    private static (JournalEventBus Bus, CommanderState State) NewTracker()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        return (bus, state);
    }

    private static void Publish(JournalEventBus bus, string json)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry), "sample JSON failed to parse");
        bus.Publish(entry);
    }

    private static void GiveCommanderACarrierAndSuit(JournalEventBus bus, string name)
    {
        Publish(bus, $$"""{ "timestamp":"2026-08-01T10:00:00Z", "event":"Commander", "Name":"{{name}}" }""");
        Publish(bus, $$"""{ "timestamp":"2026-08-01T10:00:01Z", "event":"LoadGame", "Commander":"{{name}}", "Credits":5 }""");
        Publish(bus, """
        { "timestamp":"2026-08-01T10:00:02Z", "event":"CarrierStats", "Name":"Nomad's Reach", "Callsign":"K7Q-B3L",
          "FuelLevel":638, "JumpRangeCurr":500.0,
          "SpaceUsage": { "TotalCapacity":25000, "FreeSpace":20300 } }
        """);
        Publish(bus, """
        { "timestamp":"2026-08-01T10:00:03Z", "event":"CarrierJumpRequest", "SystemName":"Ga Gu",
          "DepartureTime":"2026-08-01T11:00:00Z" }
        """);
        Publish(bus, """{ "timestamp":"2026-08-01T10:00:04Z", "event":"SuitLoadout", "SuitName":"tacticalsuit_class3", "SuitName_Localised":"Dominator Suit" }""");
    }

    [Fact]
    public void A_different_commander_does_not_inherit_the_previous_ones_carrier_or_suit()
    {
        var (bus, state) = NewTracker();
        GiveCommanderACarrierAndSuit(bus, "Alpha");
        Assert.Equal("Nomad's Reach", state.CarrierName);

        Publish(bus, """{ "timestamp":"2026-08-02T10:00:00Z", "event":"Commander", "Name":"Bravo" }""");
        Publish(bus, """{ "timestamp":"2026-08-02T10:00:01Z", "event":"LoadGame", "Commander":"Bravo", "Credits":9 }""");

        Assert.Equal("Bravo", state.Name);
        Assert.Null(state.CarrierName);
        Assert.Null(state.CarrierCallsign);
        Assert.Equal(0, state.CarrierFuel);
        Assert.Equal(0, state.CarrierJumpRange);
        Assert.Equal(0, state.CarrierUsedCapacity);
        Assert.Null(state.CarrierPendingSystem);
        Assert.Null(state.CarrierPendingDeparture);
        Assert.Null(state.SuitName);
        Assert.Null(state.SuitSymbol);
        Assert.Equal(0, state.SuitClass);
    }

    [Fact]
    public void A_switch_announced_only_by_LoadGame_also_resets()
    {
        var (bus, state) = NewTracker();
        GiveCommanderACarrierAndSuit(bus, "Alpha");

        Publish(bus, """{ "timestamp":"2026-08-02T10:00:01Z", "event":"LoadGame", "Commander":"Bravo" }""");

        Assert.Null(state.CarrierName);
        Assert.Null(state.CarrierPendingSystem);
    }

    [Fact]
    public void Logging_back_in_as_the_same_commander_keeps_the_carrier_and_pending_jump()
    {
        var (bus, state) = NewTracker();
        GiveCommanderACarrierAndSuit(bus, "Alpha");

        Publish(bus, """{ "timestamp":"2026-08-01T10:30:00Z", "event":"Commander", "Name":"alpha" }""");
        Publish(bus, """{ "timestamp":"2026-08-01T10:30:01Z", "event":"LoadGame", "Commander":"Alpha" }""");

        Assert.Equal("Nomad's Reach", state.CarrierName);
        Assert.Equal("Ga Gu", state.CarrierPendingSystem);
        Assert.Equal("Dominator Suit", state.SuitName);
    }

    [Fact]
    public void The_very_first_commander_is_not_treated_as_a_switch()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """
        { "timestamp":"2026-08-01T10:00:02Z", "event":"CarrierStats", "Name":"Nomad's Reach", "Callsign":"K7Q-B3L", "FuelLevel":10 }
        """);
        Publish(bus, """{ "timestamp":"2026-08-01T10:00:03Z", "event":"LoadGame", "Commander":"Alpha" }""");

        Assert.Equal("Nomad's Reach", state.CarrierName);   // seen before the name: nothing to reset
        Assert.Equal(10, state.CarrierFuel);
    }

    [Fact]
    public void A_non_string_departure_time_does_not_throw()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-08-01T10:00:03Z", "event":"CarrierJumpRequest", "SystemName":"Ga Gu", "DepartureTime":123 }""");

        Assert.Equal("Ga Gu", state.CarrierPendingSystem);
        Assert.Null(state.CarrierPendingDeparture);
    }
}

public class ExobiologyDeathTests
{
    private static string Scan(string scanType) => $$"""
    { "timestamp":"2026-08-01T11:00:00Z", "event":"ScanOrganic", "ScanType":"{{scanType}}",
      "Genus":"$Codex_Ent_Stratum_Genus_Name;", "Genus_Localised":"Stratum",
      "Species":"$Codex_Ent_Stratum_07_Name;", "Species_Localised":"Stratum Tectonicas",
      "SystemAddress":2871051298217, "Body":12 }
    """;

    private static void Publish(JournalEventBus bus, string json)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry));
        bus.Publish(entry);
    }

    [Fact]
    public void Dying_forfeits_unsold_data_and_in_progress_runs_but_not_banked_sales()
    {
        var bus = new JournalEventBus();
        var tracker = new ExobiologyTracker(bus, new CommanderState());
        Publish(bus, """
        { "timestamp":"2026-08-01T10:30:00Z", "event":"SellOrganicData",
          "BioData":[{"Species":"$Codex_Ent_Bacterial_01_Name;","Value":1000,"Bonus":500}] }
        """);
        Publish(bus, Scan("Log"));
        Publish(bus, Scan("Sample"));
        Publish(bus, Scan("Analyse"));
        Assert.Single(tracker.Session.Pending);

        Publish(bus, """{ "timestamp":"2026-08-01T12:00:00Z", "event":"Died", "KillerName":"Thargoid" }""");

        Assert.Empty(tracker.Session.Pending);
        Assert.Empty(tracker.Scans);
        Assert.Null(tracker.ActiveScan);
        Assert.Equal(1500, tracker.Session.SoldValue);   // already paid out
    }

    [Fact]
    public void Dying_mid_run_drops_the_active_scan()
    {
        var bus = new JournalEventBus();
        var tracker = new ExobiologyTracker(bus, new CommanderState());
        Publish(bus, Scan("Log"));
        Assert.NotNull(tracker.ActiveScan);

        Publish(bus, """{ "timestamp":"2026-08-01T12:00:00Z", "event":"Died" }""");

        Assert.Null(tracker.ActiveScan);
    }
}

public class InvariantTimeParsingTests
{
    private static void Publish(JournalEventBus bus, string json)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry));
        bus.Publish(entry);
    }

    private static void UnderCulture(string name, Action body)
    {
        var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
            body();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = saved;
        }
    }

    [Theory]
    [InlineData("ar-SA")]   // Hijri calendar
    [InlineData("th-TH")]   // Buddhist calendar
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void Mission_expiry_parses_identically_in_any_culture(string culture)
    {
        UnderCulture(culture, () =>
        {
            var bus = new JournalEventBus();
            var tracker = new MissionTracker(bus);
            Publish(bus, """
            { "timestamp":"2026-08-16T12:00:00Z", "event":"MissionAccepted", "Faction":"F", "Name":"Mission_Massacre",
              "LocalisedName":"Kill", "KillCount":1, "Expiry":"2026-08-23T12:00:00Z", "Reward":1, "MissionID":1 }
            """);

            Assert.Equal(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero), Assert.Single(tracker.Active).Expiry);
        });
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("de-DE")]
    public void Community_goal_expiry_parses_identically_in_any_culture(string culture)
    {
        UnderCulture(culture, () =>
        {
            var bus = new JournalEventBus();
            var tracker = new CommunityGoalTracker(bus);
            Publish(bus, """
            { "timestamp":"2026-08-16T12:00:00Z", "event":"CommunityGoal",
              "CurrentGoals":[{ "CGID":1, "Title":"T", "SystemName":"S", "MarketName":"M",
                                "Expiry":"2026-08-23T12:00:00Z", "IsComplete":false, "CurrentTotal":1, "PlayerContribution":0 }] }
            """);

            Assert.Equal(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero), Assert.Single(tracker.Active).Expiry);
        });
    }

    [Fact]
    public void An_expiry_without_an_offset_is_taken_as_utc()
    {
        var bus = new JournalEventBus();
        var tracker = new MissionTracker(bus);
        Publish(bus, """
        { "timestamp":"2026-08-16T12:00:00Z", "event":"MissionAccepted", "Faction":"F", "Name":"Mission_Massacre",
          "LocalisedName":"Kill", "KillCount":1, "Expiry":"2026-08-23T12:00:00", "Reward":1, "MissionID":1 }
        """);

        Assert.Equal(TimeSpan.Zero, Assert.Single(tracker.Active).Expiry!.Value.Offset);
    }
}
