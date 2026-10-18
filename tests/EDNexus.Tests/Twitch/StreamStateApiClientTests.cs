using System.Net;
using System.Text.Json;
using EDNexus.Core.State;
using EDNexus.Core.Twitch;
using Xunit;

namespace EDNexus.Tests.Twitch;

public class StreamStateApiClientTests
{
    private const string Endpoint = "https://ebs.example.com/api/update-state";

    private static StreamCardSnapshot Snapshot() =>
        StreamCardMapper.Map(new CommanderState { StarSystem = "Nervi" }, now: DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Publishes_the_snapshot_under_the_state_property_with_a_bearer_token()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new StreamStateApiClient(new HttpClient(handler));
        var result = await client.PublishAsync(Endpoint, "ebs-token", Snapshot());

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal(Endpoint, captured.RequestUri!.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("ebs-token", captured.Headers.Authorization.Parameter);

        // The EBS derives the channel from the token; the body carries only the state payload.
        using var parsed = JsonDocument.Parse(body!);
        Assert.True(parsed.RootElement.TryGetProperty("state", out var stateElement));
        Assert.Equal("Nervi", stateElement.GetProperty("loc").GetProperty("system").GetString());
        Assert.DoesNotContain("channelId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, StreamStatePublishStatus.Unauthorized)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, StreamStatePublishStatus.TooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, StreamStatePublishStatus.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, StreamStatePublishStatus.Failed)]
    public async Task Maps_each_rejection_the_ebs_can_return(HttpStatusCode status, StreamStatePublishStatus expected)
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status)));

        using var client = new StreamStateApiClient(new HttpClient(handler));
        var result = await client.PublishAsync(Endpoint, "ebs-token", Snapshot());

        Assert.Equal(expected, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Equal(expected == StreamStatePublishStatus.Unauthorized, result.RequiresReauth);
    }

    [Fact]
    public async Task An_unreachable_ebs_is_reported_not_thrown()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no route to host"));

        using var client = new StreamStateApiClient(new HttpClient(handler));
        var result = await client.PublishAsync(Endpoint, "ebs-token", Snapshot());

        Assert.Equal(StreamStatePublishStatus.Failed, result.Status);
        Assert.Contains("no route to host", result.Error);
    }

    [Fact]
    public async Task Twitch_refusing_the_backends_credentials_is_told_apart_from_an_ordinary_failure()
    {
        // The EBS marks it with a header (a proxy can rewrite a 502's body) on a 503.
        var refused = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.Add(StreamStateApiClient.PubSubRejectedHeader, "credentials");
            return Task.FromResult(response);
        });
        using (var client = new StreamStateApiClient(new HttpClient(refused)))
        {
            var result = await client.PublishAsync(Endpoint, "ebs-token", Snapshot());

            Assert.Equal(StreamStatePublishStatus.BackendRejectedByTwitch, result.Status);
            Assert.False(result.IsSuccess);
            // Not the commander's sign-in: re-login is the wrong advice and must not stop publishing.
            Assert.False(result.RequiresReauth);
        }

        // The same status with no marker is just a failure, as before.
        var plain = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var plainClient = new StreamStateApiClient(new HttpClient(plain));
        Assert.Equal(StreamStatePublishStatus.Failed, (await plainClient.PublishAsync(Endpoint, "ebs-token", Snapshot())).Status);
    }

    [Fact]
    public void The_header_names_match_what_the_EBS_sends()
    {
        // Mirrored constants: the two projects cannot reference each other, so a typo would silently
        // turn every credential problem back into an anonymous failure.
        Assert.Equal("X-EDNexus-Pubsub-Rejected", StreamStateApiClient.PubSubRejectedHeader);
    }

    [Fact]
    public async Task A_clear_the_EBS_confirms_removed_is_told_apart_from_a_proxy_502()
    {
        // The EBS removed the snapshot; only the offline broadcast failed.
        var confirmed = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadGateway);
            response.Headers.Add(StreamStateApiClient.SnapshotRemovedHeader, "true");
            return Task.FromResult(response);
        });
        using (var client = new StreamStateApiClient(new HttpClient(confirmed)))
            Assert.Equal(StreamStatePublishStatus.ClearedNotDelivered, (await client.ClearAsync(Endpoint, "ebs-token")).Status);

        // A proxy in front of an unreachable EBS: nothing says the card is gone.
        var proxy = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        using (var client = new StreamStateApiClient(new HttpClient(proxy)))
            Assert.Equal(StreamStatePublishStatus.Failed, (await client.ClearAsync(Endpoint, "ebs-token")).Status);
    }

    [Fact]
    public async Task Cancellation_still_surfaces_to_the_caller()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new StreamStateApiClient(new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.PublishAsync(Endpoint, "ebs-token", Snapshot(), cts.Token));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
            => _respond = (request, _) => respond(request);

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => _respond(request, ct);
    }
}
