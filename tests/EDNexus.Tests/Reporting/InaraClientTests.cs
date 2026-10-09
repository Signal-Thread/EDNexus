using System.Net;
using EliteDangerous.Inara;
using Xunit;

namespace EDNexus.Tests.Reporting;

public class InaraClientTests
{
    private static readonly InaraClientOptions Options = new()
    {
        AppName = "EDNexus.Tests",
        AppVersion = "1.0.0",
        IsBeingDeveloped = true,
    };

    private static readonly InaraIdentity Identity = new()
    {
        ApiKey = "test-key",
        CommanderName = "Jameson",
        CommanderFrontierID = "F123",
    };

    [Fact]
    public async Task Sends_header_and_events_with_expected_keys()
    {
        var handler = new RecordingHandler(body: """{ "header": { "eventStatus": 200 }, "events": [ { "eventStatus": 200 } ] }""");
        using var client = new InaraClient(Options, new HttpClient(handler));

        var ts = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var events = new[] { InaraEvent.SetCommanderCredits(ts, 1_000_000) };

        var response = await client.SendAsync(Identity, events);

        Assert.True(response.IsOk);
        var sent = handler.Bodies[0];
        Assert.Contains("\"APIkey\":\"test-key\"", sent);
        Assert.Contains("\"commanderName\":\"Jameson\"", sent);
        Assert.Contains("\"isBeingDeveloped\":true", sent);
        Assert.Contains("\"eventName\":\"setCommanderCredits\"", sent);
        Assert.Contains("\"commanderCredits\":1000000", sent);
        Assert.Contains("2020-01-01T00:00:00Z", sent);
    }

    [Fact]
    public async Task Parses_hard_error_from_bad_api_key()
    {
        var handler = new RecordingHandler(body: """{ "header": { "eventStatus": 400, "eventStatusText": "Invalid API key" } }""");
        using var client = new InaraClient(Options, new HttpClient(handler));

        var response = await client.SendAsync(Identity, new[] { InaraEvent.SetCommanderCredits(DateTimeOffset.UtcNow, 1) });

        Assert.False(response.IsOk);
        Assert.True(response.IsHardError);
        Assert.Equal("Invalid API key", response.StatusText);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task Only_slow_down_and_server_errors_are_transient(HttpStatusCode status, bool transient)
    {
        var handler = new RecordingHandler(status: status, body: "");
        using var client = new InaraClient(Options, new HttpClient(handler));

        var response = await client.SendAsync(Identity, new[] { InaraEvent.SetCommanderCredits(DateTimeOffset.UtcNow, 1) });

        Assert.False(response.IsOk);
        Assert.Equal(transient, response.IsTransient);
    }

    [Fact]
    public async Task Empty_batch_is_a_no_op()
    {
        var handler = new RecordingHandler();
        using var client = new InaraClient(Options, new HttpClient(handler));

        var response = await client.SendAsync(Identity, Array.Empty<InaraEvent>());

        Assert.True(response.IsOk);
        Assert.Equal(0, handler.CallCount);   // never hit the wire
    }

    [Fact]
    public void Rank_factory_emits_expected_shape()
    {
        var ev = InaraEvent.SetCommanderRankPilot(DateTimeOffset.UtcNow, new[] { ("combat", 5, 0.3) });
        var list = Assert.IsAssignableFrom<System.Collections.IEnumerable>(ev.EventData);
        Assert.Equal("setCommanderRankPilot", ev.EventName);
        Assert.NotNull(list);
    }
}
