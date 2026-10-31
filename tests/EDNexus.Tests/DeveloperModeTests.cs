using System.Linq;
using EDNexus.Core.Colonisation;
using EDNexus.Core.Dev;
using EDNexus.Core.Journal;
using EDNexus.Core.Market;
using EDNexus.Core.Mining;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests;

public class DeveloperModeTests
{
    // A fixed seed keeps these deterministic run-to-run.
    private static Random Seeded() => new(20260706);

    [Fact]
    public void Every_sample_line_is_valid_parseable_journal_json()
    {
        var dev = new DeveloperMode();
        var rng = Seeded();

        foreach (var source in dev.Sources)
        {
            var lines = source.Sample(rng);
            Assert.NotEmpty(lines);
            foreach (var line in lines)
            {
                Assert.True(JournalEntry.TryParse(line, historical: false, out var entry),
                    $"{source.CardKey} produced unparseable JSON: {line}");
                Assert.NotEqual(default, entry.Timestamp);   // real timestamp so LastUpdated advances
                Assert.False(string.IsNullOrEmpty(entry.Event));
            }
        }
    }

    [Fact]
    public void Randomize_all_drives_every_card_through_the_real_pipeline()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        var colonisation = new ColonisationTracker(bus, state);

        new DeveloperMode().Randomize(bus, Seeded());

        Assert.False(string.IsNullOrEmpty(state.Name));        // ship sampler set the commander
        Assert.False(string.IsNullOrEmpty(state.StarSystem));  // location sampler set the system
        Assert.True(state.Balance > 0);
        Assert.False(state.Cargo.IsEmpty);
        Assert.True(state.Materials.TotalCount > 0);

        var site = colonisation.ActiveSite;
        Assert.NotNull(site);
        Assert.NotEmpty(site!.Resources);
        Assert.False(string.IsNullOrEmpty(site.StationName));  // stamped from the Docked event
    }

    [Fact]
    public void Randomize_single_card_only_touches_that_card()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);

        new DeveloperMode().Randomize(bus, Seeded(), cardKey: "materials");

        Assert.True(state.Materials.TotalCount > 0);
        Assert.True(state.Cargo.IsEmpty);                 // cargo sampler did not run
        Assert.True(string.IsNullOrEmpty(state.StarSystem));  // location sampler did not run
    }

    [Fact]
    public void Colonisation_sample_stocks_hold_so_cross_reference_highlight_shows()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        var colonisation = new ColonisationTracker(bus, state);

        new DeveloperMode().Randomize(bus, Seeded(), cardKey: "colonisation");

        var list = colonisation.ActiveSite!.BuildShoppingList(state.Cargo);
        Assert.Contains(list, i => i.InHold > 0);   // at least one commodity is carried
    }

    [Fact]
    public void Market_sample_loads_a_board_and_values_the_hold()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        var market = new MarketTracker(bus, state);

        new DeveloperMode().Randomize(bus, Seeded(), cardKey: "market");

        var snap = market.Current;
        Assert.NotNull(snap);
        Assert.NotEmpty(snap!.Commodities);
        Assert.False(string.IsNullOrEmpty(snap.StationName));  // carried on the Market event
        Assert.Contains(snap.Commodities, c => c.Sellable);    // the station buys something

        // The sampler stocks the hold with commodities the station has demand for.
        Assert.NotEmpty(snap.ValuateHold(state.Cargo));
    }

    [Fact]
    public void Shopping_sample_exists_and_moves_held_counts_for_tracked_items()
    {
        var dev = new DeveloperMode();
        var source = Assert.Single(dev.Sources, s => s.CardKey == "shopping");
        Assert.Equal("Shopping list", source.DisplayName);

        var rng = Seeded();
        foreach (var line in source.Sample(rng))
            Assert.True(JournalEntry.TryParse(line, historical: false, out _), $"unparseable: {line}");

        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        dev.Randomize(bus, rng, cardKey: "shopping");

        Assert.False(state.Cargo.IsEmpty);
        Assert.True(state.Materials.TotalCount > 0);
        // The Odyssey catalog has no Consumables, so only the three categories it does hold are checked.
        Assert.NotEmpty(state.OnFoot.Components);
        Assert.NotEmpty(state.OnFoot.Data);
        Assert.NotEmpty(state.OnFoot.Items);
    }

    [Fact]
    public void Mining_sample_produces_a_prospect_with_at_least_one_material()
    {
        var bus = new JournalEventBus();
        var mining = new MiningTracker(bus);
        var dev = new DeveloperMode();
        var rng = Seeded();

        // A single draw can land on a rock with zero materials by chance, so draw a few times rather
        // than asserting on one — the point is that the sampler's output is usable, not that every
        // single draw is non-empty.
        ProspectResult? withMaterials = null;
        for (var i = 0; i < 10 && withMaterials is null; i++)
        {
            dev.Randomize(bus, rng, cardKey: "mining");
            if (mining.Latest is { Materials.Count: > 0 } hit) withMaterials = hit;
        }

        Assert.NotNull(withMaterials);
        Assert.All(withMaterials!.Materials, m => Assert.False(string.IsNullOrEmpty(m.Symbol)));
    }
}
