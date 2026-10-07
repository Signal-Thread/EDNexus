using EDNexus.Core.State;
using EDNexus.Core.Twitch;
using Xunit;

namespace EDNexus.Tests.Twitch;

public class TwitchStreamCardServiceTests
{
    private const string Endpoint = "https://ebs.example.com/api/update-state";

    /// <summary>
    /// Short enough that a test never sits on the real five-second floor, long enough that a burst
    /// of changes still lands inside one window on a loaded CI machine.
    /// </summary>
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(50);

    private static TwitchStreamCardService Create(
        CommanderState state,
        FakeStreamStateApiClient client,
        Func<string?>? token = null,
        Func<StreamCardVisibility>? visibility = null,
        Func<bool>? isSuppressed = null) =>
        new(
            state,
            StreamCardSources.Empty,
            client,
            static () => Endpoint,
            token ?? (static () => "ebs-token"),
            visibility,
            isSuppressed,
            FastInterval,
            clock: null);

    [Fact]
    public async Task Nothing_is_published_until_the_host_has_replayed_the_journal()
    {
        var client = new FakeStreamStateApiClient();

        // Built inside EngineHost's constructor, before the replay. Publishing here would send an
        // empty card and leave that as the EBS's initial state until the warm one follows.
        using var service = Create(new CommanderState(), client);

        Assert.False(await client.WaitForPublishAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Empty(client.Snapshots);
    }

    [Fact]
    public async Task Publishes_the_warmed_picture_when_the_host_asks()
    {
        var state = new CommanderState { StarSystem = "Nervi", Ship = "Krait Phantom" };
        var client = new FakeStreamStateApiClient();

        using var service = Create(state, client);
        service.RequestPublish();

        Assert.True(await client.WaitForPublishAsync());
        Assert.Equal("Nervi", client.Snapshots[0].Location!.System);
        Assert.Equal("ebs-token", client.LastToken);
        Assert.Equal(Endpoint, client.LastEndpoint);
    }

    [Fact]
    public async Task Nothing_is_published_until_the_commander_has_logged_in()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();

        using var service = Create(state, client, token: static () => null);

        Assert.False(await client.WaitForPublishAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Empty(client.Snapshots);
    }

    [Fact]
    public async Task Nothing_is_published_while_suppressed()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();

        // Developer mode feeds fabricated journal events through the real bus; they must never reach
        // a live audience.
        using var service = Create(state, client, isSuppressed: static () => true);

        Assert.False(await client.WaitForPublishAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Empty(client.Snapshots);
    }

    [Fact]
    public async Task A_change_that_viewers_cannot_see_does_not_spend_a_publish()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();

        using var service = Create(state, client);
        service.RequestPublish();
        Assert.True(await client.WaitForPublishAsync());

        // LastUpdated ticks on virtually every journal line but changes nothing on the card.
        for (var i = 0; i < 5; i++)
        {
            state.LastUpdated = DateTimeOffset.UtcNow;
            await Task.Delay(FastInterval);
        }

        Assert.Single(client.Snapshots);
    }

    [Fact]
    public async Task A_real_change_is_published()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();

        using var service = Create(state, client);
        service.RequestPublish();
        Assert.True(await client.WaitForPublishAsync());

        state.StarSystem = "Colonia";

        Assert.True(await client.WaitForPublishAsync());
        Assert.Equal("Colonia", client.Snapshots[^1].Location!.System);
    }

    [Fact]
    public async Task A_burst_of_changes_is_coalesced_into_the_newest_state()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();

        using var service = Create(state, client);
        service.RequestPublish();
        Assert.True(await client.WaitForPublishAsync());

        // A single hyperspace jump touches several properties at once; viewers only need the result.
        state.StarSystem = "Colonia";
        state.Body = "Colonia 2 a";
        state.FuelMain = 18.2;
        state.CargoTons = 24;

        Assert.True(await client.WaitForPublishAsync());
        await Task.Delay(FastInterval * 4);

        Assert.True(client.Snapshots.Count < 5, $"Expected a coalesced burst, saw {client.Snapshots.Count} publishes.");
        Assert.Equal("Colonia", client.Snapshots[^1].Location!.System);
    }

    [Fact]
    public async Task A_rejected_token_stops_publishing_and_asks_for_a_new_login()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient
        {
            Respond = _ => new StreamStatePublishResult(StreamStatePublishStatus.Unauthorized, "revoked"),
        };

        using var service = Create(state, client);
        service.RequestPublish();
        var reauth = new TaskCompletionSource();
        service.ReauthRequired += () => reauth.TrySetResult();

        Assert.True(await client.WaitForPublishAsync());
        await reauth.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.StoppedForReauth);

        var attempts = client.Snapshots.Count;
        state.StarSystem = "Colonia";
        await Task.Delay(FastInterval * 4);

        // Retrying a revoked grant can only ever earn a rate-limit, so publishing stays stopped.
        Assert.Equal(attempts, client.Snapshots.Count);
    }

    [Fact]
    public async Task Signing_in_again_resumes_publishing_after_a_rejected_token()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient
        {
            Respond = _ => new StreamStatePublishResult(StreamStatePublishStatus.Unauthorized, "revoked"),
        };
        var token = "stale-token";

        using var service = Create(state, client, token: () => token);
        service.RequestPublish();
        // The fake releases its signal before returning the 401, and the pump sets the latch only
        // after that call returns — so waiting on the publish alone races the write.
        var reauth = new TaskCompletionSource();
        service.ReauthRequired += () => reauth.TrySetResult();
        Assert.True(await client.WaitForPublishAsync());
        await reauth.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.StoppedForReauth);

        // The commander logs in again: a different credential is what tells the service the
        // situation has changed, so it never depends on the UI remembering to poke it.
        client.Respond = _ => StreamStatePublishResult.Ok;
        token = "fresh-token";
        state.StarSystem = "Colonia";

        Assert.True(await client.WaitForPublishAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("fresh-token", client.LastToken);
        Assert.False(service.StoppedForReauth);
    }

    [Fact]
    public async Task Switching_the_card_on_publishes_without_waiting_for_a_journal_event()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();
        var enabled = false;

        // How the app wires it: the card being off reads through as "no token".
        using var service = Create(state, client, token: () => enabled ? "ebs-token" : null);
        Assert.False(await client.WaitForPublishAsync(TimeSpan.FromMilliseconds(300)));

        // The commander ticks "Show my session to viewers". Nothing about the commander picture has
        // changed, and with the game closed no journal event is ever coming — so the settings dialog
        // has to ask for the publish itself.
        enabled = true;
        service.RequestPublish();

        Assert.True(await client.WaitForPublishAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Nervi", client.Snapshots[^1].Location!.System);
    }

    [Fact]
    public async Task Requesting_a_publish_that_changes_nothing_is_still_deduplicated()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();

        using var service = Create(state, client);
        service.RequestPublish();
        Assert.True(await client.WaitForPublishAsync());

        // Saving the settings dialog repeatedly must not spend the PubSub quota re-sending an
        // identical card.
        for (var i = 0; i < 3; i++)
        {
            service.RequestPublish();
            await Task.Delay(FastInterval * 2);
        }

        Assert.Single(client.Snapshots);
    }

    [Fact]
    public async Task The_endpoint_is_read_afresh_on_every_publish()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();
        var endpoint = "https://first.example.com/api/update-state";

        using var service = new TwitchStreamCardService(
            state, StreamCardSources.Empty, client, () => endpoint, static () => "ebs-token",
            null, null, FastInterval, clock: null);
        service.RequestPublish();

        Assert.True(await client.WaitForPublishAsync());
        Assert.Equal("https://first.example.com/api/update-state", client.LastEndpoint);

        // Pointing the app at a different EBS must not need a restart.
        endpoint = "https://second.example.com/api/update-state";
        state.StarSystem = "Colonia";

        Assert.True(await client.WaitForPublishAsync());
        Assert.Equal("https://second.example.com/api/update-state", client.LastEndpoint);
    }

    [Fact]
    public async Task Turning_the_card_off_stops_publishing_without_rebuilding_the_service()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();
        // How EngineHost wires it: "card switched off" reads through as "no token".
        var enabled = true;

        using var service = Create(state, client, token: () => enabled ? "ebs-token" : null);
        service.RequestPublish();
        Assert.True(await client.WaitForPublishAsync());

        enabled = false;
        var attempts = client.Snapshots.Count;
        state.StarSystem = "Colonia";
        await Task.Delay(FastInterval * 4);

        Assert.Equal(attempts, client.Snapshots.Count);
    }

    [Fact]
    public async Task An_unchanged_card_is_refreshed_so_the_EBS_does_not_expire_it()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();

        using var service = new TwitchStreamCardService(
            state, StreamCardSources.Empty, client, static () => Endpoint, static () => "ebs-token",
            null, null, FastInterval, clock: null, refreshInterval: TimeSpan.FromMilliseconds(200));
        service.RequestPublish();
        Assert.True(await client.WaitForPublishAsync());

        // No journal event, no settings change: the same card goes out again anyway.
        Assert.True(await client.WaitForPublishAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(client.Snapshots[0].ContentFingerprint(), client.Snapshots[1].ContentFingerprint());
    }

    [Fact]
    public async Task The_refresh_does_not_publish_before_the_host_has_replayed_the_journal()
    {
        var client = new FakeStreamStateApiClient();

        using var service = new TwitchStreamCardService(
            new CommanderState(), StreamCardSources.Empty, client, static () => Endpoint, static () => "ebs-token",
            null, null, FastInterval, clock: null, refreshInterval: TimeSpan.FromMilliseconds(50));

        Assert.False(await client.WaitForPublishAsync(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task Taking_the_card_off_the_air_clears_it_with_the_token_it_was_published_with()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();
        var enabled = true;

        using var service = Create(state, client, token: () => enabled ? "ebs-token" : null);
        service.RequestPublish();
        Assert.True(await client.WaitForPublishAsync());

        // Switched off: the token callback already reads null, so the caller supplies the old one.
        enabled = false;
        var result = await service.TakeOffAirAsync(Endpoint, "ebs-token");

        Assert.Equal(StreamStatePublishStatus.Cleared, result.Status);
        Assert.Equal(new[] { "ebs-token" }, client.Clears.ToArray());
        Assert.Equal(Endpoint, client.LastEndpoint);
    }

    [Fact]
    public async Task Switching_back_on_after_a_clear_republishes_an_unchanged_card()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();
        var enabled = true;

        using var service = Create(state, client, token: () => enabled ? "ebs-token" : null);
        service.RequestPublish();
        Assert.True(await client.WaitForPublishAsync());

        enabled = false;
        await service.TakeOffAirAsync(Endpoint, "ebs-token");

        // Nothing about the commander changed, but the EBS no longer holds the card: the
        // deduplication key must not suppress this publish.
        enabled = true;
        service.RequestPublish();

        Assert.True(await client.WaitForPublishAsync());
        Assert.Equal(2, client.Snapshots.Count);
    }

    [Fact]
    public async Task Disposing_during_a_slow_publish_neither_faults_nor_reports_afterwards()
    {
        var client = new BlockingStreamStateApiClient();
        var reported = new List<StreamStatePublishResult>();

        var service = new TwitchStreamCardService(
            new CommanderState { StarSystem = "Nervi" }, StreamCardSources.Empty, client,
            static () => Endpoint, static () => "ebs-token", null, null, FastInterval, clock: null);
        service.PublishCompleted += r => { lock (reported) reported.Add(r); };
        service.RequestPublish();
        Assert.True(await client.Entered.WaitAsync(TimeSpan.FromSeconds(5)));

        // Dispose gives up waiting after two seconds with the EBS still not answering.
        service.Dispose();
        client.Release();

        // The pump then finishes on its own: no ObjectDisposedException from the semaphores it still
        // held, and nothing reported to a UI that has already let go of the service.
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.Completion.IsFaulted);
        lock (reported) Assert.Empty(reported);
    }

    [Fact]
    public async Task Dispose_waits_for_a_handler_already_running()
    {
        var service = Create(new CommanderState { StarSystem = "Nervi" }, new FakeStreamStateApiClient());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.PublishCompleted += _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        service.RequestPublish();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        // Once Dispose returns, the caller may tear down whatever the handler touches, so it must not
        // return while one is still running — including past the two seconds it gives the pump.
        var disposing = Task.Run(service.Dispose);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.False(disposing.IsCompleted);

        release.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Holds every publish until the test releases it, like an EBS that is slow to answer.</summary>
    private sealed class BlockingStreamStateApiClient : IStreamStateApiClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SemaphoreSlim Entered { get; } = new(0);

        public void Release() => _release.TrySetResult();

        public async Task<StreamStatePublishResult> PublishAsync(
            string updateStateEndpoint, string token, StreamCardSnapshot snapshot, CancellationToken ct = default)
        {
            Entered.Release();
            // Ignores ct on purpose: a request already on the wire does not stop because we stopped caring.
            await _release.Task.ConfigureAwait(false);
            return StreamStatePublishResult.Ok;
        }

        public Task<StreamStatePublishResult> ClearAsync(string updateStateEndpoint, string token, CancellationToken ct = default) =>
            Task.FromResult(StreamStatePublishResult.ClearedOk);
    }

    [Fact]
    public void Preview_can_map_against_a_visibility_the_commander_has_not_saved_yet()
    {
        var state = new CommanderState { StarSystem = "Nervi", Ship = "Krait Phantom" };
        var client = new FakeStreamStateApiClient();

        using var service = Create(state, client, token: static () => null);

        // What the settings dialog does while the commander is still ticking boxes.
        var preview = service.Preview(StreamCardVisibility.Default with { Ship = false });

        Assert.Null(preview.Ship);
        Assert.NotNull(service.Preview().Ship);
    }

    [Fact]
    public async Task A_failed_publish_is_retried_without_waiting_for_a_change()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var attempts = 0;
        var client = new FakeStreamStateApiClient();
        // The EBS was not listening yet when the app started — the exact race between launching the
        // service and launching the app. Nothing about the commander will change afterwards if the
        // game is closed, so the pump has to come back by itself.
        client.Respond = _ => ++attempts < 3
            ? new StreamStatePublishResult(StreamStatePublishStatus.Failed, "connection refused")
            : StreamStatePublishResult.Ok;

        using var service = Create(state, client);
        service.RequestPublish();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !client.Snapshots.Any())
            await client.WaitForPublishAsync(TimeSpan.FromSeconds(1));

        // Give the retries room to reach the successful attempt.
        var success = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < success && attempts < 3)
            await Task.Delay(FastInterval * 2);

        Assert.True(attempts >= 3, $"Expected the pump to retry unprompted; it stopped after {attempts} attempt(s).");
    }

    [Fact]
    public async Task Retries_are_bounded_so_a_dead_ebs_is_not_polled_forever()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient
        {
            Respond = _ => new StreamStatePublishResult(StreamStatePublishStatus.Failed, "connection refused"),
        };

        using var service = Create(state, client);
        service.RequestPublish();

        await Task.Delay(FastInterval * (TwitchStreamCardService.MaxPublishRetries + 6) * 2);

        // One initial attempt plus at most MaxPublishRetries, then the pump sleeps until something
        // really changes.
        Assert.InRange(client.Snapshots.Count, 1, TwitchStreamCardService.MaxPublishRetries + 1);
    }

    [Fact]
    public async Task A_failed_publish_is_retried_on_the_next_change()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient
        {
            Respond = _ => new StreamStatePublishResult(StreamStatePublishStatus.Failed, "unreachable"),
        };

        using var service = Create(state, client);
        service.RequestPublish();
        Assert.True(await client.WaitForPublishAsync());

        // The fingerprint is only remembered on success, so the same card is offered again.
        client.Respond = _ => StreamStatePublishResult.Ok;
        state.StarSystem = "Colonia";

        Assert.True(await client.WaitForPublishAsync(TimeSpan.FromSeconds(5)));
        Assert.True(client.Snapshots.Count >= 2);
    }

    [Fact]
    public async Task Hiding_a_section_takes_effect_without_rebuilding_the_service()
    {
        var state = new CommanderState { StarSystem = "Nervi", Ship = "Krait Phantom" };
        var client = new FakeStreamStateApiClient();
        var visibility = StreamCardVisibility.Default;

        using var service = Create(state, client, visibility: () => visibility);
        service.RequestPublish();
        Assert.True(await client.WaitForPublishAsync());
        Assert.NotNull(client.Snapshots[0].Ship);

        visibility = StreamCardVisibility.Default with { Ship = false };
        state.StarSystem = "Colonia";

        Assert.True(await client.WaitForPublishAsync());
        Assert.Null(client.Snapshots[^1].Ship);
    }

    [Fact]
    public async Task Preview_reports_what_would_be_published_without_sending_it()
    {
        var state = new CommanderState { StarSystem = "Nervi" };
        var client = new FakeStreamStateApiClient();

        using var service = Create(state, client, token: static () => null);

        var preview = service.Preview();

        Assert.Equal("Nervi", preview.Location!.System);
        Assert.Empty(client.Snapshots);
        Assert.False(await client.WaitForPublishAsync(TimeSpan.FromMilliseconds(200)));
    }
}
