using EDNexus.Core.Journal;
using EDNexus.Core.Ship;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.State;

public class FsdInjectionTests
{
    private static (JournalEventBus bus, CommanderState state) NewTracker()
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

    [Theory]
    [InlineData("FSD Basic", 1.25)]
    [InlineData("FSD Standard", 1.5)]
    [InlineData("FSD Premium", 2.0)]
    public void Injection_synthesis_sets_the_pending_multiplier(string name, double expected)
    {
        var (bus, state) = NewTracker();
        Publish(bus, $$"""{ "timestamp":"2026-10-30T23:16:41Z", "event":"Synthesis", "Name":"{{name}}", "Materials":[ { "Name":"carbon", "Count":1 } ] }""");

        Assert.Equal(expected, state.FsdInjectionMultiplier);
    }

    [Fact]
    public void Other_synthesis_leaves_the_multiplier_alone()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-10-30T23:16:41Z", "event":"Synthesis", "Name":"Limpet Basic", "Materials":[ { "Name":"iron", "Count":1 } ] }""");

        Assert.Equal(1, state.FsdInjectionMultiplier);
        Assert.Null(FsdInjection.MultiplierFor("Limpet Basic"));
    }

    [Fact]
    public void The_next_jump_consumes_the_injection()
    {
        var (bus, state) = NewTracker();
        Publish(bus, """{ "timestamp":"2026-10-30T23:16:41Z", "event":"Synthesis", "Name":"FSD Standard", "Materials":[] }""");
        Publish(bus, """{ "timestamp":"2026-10-30T23:20:00Z", "event":"FSDJump", "StarSystem":"Sol", "BoostUsed":2 }""");

        Assert.Equal(1, state.FsdInjectionMultiplier);
    }
}
