using System;
using System.IO;
using System.Linq;
using EDNexus.Core.Colonisation;
using EDNexus.Core.Dev;
using EDNexus.Core.Journal;
using EDNexus.Core.ShoppingList;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.Colonisation;

public class ColonisationProjectsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ednx-colo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly DateTimeOffset June1 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static ColonisationSite Site(
        long id, string? station, bool complete = false, bool failed = false, DateTimeOffset? updated = null,
        params (string Sym, string Name, int Req, int Prov)[] lines) => new()
    {
        MarketId = id,
        StationName = station,
        StarSystem = "Sol",
        Progress = 0.25,
        Complete = complete,
        Failed = failed,
        Updated = updated ?? June1,
        Resources = lines.Select(l => new ColonisationResource(l.Name, l.Sym, l.Req, l.Prov, 100)).ToList(),
    };

    private static (JournalEventBus bus, CommanderState state, ColonisationTracker tracker) NewTracker()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        return (bus, state, new ColonisationTracker(bus, state));
    }

    private static void Publish(JournalEventBus bus, string json)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry), "sample JSON failed to parse");
        bus.Publish(entry);
    }

    private static string Depot(long id, string ts, string resources, bool complete = false) =>
        "{ \"timestamp\":\"" + ts + "\", \"event\":\"ColonisationConstructionDepot\", \"MarketID\":" + id
        + ", \"ConstructionProgress\":0.1, \"ConstructionComplete\":" + (complete ? "true" : "false")
        + ", \"ConstructionFailed\":false, \"ResourcesRequired\":[ " + resources + " ] }";

    private const string Steel100 = """{ "Name":"$steel_name;", "Name_Localised":"Steel", "RequiredAmount":100, "ProvidedAmount":40, "Payment":1 }""";
    private const string Alu50 = """{ "Name":"$aluminium_name;", "Name_Localised":"Aluminium", "RequiredAmount":50, "ProvidedAmount":0, "Payment":1 }""";
    private const string LoadGame = """{ "timestamp":"2026-06-03T00:00:00Z", "event":"LoadGame", "Commander":"CMDR Test" }""";

    private static (string, string, int, int)[] Lines(params (string, string, int, int)[] l) => l;

    // ---- model ----

    [Fact]
    public void DisplayName_prefers_station_then_system_then_market_id()
    {
        Assert.Equal("Orbital A", Site(1, "Orbital A").DisplayName);
        Assert.Equal("Sol", Site(2, null).DisplayName);
        Assert.Equal("Market 3", new ColonisationSite { MarketId = 3 }.DisplayName);
    }

    [Fact]
    public void IsOpen_is_false_for_complete_failed_or_fully_supplied()
    {
        Assert.True(Site(1, "A", lines: Lines(("steel", "Steel", 100, 40))).IsOpen);
        Assert.False(Site(2, "B", complete: true, lines: Lines(("steel", "Steel", 100, 40))).IsOpen);
        Assert.False(Site(3, "C", failed: true, lines: Lines(("steel", "Steel", 100, 40))).IsOpen);
        Assert.False(Site(4, "D", lines: Lines(("steel", "Steel", 100, 100))).IsOpen);
        Assert.False(Site(5, "E").IsOpen);   // no resources at all
    }

    // ---- aggregation ----

    [Fact]
    public void Combine_sums_remaining_per_commodity_with_per_project_breakdown()
    {
        var a = Site(1, "Alpha", lines: Lines(("steel", "Steel", 100, 40), ("copper", "Copper", 10, 0)));
        var b = Site(2, "Beta", lines: Lines(("steel", "Steel", 300, 100), ("water", "Water", 20, 20)));

        var combined = ColonisationAggregator.Combine(new[] { a, b });

        var steel = Assert.Single(combined, c => c.Symbol == "steel");
        Assert.Equal(400, steel.Required);
        Assert.Equal(140, steel.Provided);
        Assert.Equal(60 + 200, steel.Remaining);
        Assert.Equal(new[] { "Beta", "Alpha" }, steel.Projects.Select(p => p.ProjectName));   // biggest share first
        Assert.Equal(new[] { 200, 60 }, steel.Projects.Select(p => p.Remaining));

        Assert.DoesNotContain(combined, c => c.Symbol == "water");   // fully delivered: nothing owed
        Assert.Equal(new[] { "steel", "copper" }, combined.Select(c => c.Symbol));   // biggest shortfall first
    }

    [Fact]
    public void Combine_excludes_completed_and_failed_projects()
    {
        var open = Site(1, "Open", lines: Lines(("steel", "Steel", 100, 0)));
        var done = Site(2, "Done", complete: true, lines: Lines(("steel", "Steel", 999, 0)));
        var failed = Site(3, "Failed", failed: true, lines: Lines(("steel", "Steel", 999, 0)));

        var steel = Assert.Single(ColonisationAggregator.Combine(new[] { open, done, failed }));
        Assert.Equal(100, steel.Remaining);
        Assert.Single(steel.Projects);
    }

    [Fact]
    public void Combine_breaks_ties_by_name_and_handles_empty_input()
    {
        Assert.Empty(ColonisationAggregator.Combine(Array.Empty<ColonisationSite>()));

        var s = Site(1, "A", lines: Lines(("zinc", "Zinc", 5, 0), ("aluminium", "Aluminium", 5, 0)));
        Assert.Equal(new[] { "Aluminium", "Zinc" }, ColonisationAggregator.Combine(new[] { s }).Select(c => c.Name));
    }

    [Fact]
    public void Combine_does_not_overflow_on_huge_totals()
    {
        var a = Site(1, "A", lines: Lines(("steel", "Steel", int.MaxValue, 0)));
        var b = Site(2, "B", lines: Lines(("steel", "Steel", int.MaxValue, 0)));
        Assert.Equal(int.MaxValue, Assert.Single(ColonisationAggregator.Combine(new[] { a, b })).Remaining);
    }

    // ---- tracker: journal events, selection, view ----

    [Fact]
    public void Several_depots_are_tracked_and_the_latest_is_selected()
    {
        var (bus, _, tracker) = NewTracker();
        Publish(bus, Depot(11, "2026-06-03T00:00:00Z", Steel100));
        Publish(bus, Depot(22, "2026-06-03T01:00:00Z", Alu50));

        Assert.Equal(2, tracker.Sites.Count);
        Assert.Equal(22, tracker.SelectedSite!.MarketId);
        Assert.Equal(new long[] { 22, 11 }, tracker.Sites.Select(s => s.MarketId));   // newest first
    }

    [Fact]
    public void Select_switches_project_and_ignores_unknown_ids()
    {
        var (bus, _, tracker) = NewTracker();
        Publish(bus, Depot(11, "2026-06-03T00:00:00Z", Steel100));
        Publish(bus, Depot(22, "2026-06-03T01:00:00Z", Alu50));

        tracker.Select(11);
        Assert.Equal(11, tracker.SelectedSite!.MarketId);
        Assert.Single(tracker.FocusedSites);

        tracker.Select(999);
        Assert.Equal(11, tracker.SelectedSite!.MarketId);
    }

    [Fact]
    public void ShowAll_focuses_every_open_project_and_docking_does_not_leave_it()
    {
        var (bus, _, tracker) = NewTracker();
        Publish(bus, Depot(11, "2026-06-03T00:00:00Z", Steel100));
        Publish(bus, Depot(33, "2026-06-03T00:30:00Z", Steel100, complete: true));
        tracker.SetShowAll(true);
        Publish(bus, Depot(22, "2026-06-03T01:00:00Z", Alu50));

        Assert.True(tracker.ShowAll);
        Assert.Equal(new long[] { 22, 11 }, tracker.FocusedSites.Select(s => s.MarketId));   // completed 33 left out
        Assert.Equal(2, tracker.Combined.Count);
    }

    [Fact]
    public void Contribution_updates_only_its_own_project()
    {
        var (bus, _, tracker) = NewTracker();
        Publish(bus, Depot(11, "2026-06-03T00:00:00Z", Steel100));
        Publish(bus, Depot(22, "2026-06-03T01:00:00Z", Steel100));
        Publish(bus, """
        { "timestamp":"2026-06-03T02:00:00Z", "event":"ColonisationContribution", "MarketID":11,
          "Contributions":[ { "Name":"$Steel_name;", "Name_Localised":"Steel", "Amount":10 } ] }
        """);

        Assert.Equal(50, tracker.Sites.Single(s => s.MarketId == 11).Resources[0].Provided);
        Assert.Equal(40, tracker.Sites.Single(s => s.MarketId == 22).Resources[0].Provided);
        Assert.Equal(11, tracker.SelectedSite!.MarketId);
    }

    [Fact]
    public void Forget_removes_the_project_and_clears_selection()
    {
        var (bus, _, tracker) = NewTracker();
        Publish(bus, Depot(11, "2026-06-03T00:00:00Z", Steel100));
        Assert.True(tracker.Forget(11));
        Assert.False(tracker.Forget(11));
        Assert.Null(tracker.SelectedSite);
        Assert.Empty(tracker.Sites);
    }

    [Fact]
    public void Changed_fires_for_select_show_all_forget_and_restore()
    {
        var (bus, _, tracker) = NewTracker();
        Publish(bus, Depot(11, "2026-06-03T00:00:00Z", Steel100));
        Publish(bus, Depot(22, "2026-06-03T01:00:00Z", Alu50));
        var raised = 0;
        tracker.Changed += () => raised++;

        tracker.Select(11);
        tracker.SetShowAll(true);
        tracker.Forget(22);
        tracker.Restore(new ColonisationProjectData());
        Assert.Equal(4, raised);
    }

    // ---- malformed or missing journal fields ----

    [Theory]
    [InlineData("""{ "timestamp":"2026-06-03T00:00:00Z", "event":"ColonisationConstructionDepot", "ResourcesRequired":[] }""")]
    [InlineData("""{ "timestamp":"2026-06-03T00:00:00Z", "event":"ColonisationConstructionDepot", "MarketID":"abc", "ResourcesRequired":[] }""")]
    [InlineData("""{ "timestamp":"2026-06-03T00:00:00Z", "event":"ColonisationConstructionDepot", "MarketID":5 }""")]
    [InlineData("""{ "timestamp":"2026-06-03T00:00:00Z", "event":"ColonisationConstructionDepot", "MarketID":5, "ResourcesRequired":"nope" }""")]
    [InlineData("""{ "timestamp":"2026-06-03T00:00:00Z", "event":"ColonisationContribution", "MarketID":5 }""")]
    public void Malformed_events_are_ignored_without_throwing(string json)
    {
        var (bus, _, tracker) = NewTracker();
        Publish(bus, json);
        Assert.Empty(tracker.Sites);
        Assert.Null(tracker.SelectedSite);
    }

    [Fact]
    public void Resource_lines_with_missing_or_wrong_typed_fields_become_zeroes()
    {
        var (bus, _, tracker) = NewTracker();
        Publish(bus, Depot(7, "2026-06-03T00:00:00Z",
            """{ "Name":"$steel_name;", "RequiredAmount":"lots", "ProvidedAmount":null }, { "Name_Localised":"Mystery" }, 17"""));

        var site = tracker.SelectedSite!;
        Assert.Equal(7, site.MarketId);
        Assert.All(site.Resources, r => Assert.Equal(0, r.Required));
        Assert.False(site.IsOpen);
    }

    // ---- persistence ----

    [Fact]
    public void Store_round_trips_projects_selection_and_view_mode()
    {
        var store = new ColonisationProjectStore(_dir);
        var (bus, _, tracker) = NewTracker();
        Publish(bus, Depot(11, "2026-06-03T00:00:00Z", Steel100 + "," + Alu50));
        Publish(bus, Depot(22, "2026-06-03T01:00:00Z", Alu50));
        tracker.Select(11);
        tracker.SetShowAll(true);

        Assert.True(store.TrySave("CMDR Test", tracker.Snapshot()));

        var (_, _, fresh) = NewTracker();
        fresh.Restore(store.Load("CMDR Test"));

        Assert.Equal(2, fresh.Sites.Count);
        Assert.True(fresh.ShowAll);
        Assert.Equal(11, fresh.SelectedSite!.MarketId);
        var restored = fresh.Sites.Single(s => s.MarketId == 11);
        Assert.Equal(2, restored.Resources.Count);
        Assert.Equal(60, restored.Resources.Single(r => r.Symbol == "steel").Remaining);
    }

    [Fact]
    public void Store_keeps_each_commander_separate_and_survives_odd_names()
    {
        var store = new ColonisationProjectStore(_dir);
        var data = new ColonisationProjectData { Projects = [ColonisationProjectRecord.From(Site(1, "A"))] };
        Assert.True(store.TrySave("CMDR One", data));

        Assert.Single(store.Load("cmdr one").ToSites());   // case-insensitive
        Assert.Empty(store.Load("CMDR Two").ToSites());
        Assert.True(store.TrySave("we/ird:na*me?", data));
        Assert.Single(store.Load("we/ird:na*me?").ToSites());
        Assert.Empty(store.Load("").ToSites());
        Assert.False(store.TrySave("  ", data));
    }

    [Fact]
    public void Store_moves_a_corrupt_file_aside_and_loads_empty()
    {
        var store = new ColonisationProjectStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor("CMDR Test"), "{ not json");

        Assert.Empty(store.Load("CMDR Test").ToSites());
        Assert.False(File.Exists(store.PathFor("CMDR Test")));
        Assert.Single(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void Hand_edited_records_are_repaired_or_dropped()
    {
        var store = new ColonisationProjectStore(_dir);
        Directory.CreateDirectory(_dir);

        File.WriteAllText(store.PathFor("CMDR Test"), """
        { "Projects": [
            null,
            { "MarketId": 0, "StationName": "no id" },
            { "MarketId": 5, "StationName": "first" },
            { "MarketId": 5, "StationName": "later duplicate wins", "Resources": [] }
        ] }
        """);
        var site = Assert.Single(store.Load("CMDR Test").ToSites());
        Assert.Equal("later duplicate wins", site.StationName);

        File.WriteAllText(store.PathFor("CMDR Test"), """
        { "Projects": [ { "MarketId": 5, "Progress": 7.5,
              "Resources": [ null, { "Name": "", "Symbol": "" }, { "Name": "Steel", "Symbol": "steel", "Required": -4, "Provided": 3 },
                             { "Name": "Water", "Symbol": null, "Required": 10, "Provided": 2 } ] } ] }
        """);
        var repaired = Assert.Single(store.Load("CMDR Test").ToSites());
        Assert.Equal(1, repaired.Progress);   // clamped
        Assert.Equal(2, repaired.Resources.Count);   // null and unnamed lines dropped
        Assert.Equal(0, repaired.Resources.Single(r => r.Symbol == "steel").Required);   // negative clamped
        Assert.Equal("water", repaired.Resources.Single(r => r.Name == "Water").Symbol);   // symbol derived from name
    }

    [Fact]
    public void Store_with_json_null_or_wrong_shape_loads_empty()
    {
        var store = new ColonisationProjectStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor("CMDR Test"), "null");
        Assert.Empty(store.Load("CMDR Test").ToSites());

        File.WriteAllText(store.PathFor("CMDR Test"), """{ "Projects": "nope" }""");
        Assert.Empty(store.Load("CMDR Test").ToSites());
    }

    [Fact]
    public void Restore_never_rolls_back_a_newer_live_project()
    {
        var (bus, _, tracker) = NewTracker();
        Publish(bus, Depot(11, "2026-06-03T05:00:00Z", Steel100));   // live: provided 40

        var stale = Site(11, "Stale", updated: June1, lines: Lines(("steel", "Steel", 100, 5)));
        var other = Site(99, "Other");
        tracker.Restore(new ColonisationProjectData
        {
            Projects = [ColonisationProjectRecord.From(stale), ColonisationProjectRecord.From(other)],
        });

        Assert.Equal(40, tracker.Sites.Single(s => s.MarketId == 11).Resources[0].Provided);
        Assert.Equal(2, tracker.Sites.Count);
        Assert.Equal(11, tracker.SelectedSite!.MarketId);   // live selection kept
    }

    [Fact]
    public void Restore_alone_makes_the_most_recent_saved_project_the_selection()
    {
        var (_, _, tracker) = NewTracker();
        var older = Site(1, "Older", updated: June1);
        var newer = Site(2, "Newer", updated: June1.AddDays(1));
        tracker.Restore(new ColonisationProjectData
        {
            Projects = [ColonisationProjectRecord.From(older), ColonisationProjectRecord.From(newer)],
        });
        Assert.Equal(2, tracker.SelectedSite!.MarketId);
    }

    [Fact]
    public void Persistence_restores_on_load_game_and_saves_changes_on_flush()
    {
        var store = new ColonisationProjectStore(_dir);
        store.TrySave("CMDR Test", new ColonisationProjectData
        {
            Projects = [ColonisationProjectRecord.From(Site(5, "Saved", lines: Lines(("steel", "Steel", 10, 0))))],
        });

        var (bus, state, tracker) = NewTracker();
        using var persistence = new ColonisationProjectPersistence(bus, state, tracker, store);

        Assert.Empty(tracker.Sites);
        Publish(bus, LoadGame);
        Assert.Equal(5, tracker.SelectedSite!.MarketId);

        Publish(bus, Depot(11, "2026-06-03T01:00:00Z", Steel100));
        persistence.Flush();
        Assert.Equal(new long[] { 5, 11 }, store.Load("CMDR Test").ToSites().Select(s => s.MarketId).OrderBy(i => i));
    }

    [Fact]
    public void Persistence_does_nothing_before_the_commander_is_known_or_while_suppressed()
    {
        var store = new ColonisationProjectStore(_dir);
        var (bus, state, tracker) = NewTracker();
        using var persistence = new ColonisationProjectPersistence(bus, state, tracker, store, () => true);

        Publish(bus, Depot(11, "2026-06-03T01:00:00Z", Steel100));
        persistence.Flush();
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);   // no commander yet

        Publish(bus, LoadGame);
        Publish(bus, Depot(12, "2026-06-03T02:00:00Z", Alu50));
        persistence.Flush();
        Assert.False(File.Exists(store.PathFor("CMDR Test")));   // developer mode: never written
    }

    [Fact]
    public void Forgetting_the_selected_project_falls_back_to_a_remaining_one()
    {
        var (bus, _, tracker) = NewTracker();
        Publish(bus, Depot(11, "2026-06-03T00:00:00Z", Steel100));
        Publish(bus, Depot(22, "2026-06-03T01:00:00Z", Alu50));
        Publish(bus, Depot(33, "2026-06-03T02:00:00Z", Steel100));

        Assert.True(tracker.Forget(33));
        Assert.Equal(22, tracker.SelectedSite!.MarketId);   // newest survivor, not "nothing"
    }

    [Fact]
    public void Persistence_does_not_carry_projects_over_to_a_different_commander()
    {
        var store = new ColonisationProjectStore(_dir);
        var (bus, state, tracker) = NewTracker();
        using var persistence = new ColonisationProjectPersistence(bus, state, tracker, store);

        Publish(bus, LoadGame);
        Publish(bus, Depot(11, "2026-06-03T01:00:00Z", Steel100));
        Publish(bus, """{ "timestamp":"2026-06-03T03:00:00Z", "event":"LoadGame", "Commander":"CMDR Other" }""");
        persistence.Flush();

        Assert.Empty(tracker.Sites);
        Assert.Single(store.Load("CMDR Test").ToSites());   // saved under the first commander
        Assert.Empty(store.Load("CMDR Other").ToSites());   // never mixed into the second
    }

    [Fact]
    public void Persistence_flushes_pending_changes_on_dispose()
    {
        var store = new ColonisationProjectStore(_dir);
        var (bus, state, tracker) = NewTracker();
        var persistence = new ColonisationProjectPersistence(bus, state, tracker, store);

        Publish(bus, LoadGame);
        Publish(bus, Depot(11, "2026-06-03T01:00:00Z", Steel100));
        persistence.Dispose();
        persistence.Dispose();   // idempotent

        Assert.Single(store.Load("CMDR Test").ToSites());
    }

    // ---- shopping list and dev source ----

    [Fact]
    public void Shopping_list_sums_the_same_commodity_across_projects()
    {
        var a = Site(1, "A", lines: Lines(("steel", "Steel", 100, 40)));
        var b = Site(2, "B", lines: Lines(("steel", "Steel", 50, 0)));

        var rows = ShoppingListService.Build(ShoppingListService.FromColonisationProjects(new[] { a, b }), new CommanderState());
        Assert.Equal(110, Assert.Single(rows).Needed);
    }

    [Fact]
    public void Dev_source_publishes_several_projects_through_the_real_pipeline()
    {
        var (bus, _, tracker) = NewTracker();
        foreach (var line in new ColonisationProjectsSampleSource().Sample(new Random(7)))
        {
            Assert.True(JournalEntry.TryParse(line, historical: false, out var entry), line);
            bus.Publish(entry);
        }

        Assert.InRange(tracker.Sites.Count, 3, 4);
        Assert.Contains(tracker.Sites, s => s.Complete);
        Assert.Contains(tracker.Sites, s => s.IsOpen);
        Assert.All(tracker.Sites, s => Assert.False(string.IsNullOrEmpty(s.StationName)));
        Assert.NotEmpty(tracker.Combined);
    }

    [Fact]
    public void Dev_source_is_registered_under_the_colonisation_card()
    {
        Assert.Contains(new DeveloperMode().Sources, s => s is ColonisationProjectsSampleSource && s.CardKey == "colonisation");
    }
}
