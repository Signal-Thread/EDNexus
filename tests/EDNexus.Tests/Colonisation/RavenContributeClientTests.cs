using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EliteDangerous.RavenColonial;
using Xunit;

namespace EDNexus.Tests.Colonisation;

/// <summary>
/// The write half of the Raven Colonial client. Every request here goes to a fake handler:
/// nothing in these tests may ever reach the live service.
/// </summary>
public class RavenContributeClientTests
{
    private sealed record Seen(HttpMethod Method, Uri? Uri, string Body, string? ContentType, string HeaderNames);

    private sealed class ScriptedHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<Seen> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var names = request.Headers.Select(h => h.Key)
                .Concat(request.Content?.Headers.Select(h => h.Key) ?? Enumerable.Empty<string>());
            lock (Requests)
                Requests.Add(new Seen(request.Method, request.RequestUri, body,
                    request.Content?.Headers.ContentType?.MediaType, string.Join(",", names)));
            return respond(Interlocked.Increment(ref _calls), request);
        }
    }

    private static readonly RavenColonialClientOptions Options = new()
    {
        SoftwareName = "T",
        SoftwareVersion = "1",
        BaseUrl = "https://raven.test",
        TransientRetryDelay = TimeSpan.Zero,
        MaxRetryAfter = TimeSpan.FromSeconds(1),
    };

    private static HttpResponseMessage Reply(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent("") };
        if (retryAfter is { } wait) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        return r;
    }

    private static (RavenColonialClient client, ScriptedHandler handler) NewClient(
        Func<int, HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new ScriptedHandler(respond ?? ((_, _) => Reply(HttpStatusCode.OK)));
        return (new RavenColonialClient(Options, new HttpClient(handler)), handler);
    }

    private static Dictionary<string, int> Deltas(params (string name, int units)[] items)
        => items.ToDictionary(i => i.name, i => i.units);

    [Fact]
    public async Task A_contribution_is_a_post_of_a_commodity_to_units_object_with_no_credentials()
    {
        var (client, handler) = NewClient();

        var result = await client.ContributeAsync("3684c1a4", "Jane Doe", Deltas(("aluminium", 50), ("cmmcomposite", 120)));

        Assert.True(result.IsOk);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://raven.test/api/project/3684c1a4/contribute/Jane%20Doe", sent.Uri!.AbsoluteUri);
        Assert.Equal("application/json", sent.ContentType);

        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(JsonValueKind.Object, body.RootElement.ValueKind);
        Assert.Equal(50, body.RootElement.GetProperty("aluminium").GetInt32());
        Assert.Equal(120, body.RootElement.GetProperty("cmmcomposite").GetInt32());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());

        // Raven's contribute endpoint is unauthenticated; the client must not invent a credential.
        Assert.DoesNotContain("Authorization", sent.HeaderNames, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rcc-key", sent.HeaderNames, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "aluminium", "cmmcomposite" }, result.Value!.Sent.Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task The_commander_name_is_escaped_into_one_path_segment()
    {
        var (client, handler) = NewClient();

        await client.ContributeAsync("b1", "A/B?C#D", Deltas(("steel", 1)));

        Assert.Equal("https://raven.test/api/project/b1/contribute/A%2FB%3FC%23D", handler.Requests.Single().Uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("", "Jane")]
    [InlineData("  ", "Jane")]
    [InlineData("..", "Jane")]
    [InlineData(".", "Jane")]
    [InlineData("b1", "")]
    [InlineData("b1", "   ")]
    [InlineData("b1", "..")]
    public async Task A_blank_or_dot_segment_id_or_commander_never_reaches_the_network(string buildId, string cmdr)
    {
        var (client, handler) = NewClient();

        var result = await client.ContributeAsync(buildId, cmdr, Deltas(("steel", 1)));

        Assert.False(result.IsOk);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task An_empty_delivery_never_reaches_the_network()
    {
        var (client, handler) = NewClient();

        var result = await client.ContributeAsync("b1", "Jane", new Dictionary<string, int>());

        Assert.False(result.IsOk);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_delivery_of_only_zero_negative_or_unnamed_amounts_never_reaches_the_network()
    {
        var (client, handler) = NewClient();

        var result = await client.ContributeAsync("b1", "Jane", Deltas(("steel", 0), ("copper", -5), (" ", 10)));

        Assert.False(result.IsOk);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Non_positive_amounts_are_dropped_and_the_rest_is_sent()
    {
        var (client, handler) = NewClient();

        var result = await client.ContributeAsync("b1", "Jane", Deltas(("steel", 0), ("copper", -5), ("aluminium", 7)));

        Assert.True(result.IsOk);
        Assert.Equal("""{"aluminium":7}""", handler.Requests.Single().Body);
    }

    [Fact]
    public async Task A_rejected_contribution_is_a_failure_with_the_status_in_it()
    {
        var (client, handler) = NewClient((_, _) => Reply(HttpStatusCode.BadRequest));

        var result = await client.ContributeAsync("b1", "Jane", Deltas(("steel", 1)));

        Assert.False(result.IsOk);
        Assert.Contains("400", result.Error);
        Assert.Equal(1, handler.Calls);   // a client error is final, not retried
    }

    [Fact]
    public async Task A_network_fault_is_a_failure_not_an_exception()
    {
        var (client, _) = NewClient((_, _) => throw new HttpRequestException("no route to host"));

        var result = await client.ContributeAsync("b1", "Jane", Deltas(("steel", 1)));

        Assert.False(result.IsOk);
        Assert.Contains("no route", result.Error);
    }

    [Fact]
    public async Task A_busy_server_is_retried_once_and_resends_the_same_body()
    {
        var (client, handler) = NewClient((n, _) => n == 1
            ? Reply((HttpStatusCode)429, TimeSpan.FromMilliseconds(10))
            : Reply(HttpStatusCode.OK));

        var result = await client.ContributeAsync("b1", "Jane", Deltas(("steel", 3)));

        Assert.True(result.IsOk);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        Assert.Equal("""{"steel":3}""", handler.Requests[1].Body);
    }

    [Fact]
    public async Task A_server_that_stays_busy_fails_after_exactly_one_retry()
    {
        var (client, handler) = NewClient((_, _) => Reply(HttpStatusCode.ServiceUnavailable));

        var result = await client.ContributeAsync("b1", "Jane", Deltas(("steel", 3)));

        Assert.False(result.IsOk);
        Assert.Contains("503", result.Error);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_retry_after_beyond_the_bound_is_not_waited_for()
    {
        var (client, handler) = NewClient((_, _) => Reply((HttpStatusCode)429, TimeSpan.FromMinutes(10)));

        var result = await client.ContributeAsync("b1", "Jane", Deltas(("steel", 3)));

        Assert.False(result.IsOk);
        Assert.Contains("429", result.Error);
        Assert.Equal(1, handler.Calls);
    }
}
