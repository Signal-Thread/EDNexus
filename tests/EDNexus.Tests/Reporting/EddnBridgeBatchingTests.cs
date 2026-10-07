using System.Net;
using System.Text.Json.Nodes;
using EDNexus.Core.Journal;
using EDNexus.Core.Reporting;
using EDNexus.Core.Settings;
using EliteDangerous.Eddn;
using Xunit;

namespace EDNexus.Tests.Reporting;

/// <summary>The bridge coalesces runs of FSSSignalDiscovered into one fsssignaldiscovered message.</summary>
public class EddnBridgeBatchingTests
{
    private static readonly EddnClientOptions Options = new()
    {
        SoftwareName = "T", SoftwareVersion = "1", RetryDelay = TimeSpan.Zero,
    };

    private static JournalEntry Entry(string json, bool historical = false)
    {
        Assert.True(JournalEntry.TryParse(json, historical, out var e));
        return e;
    }

    private const string Jump = """
    { "timestamp": "2020-01-01T00:00:00Z", "event": "FSDJump",
      "StarSystem": "Sol", "SystemAddress": 1, "StarPos": [0.0, 0.0, 0.0] }
    """;

    private static string Signal(string name, string ts = "2020-01-01T00:00:05Z")
        => $$"""{ "timestamp": "{{ts}}", "event": "FSSSignalDiscovered", "SystemAddress": 1, "SignalName": "{{name}}", "IsStation": true }""";

    private static (EddnBridge Bridge, RecordingHandler Handler, JournalEventBus Bus) NewBridge(TimeSpan window)
    {
        var settings = new AppSettings();
        settings.Reporting.Eddn.Enabled = true;
        var bus = new JournalEventBus();
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var bridge = new EddnBridge(bus, settings, new EddnUploader(Options, new HttpClient(handler)),
            new EddnJournalTransformer(Options), window);
        return (bridge, handler, bus);
    }

    [Fact]
    public async Task A_run_of_signals_is_sent_as_one_message_when_the_next_event_arrives()
    {
        var (bridge, handler, bus) = NewBridge(TimeSpan.FromMinutes(5));
        await using var _ = bridge;

        bus.Publish(Entry(Jump));
        bus.Publish(Entry(Signal("A")));
        bus.Publish(Entry(Signal("B")));
        bus.Publish(Entry(Signal("C")));

        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:00:06Z", "event": "Music", "MusicTrack": "Exploration" }"""));
        await bridge.DisposeAsync();

        Assert.Equal(2, handler.CallCount);   // the FSDJump, and ONE message for the three signals
        var env = handler.Bodies.Select(b => JsonNode.Parse(b)!).Single(n => (string?)n["$schemaRef"] == EddnSchemas.FssSignalDiscovered);
        Assert.Equal(3, env["message"]!["signals"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_trailing_run_is_flushed_by_the_timer()
    {
        var (bridge, handler, bus) = NewBridge(TimeSpan.FromMilliseconds(50));
        await using var _ = bridge;

        bus.Publish(Entry(Jump));
        bus.Publish(Entry(Signal("A")));
        for (var i = 0; i < 200 && handler.CallCount < 2; i++) await Task.Delay(25);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task A_trailing_run_is_flushed_on_dispose()
    {
        var (bridge, handler, bus) = NewBridge(TimeSpan.FromMinutes(5));

        bus.Publish(Entry(Jump));
        bus.Publish(Entry(Signal("A")));
        await bridge.DisposeAsync();

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Historical_signals_are_never_uploaded()
    {
        var (bridge, handler, bus) = NewBridge(TimeSpan.FromMilliseconds(20));
        await using var _ = bridge;

        bus.Publish(Entry(Jump, historical: true));
        bus.Publish(Entry(Signal("A"), historical: true));
        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:00:06Z", "event": "Music", "MusicTrack": "x" }""", historical: true));
        await Task.Delay(100);
        await bridge.DisposeAsync();

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Odyssey_order_signals_before_the_jump_use_the_arrival_system()
    {
        var (bridge, handler, bus) = NewBridge(TimeSpan.FromMinutes(5));
        await using var _ = bridge;
        bus.Publish(Entry(Jump, historical: true));   // tracked location is Sol (address 1)

        // Odyssey writes the new system's signals before its FSDJump line.
        const string Other = """
        { "timestamp": "2020-01-01T00:10:00Z", "event": "FSDJump",
          "StarSystem": "Alpha Centauri", "SystemAddress": 2, "StarPos": [3.0, 0.0, 3.0] }
        """;
        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:09:59Z", "event": "FSSSignalDiscovered", "SystemAddress": 2, "SignalName": "X", "IsStation": true }"""));
        bus.Publish(Entry(Other));
        await bridge.DisposeAsync();

        var signals = handler.Bodies.Select(b => JsonNode.Parse(b)!).Single(n => (string?)n["$schemaRef"] == EddnSchemas.FssSignalDiscovered);
        Assert.Equal("Alpha Centauri", (string?)signals["message"]!["StarSystem"]);
    }
}
