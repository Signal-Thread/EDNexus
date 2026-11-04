using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EliteDangerous.RavenColonial;
using Xunit;

namespace EDNexus.Tests.Colonisation;

/// <summary>
/// The fleet-carrier half of the Raven Colonial client. Every request goes to a fake handler: nothing
/// here may ever reach the live service, and the API key used is a made-up one.
/// </summary>
public class RavenCarrierClientTests
{
    private const string Key = "test-key-0000";

    private sealed record Seen(HttpMethod Method, Uri? Uri, string Body, Dictionary<string, string> Headers);

    private sealed class ScriptedHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public List<Seen> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            lock (Requests) Requests.Add(new Seen(request.Method, request.RequestUri, body, headers));
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

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (RavenColonialClient client, ScriptedHandler handler) NewClient(Func<int, HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new ScriptedHandler(respond ?? ((_, _) => Json(HttpStatusCode.OK, "{}")));
        return (new RavenColonialClient(Options, new HttpClient(handler)), handler);
    }

    private static Dictionary<string, int> Deltas(params (string name, int units)[] items)
        => items.ToDictionary(i => i.name, i => i.units);

    // --- Raven's commodity spelling ---

    [Theory]
    [InlineData("steel", "steel")]
    [InlineData("Steel", "steel")]
    [InlineData("$Aluminium_name;", "aluminium")]
    [InlineData("$cmmcomposite_name;", "cmmcomposite")]
    [InlineData("  Titanium ", "titanium")]
    [InlineData("liquidoxygen_name", "liquidoxygen")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Commodities_are_spelled_lower_case_without_the_journal_wrapper(string? raw, string expected)
        => Assert.Equal(expected, RavenCommodityKey.Normalize(raw));

    // --- Reading a carrier ---

    [Fact]
    public async Task A_carrier_is_read_from_the_fc_route_and_its_cargo_keys_are_normalised()
    {
        var (client, handler) = NewClient((_, _) => Json(HttpStatusCode.OK, """
        { "marketId": "3711552256", "name": "X8J-LVZ", "displayName": "CORTANA'S REQUIEM", "owner": "Jane Doe",
          "systemName": "Sol", "cargo": { "Steel": 1276, "titanium": "32", "Zero": 0 } }
        """));

        var result = await client.GetCarrierAsync(3711552256);

        Assert.True(result.IsOk);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://raven.test/api/fc/3711552256", request.Uri!.AbsoluteUri);
        var carrier = result.Value!;
        Assert.Equal(3711552256, carrier.MarketId);
        Assert.Equal("CORTANA'S REQUIEM", carrier.DisplayName);
        Assert.Equal("Jane Doe", carrier.Owner);
        Assert.Equal(1276, carrier.Cargo["steel"]);
        Assert.Equal(32, carrier.Cargo["titanium"]);      // integer-or-string, like the rest of the API
    }

    [Fact]
    public async Task An_unknown_carrier_is_ok_with_no_value_not_a_failure()
    {
        var (client, _) = NewClient((_, _) => Json(HttpStatusCode.NotFound, ""));

        var result = await client.GetCarrierAsync(1234);

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_carrier_lookup_with_no_market_id_never_touches_the_network()
    {
        var (client, handler) = NewClient();

        var result = await client.GetCarrierAsync(0);

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
        Assert.Empty(handler.Requests);
    }

    // --- Writing cargo ---

    [Fact]
    public async Task Cargo_is_patched_as_signed_deltas_with_the_key_in_the_rcc_key_header()
    {
        var (client, handler) = NewClient((_, _) => Json(HttpStatusCode.OK, """{ "steel": 1376, "titanium": 5 }"""));

        var result = await client.PatchCarrierCargoAsync(3711552256, Key, Deltas(("steel", 100), ("titanium", -32)));

        Assert.True(result.IsOk);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal("https://raven.test/api/fc/3711552256/cargo", sent.Uri!.AbsoluteUri);
        Assert.Equal(Key, sent.Headers["rcc-key"]);

        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(100, body.RootElement.GetProperty("steel").GetInt32());
        Assert.Equal(-32, body.RootElement.GetProperty("titanium").GetInt32());

        // The receipt carries what the server now holds, which is what drift detection compares against.
        Assert.Equal(1376, result.Value!.Totals["steel"]);
        Assert.Equal(5, result.Value.Totals["titanium"]);
        Assert.Equal(100, result.Value.Sent["steel"]);
    }

    [Fact]
    public async Task Names_are_normalised_merged_and_zeroed_commodities_dropped()
    {
        var (client, handler) = NewClient();

        var result = await client.PatchCarrierCargoAsync(1, Key,
            Deltas(("Steel", 40), ("$steel_name;", 60), ("Gold", 0), ("  ", 5)));

        Assert.True(result.IsOk);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(100, body.RootElement.GetProperty("steel").GetInt32());
        Assert.Single(body.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task A_delta_that_cancels_out_is_not_sent()
    {
        var (client, handler) = NewClient();

        var result = await client.PatchCarrierCargoAsync(1, Key, Deltas(("steel", 5), ("$steel_name;", -5)));

        Assert.False(result.IsOk);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_empty_or_unparseable_reply_is_still_a_successful_write_with_no_totals()
    {
        var (client, _) = NewClient((_, _) => Json(HttpStatusCode.OK, ""));

        var result = await client.PatchCarrierCargoAsync(1, Key, Deltas(("steel", 5)));

        Assert.True(result.IsOk);
        Assert.Empty(result.Value!.Totals);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\r\nkey")]
    [InlineData("kéy")]
    public async Task A_missing_or_header_unsafe_key_fails_without_touching_the_network(string key)
    {
        var (client, handler) = NewClient();

        var result = await client.PatchCarrierCargoAsync(1, key, Deltas(("steel", 5)));

        Assert.False(result.IsOk);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_bad_market_id_fails_without_touching_the_network()
    {
        var (client, handler) = NewClient();

        Assert.False((await client.PatchCarrierCargoAsync(0, Key, Deltas(("steel", 5)))).IsOk);
        Assert.False((await client.PatchCarrierCargoAsync(-4, Key, Deltas(("steel", 5)))).IsOk);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_key_is_reported_with_its_status_and_never_echoes_the_key(HttpStatusCode status)
    {
        var (client, _) = NewClient((_, _) => Json(status, $"denied for {Key}"));

        var result = await client.PatchCarrierCargoAsync(1, Key, Deltas(("steel", 5)));

        Assert.False(result.IsOk);
        Assert.Equal((int)status, result.StatusCode);
        Assert.DoesNotContain(Key, result.Error!);
    }

    [Fact]
    public async Task A_transport_failure_is_a_failure_that_never_echoes_the_key()
    {
        var (client, _) = NewClient((_, _) => throw new HttpRequestException($"boom {Key}"));

        var result = await client.PatchCarrierCargoAsync(1, Key, Deltas(("steel", 5)));

        Assert.False(result.IsOk);
        Assert.DoesNotContain(Key, result.Error!);
    }

    [Fact]
    public async Task A_busy_server_is_retried_once_with_the_key_and_body_intact()
    {
        var (client, handler) = NewClient((n, _) => n == 1
            ? Json(HttpStatusCode.ServiceUnavailable, "")
            : Json(HttpStatusCode.OK, """{ "steel": 5 }"""));

        var result = await client.PatchCarrierCargoAsync(1, Key, Deltas(("steel", 5)));

        Assert.True(result.IsOk);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(Key, r.Headers["rcc-key"]);
            Assert.Contains("\"steel\":5", r.Body);
        });
    }
}
