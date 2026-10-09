using System.Net;
using System.Net.Http.Headers;
using EDNexus.Core.Journal;
using EDNexus.Core.Reporting;
using EDNexus.Core.Settings;
using EliteDangerous.Inara;
using Xunit;

namespace EDNexus.Tests.Reporting;

/// <summary>Inara batches survive transient failures, and replayed history is never reported.</summary>
public class InaraBridgeResilienceTests
{
    private static readonly InaraClientOptions ClientOptions = new() { AppName = "T", AppVersion = "1", IsBeingDeveloped = true };
    private const string Ok = """{ "header": { "eventStatus": 200 } }""";

    private static AppSettings EnabledSettings()
    {
        var s = new AppSettings();
        s.Reporting.Inara.Enabled = true;
        s.Reporting.Inara.ApiKey = "key";
        return s;
    }

    private static JournalEntry Entry(string json, bool historical = false)
    {
        Assert.True(JournalEntry.TryParse(json, historical, out var e));
        return e;
    }

    private static InaraBridge NewBridge(JournalEventBus bus, HttpMessageHandler handler, TimeSpan minInterval, TimeSpan retryBackoff)
        => new(bus, EnabledSettings(), new InaraClient(ClientOptions, new HttpClient(handler)), TimeSpan.Zero, minInterval,
            isSuppressed: null, log: null, retryBackoff: retryBackoff);

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition()) await Task.Delay(10);
    }

    private const string Commander = """{ "timestamp": "2020-01-01T00:00:00Z", "event": "Commander", "Name": "Jameson" }""";
    private const string Dock = """
        { "timestamp": "2020-01-01T00:10:00Z", "event": "Docked", "StarSystem": "Sol", "StationName": "Abraham Lincoln", "MarketID": 1 }
        """;

    [Fact]
    public async Task A_batch_that_hit_a_transport_error_is_requeued_and_resent()
    {
        var bus = new JournalEventBus();
        var handler = new RecordingHandler(n => n == 1 ? (HttpStatusCode.ServiceUnavailable, "") : (HttpStatusCode.OK, Ok));
        await using var bridge = NewBridge(bus, handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));

        bus.Publish(Entry(Commander));
        bus.Publish(Entry(Dock));
        await WaitForAsync(() => handler.CallCount >= 2);

        Assert.Equal(2, handler.CallCount);
        Assert.Contains("addCommanderTravelDock", handler.Bodies[1]);   // the same dock event, not lost
    }

    [Fact]
    public async Task Retries_are_bounded_and_the_batch_is_eventually_dropped()
    {
        var bus = new JournalEventBus();
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError, "");
        await using var bridge = NewBridge(bus, handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(5));

        bus.Publish(Entry(Commander));
        bus.Publish(Entry(Dock));
        await WaitForAsync(() => handler.CallCount >= 4);
        await Task.Delay(300);

        Assert.Equal(4, handler.CallCount);   // the original attempt plus three retries, then given up
    }

    [Fact]
    public async Task A_hard_error_is_not_retried()
    {
        var bus = new JournalEventBus();
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "header": { "eventStatus": 400, "eventStatusText": "Invalid API key" } }""");
        await using var bridge = NewBridge(bus, handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(5));

        bus.Publish(Entry(Commander));
        bus.Publish(Entry(Dock));
        await WaitForAsync(() => handler.CallCount >= 1);
        await Task.Delay(150);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Replayed_history_including_shutdown_uploads_nothing()
    {
        var bus = new JournalEventBus();
        var handler = new RecordingHandler(HttpStatusCode.OK, Ok);
        await using var bridge = NewBridge(bus, handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(5));

        // EDNexus opened after the game closed: the whole last session is replayed as history.
        bus.Publish(Entry(Commander, historical: true));
        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:00:01Z", "event": "LoadGame", "Commander": "Jameson", "FID": "F1", "Credits": 5000 }""", historical: true));
        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:00:02Z", "event": "Rank", "Combat": 3 }""", historical: true));
        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:00:03Z", "event": "Loadout", "Ship": "python", "ShipID": 7 }""", historical: true));
        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:00:04Z", "event": "Reputation", "Empire": 10.0 }""", historical: true));
        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:20:00Z", "event": "Shutdown" }""", historical: true));
        await Task.Delay(150);
        await bridge.DisposeAsync();

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task History_still_supplies_identity_for_later_live_sends()
    {
        var bus = new JournalEventBus();
        var handler = new RecordingHandler(HttpStatusCode.OK, Ok);
        await using var bridge = NewBridge(bus, handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(5));

        bus.Publish(Entry(Commander, historical: true));
        bus.Publish(Entry(Dock));
        await WaitForAsync(() => handler.CallCount >= 1);

        Assert.Equal(1, handler.CallCount);
        Assert.Contains("\"commanderName\":\"Jameson\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task A_live_shutdown_flushes_without_waiting_out_the_min_interval()
    {
        var bus = new JournalEventBus();
        var handler = new RecordingHandler(HttpStatusCode.OK, Ok);
        await using var bridge = NewBridge(bus, handler, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(5));

        bus.Publish(Entry(Commander));
        bus.Publish(Entry(Dock));                                             // first send: no throttle yet
        await WaitForAsync(() => handler.CallCount >= 1);
        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:11:00Z", "event": "Loadout", "Ship": "python", "ShipID": 7 }"""));
        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:12:00Z", "event": "Shutdown" }"""));
        await WaitForAsync(() => handler.CallCount >= 2, timeoutMs: 5000);   // would take ~30 s if throttled

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Dispose_does_not_wait_out_the_min_interval()
    {
        var bus = new JournalEventBus();
        var handler = new RecordingHandler(HttpStatusCode.OK, Ok);
        var bridge = NewBridge(bus, handler, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(5));

        bus.Publish(Entry(Commander));
        bus.Publish(Entry(Dock));
        await WaitForAsync(() => handler.CallCount >= 1);
        bus.Publish(Entry("""{ "timestamp": "2020-01-01T00:11:00Z", "event": "Loadout", "Ship": "python", "ShipID": 7 }"""));

        await bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, handler.CallCount);
    }

    // --- InaraClient response handling ---

    [Fact]
    public async Task Wrong_typed_elements_in_the_reply_are_skipped_not_treated_as_a_transport_error()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "header": { "eventStatus": 200 }, "events": [ 1, "x", { "eventStatus": "bad" }, { "eventStatus": 204 } ] }""");
        using var client = new InaraClient(ClientOptions, new HttpClient(handler));

        var response = await client.SendAsync(new InaraIdentity { ApiKey = "k", CommanderName = "J" },
            new[] { InaraEvent.SetCommanderCredits(DateTimeOffset.UtcNow, 1) });

        Assert.True(response.IsOk);
        Assert.False(response.IsTransient);
        Assert.Equal(new[] { 0, 204 }, response.Events.Select(e => e.Status));
    }

    [Fact]
    public async Task Http_503_with_retry_after_is_transient_and_carries_the_hint()
    {
        var handler = new RetryAfterHandler();
        using var client = new InaraClient(ClientOptions, new HttpClient(handler));

        var response = await client.SendAsync(new InaraIdentity { ApiKey = "k", CommanderName = "J" },
            new[] { InaraEvent.SetCommanderCredits(DateTimeOffset.UtcNow, 1) });

        Assert.True(response.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(7), response.RetryAfter);
    }

    private sealed class RetryAfterHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(r);
        }
    }
}
