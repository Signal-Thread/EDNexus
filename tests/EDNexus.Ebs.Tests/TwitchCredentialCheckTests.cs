using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EDNexus.Ebs.Options;
using EDNexus.Ebs.Security;
using EDNexus.Ebs.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EDNexus.Ebs.Tests;

/// <summary>
/// The EBS's answer to "does Twitch accept our PubSub credentials?": the startup check, the status
/// every real broadcast keeps, /healthz, and what a commander's publish gets back. A wrong
/// <c>Twitch__ExtensionSecret</c> used to be completely silent (the card was stored, /healthz said ok,
/// and nothing was ever delivered), which is exactly what these make loud.
/// </summary>
public class TwitchCredentialCheckTests
{
    private static TwitchEbsOptions Options(string? secret = EbsHostFactory.ExtensionSecret, string? owner = null) => new()
    {
        ClientId = "test-client-id",
        ExtensionId = "test-extension-id",
        ExtensionSecret = secret ?? "",
        OwnerUserId = owner,
    };

    private static ITwitchExtensionJwtService Jwt(TwitchEbsOptions options) => new TwitchExtensionJwtService(Microsoft.Extensions.Options.Options.Create(options));

    private static (TwitchCredentialProbe Probe, TwitchCredentialStatus Status, RecordingHandler Handler) Probe(
        HttpStatusCode code, string body = "{}", string? secret = EbsHostFactory.ExtensionSecret, string? owner = null)
    {
        var options = Options(secret, owner);
        var status = new TwitchCredentialStatus();
        var handler = new RecordingHandler(code, body);
        var probe = new TwitchCredentialProbe(new HttpClient(handler), Jwt(options), Microsoft.Extensions.Options.Options.Create(options), status, NullLogger<TwitchCredentialProbe>.Instance);
        return (probe, status, handler);
    }

    private static (TwitchPubSubClient Client, TwitchCredentialStatus Status) PubSub(HttpStatusCode code, TwitchCredentialStatus? status = null)
    {
        var options = Options();
        status ??= new TwitchCredentialStatus();
        var client = new TwitchPubSubClient(
            new HttpClient(new RecordingHandler(code, "{}")), Jwt(options),
            Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Options.Options.Create(new EbsOptions()),
            status, NullLogger<TwitchPubSubClient>.Instance);
        return (client, status);
    }

    private static JsonElement State() => JsonDocument.Parse("""{"v":1,"headline":"Docked"}""").RootElement.Clone();

    // --- the startup check's verdict -----------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]      // the one seen in production: "JWT could not be verified"
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_refused_token_is_a_rejection_and_is_recorded(HttpStatusCode code)
    {
        var (probe, status, _) = Probe(code, """{"error":"Forbidden","status":403,"message":"Error (403): JWT could not be verified"}""");

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal(TwitchCredentialProbeResult.Rejected, result);
        Assert.Equal(TwitchCredentialState.Rejected, status.State);
        Assert.Equal((int)code, status.Current.HttpStatus);
    }

    [Fact]
    public async Task A_400_means_the_credentials_got_through_and_only_the_bogus_target_was_refused()
    {
        var (probe, status, _) = Probe(HttpStatusCode.BadRequest, """{"error":"Bad Request","message":"Invalid target"}""");

        Assert.Equal(TwitchCredentialProbeResult.Accepted, await probe.CheckAsync(CancellationToken.None));
        Assert.Equal(TwitchCredentialState.Accepted, status.State);
    }

    [Fact]
    public async Task An_outright_success_is_still_not_a_refusal()
    {
        var (probe, status, _) = Probe(HttpStatusCode.NoContent);

        Assert.Equal(TwitchCredentialProbeResult.Accepted, await probe.CheckAsync(CancellationToken.None));
        Assert.Equal(TwitchCredentialState.Accepted, status.State);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_response_that_says_nothing_about_the_credentials_changes_nothing(HttpStatusCode code)
    {
        var (probe, status, _) = Probe(code);

        Assert.Equal(TwitchCredentialProbeResult.Inconclusive, await probe.CheckAsync(CancellationToken.None));
        Assert.Equal(TwitchCredentialState.Unknown, status.State);
    }

    [Fact]
    public async Task An_unreachable_Twitch_is_inconclusive_and_never_throws()
    {
        var options = Options();
        var status = new TwitchCredentialStatus();
        var probe = new TwitchCredentialProbe(new HttpClient(new ThrowingHandler()), Jwt(options), Microsoft.Extensions.Options.Options.Create(options), status, NullLogger<TwitchCredentialProbe>.Instance);

        Assert.Equal(TwitchCredentialProbeResult.Inconclusive, await probe.CheckAsync(CancellationToken.None));
        Assert.Equal(TwitchCredentialState.Unknown, status.State);
    }

    [Fact]
    public async Task A_secret_that_is_not_even_base64_is_inconclusive_and_never_throws()
    {
        var (probe, status, handler) = Probe(HttpStatusCode.Forbidden, secret: "this is not base64!");

        Assert.Equal(TwitchCredentialProbeResult.Inconclusive, await probe.CheckAsync(CancellationToken.None));
        Assert.Equal(TwitchCredentialState.Unknown, status.State);
        Assert.Equal(0, handler.CallCount);   // it could not sign, so it sent nothing
    }

    [Fact]
    public async Task The_check_cannot_reach_a_viewer_and_is_signed_like_a_real_broadcast()
    {
        var (probe, _, handler) = Probe(HttpStatusCode.BadRequest, owner: "424242");

        await probe.CheckAsync(CancellationToken.None);

        var sent = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
        // The point of the design: a target Twitch cannot deliver to, so nothing is ever broadcast.
        Assert.Equal(TwitchCredentialProbe.InvalidTarget, Assert.Single(sent.GetProperty("target").EnumerateArray()).GetString());
        Assert.DoesNotContain("\"broadcast\"", sent.GetProperty("target").GetRawText());
        Assert.Equal("424242", sent.GetProperty("broadcaster_id").GetString());
        Assert.Equal("test-extension-id", Assert.Single(handler.ClientIds));   // the same Client-Id a real publish sends
        Assert.Equal("https://api.twitch.tv/helix/extensions/pubsub", Assert.Single(handler.Uris)!.ToString());
    }

    // --- real broadcasts keep the status honest ------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_real_broadcast_refused_for_its_token_marks_the_credentials_rejected(HttpStatusCode code)
    {
        var (client, status) = PubSub(code);

        Assert.False(await client.BroadcastAsync("chan", State(), CancellationToken.None));

        Assert.Equal(TwitchCredentialState.Rejected, status.State);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]           // the payload, not the credentials
    [InlineData(HttpStatusCode.InternalServerError)]  // Twitch, not the credentials
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Other_failures_say_nothing_about_the_credentials(HttpStatusCode code)
    {
        var (client, status) = PubSub(code);

        Assert.False(await client.BroadcastAsync("chan", State(), CancellationToken.None));

        Assert.Equal(TwitchCredentialState.Unknown, status.State);
    }

    [Fact]
    public async Task A_real_broadcast_that_succeeds_clears_a_rejection_the_startup_check_recorded()
    {
        var status = new TwitchCredentialStatus();
        status.MarkRejected(403);
        var (client, _) = PubSub(HttpStatusCode.NoContent, status);

        Assert.True(await client.BroadcastAsync("chan", State(), CancellationToken.None));

        Assert.Equal(TwitchCredentialState.Accepted, status.State);
        Assert.Null(status.Current.HttpStatus);
    }

    [Fact]
    public async Task A_rejection_after_a_success_is_noticed_straight_away()
    {
        var status = new TwitchCredentialStatus();
        var (ok, _) = PubSub(HttpStatusCode.NoContent, status);
        await ok.BroadcastAsync("chan", State(), CancellationToken.None);
        var (refused, _) = PubSub(HttpStatusCode.Forbidden, status);

        await refused.BroadcastAsync("chan", State(), CancellationToken.None);

        Assert.Equal(TwitchCredentialState.Rejected, status.State);
    }

    // --- what the commander's app and the operator see -----------------------------------------------

    [Fact]
    public async Task A_publish_Twitch_refuses_for_the_EBS_credentials_is_a_503_with_the_marker_header()
    {
        using var factory = new EbsHostFactory();
        var (client, _) = factory.CreateBroadcasterClient("chan-rejected");
        factory.PubSub.RejectFor.Add("chan-rejected");
        factory.Services.GetRequiredService<TwitchCredentialStatus>().MarkRejected(403);

        var response = await client.PostAsJsonAsync("/api/update-state", new { state = new { v = 1, headline = "Docked at Home" } });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("credentials", Assert.Single(response.Headers.GetValues(TwitchCredentialStatus.RejectedHeader)));
        Assert.Contains("credentials", await response.Content.ReadAsStringAsync());
        // The card is still stored, so a viewer who opens the channel gets it: only live pushes are lost.
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/initial-state/chan-rejected")).StatusCode);
    }

    [Fact]
    public async Task An_ordinary_PubSub_failure_is_still_a_plain_502_without_the_marker()
    {
        using var factory = new EbsHostFactory();
        var (client, _) = factory.CreateBroadcasterClient("chan-flaky");
        factory.PubSub.RejectFor.Add("chan-flaky");   // fails, but Twitch never said the credentials were refused

        var response = await client.PostAsJsonAsync("/api/update-state", new { state = new { v = 1, headline = "Docked at Home" } });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(response.Headers.Contains(TwitchCredentialStatus.RejectedHeader));
    }

    [Fact]
    public async Task A_successful_publish_does_not_carry_the_marker()
    {
        using var factory = new EbsHostFactory();
        var (client, _) = factory.CreateBroadcasterClient("chan-fine");

        var response = await client.PostAsJsonAsync("/api/update-state", new { state = new { v = 1, headline = "Docked at Home" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(TwitchCredentialStatus.RejectedHeader));
    }

    [Fact]
    public async Task Healthz_stays_ok_but_reports_what_is_known_about_the_credentials()
    {
        using var factory = new EbsHostFactory();
        var http = factory.CreateClient();
        var status = factory.Services.GetRequiredService<TwitchCredentialStatus>();

        var unknown = await ReadHealth(http);
        Assert.Equal("ok", unknown.GetProperty("status").GetString());
        Assert.Equal("unknown", unknown.GetProperty("pubsub").GetString());

        status.MarkRejected(403);
        var rejected = await ReadHealth(http);
        // Still "ok": a wrong secret must not get the container restarted in a loop, which is what a
        // failing health check does and which would not fix it.
        Assert.Equal("ok", rejected.GetProperty("status").GetString());
        Assert.Equal("rejected", rejected.GetProperty("pubsub").GetString());
        Assert.Equal(403, rejected.GetProperty("pubsubStatus").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, rejected.GetProperty("pubsubCheckedAt").ValueKind);

        status.MarkAccepted();
        var accepted = await ReadHealth(http);
        Assert.Equal("accepted", accepted.GetProperty("pubsub").GetString());
        Assert.Equal(JsonValueKind.Null, accepted.GetProperty("pubsubStatus").ValueKind);
    }

    private static async Task<JsonElement> ReadHealth(HttpClient http)
    {
        var response = await http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // --- the background service ----------------------------------------------------------------------

    [Fact]
    public async Task One_run_asks_the_probe_once()
    {
        var probe = new CountingProbe();
        var service = new TwitchCredentialCheckBackgroundService(probe, new TestEnvironment("Production"), TimeProvider.System, NullLogger<TwitchCredentialCheckBackgroundService>.Instance);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task A_probe_that_throws_cannot_take_the_host_down()
    {
        var probe = new CountingProbe { Throw = true };
        var service = new TwitchCredentialCheckBackgroundService(probe, new TestEnvironment("Production"), TimeProvider.System, NullLogger<TwitchCredentialCheckBackgroundService>.Instance);

        await service.RunOnceAsync(CancellationToken.None);   // does not throw

        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task Under_the_test_host_it_never_reaches_out_at_all()
    {
        var probe = new CountingProbe();
        var service = new TwitchCredentialCheckBackgroundService(probe, new TestEnvironment("Testing"), TimeProvider.System, NullLogger<TwitchCredentialCheckBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Shutting_down_before_the_first_check_stops_it_cleanly_without_asking_Twitch()
    {
        var probe = new CountingProbe();
        var service = new TwitchCredentialCheckBackgroundService(probe, new TestEnvironment("Production"), TimeProvider.System, NullLogger<TwitchCredentialCheckBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, probe.Calls);   // the 10 s initial delay had not passed
    }

    // --- helpers -------------------------------------------------------------------------------------

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("network is down");
    }

    private sealed class CountingProbe : ITwitchCredentialProbe
    {
        public int Calls;
        public bool Throw;

        public Task<TwitchCredentialProbeResult> CheckAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("boom");
            return Task.FromResult(TwitchCredentialProbeResult.Inconclusive);
        }
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
