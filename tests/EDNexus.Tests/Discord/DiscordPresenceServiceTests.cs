using EDNexus.Core.Discord;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.Discord;

public class DiscordPresenceServiceTests
{
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(15);
        }
    }

    [Fact]
    public void Construction_attempts_to_initialize_the_client()
    {
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(new CommanderState(), client, TimeSpan.FromSeconds(15));

        Assert.Equal(1, client.InitializeCalls);
    }

    [Fact]
    public void Construction_immediately_pushes_whatever_state_is_already_known()
    {
        var state = new CommanderState { StarSystem = "Sol" };
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(state, client, TimeSpan.FromSeconds(15));

        var sent = Assert.Single(client.Sent);
        Assert.Equal("Exploring Sol", sent.State);
    }

    [Fact]
    public void A_relevant_change_outside_the_throttle_window_sends_immediately()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var state = new CommanderState();
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(state, client, TimeSpan.FromSeconds(15), clock: () => now);

        now = now.AddMinutes(1);   // past the window opened by the constructor's initial send
        state.StarSystem = "Colonia";

        var last = Assert.Single(client.Sent, p => p.State == "Exploring Colonia");
        Assert.Equal("Exploring Colonia", last.State);
    }

    [Fact]
    public void An_irrelevant_property_change_never_triggers_a_send()
    {
        var state = new CommanderState();
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(state, client, TimeSpan.FromSeconds(15));

        var countAfterConstruction = client.Sent.Count;
        state.Balance = 999;   // not part of the presence mapping

        Assert.Equal(countAfterConstruction, client.Sent.Count);
    }

    [Fact]
    public void Suppressed_service_never_sends_even_on_relevant_changes()
    {
        var state = new CommanderState();
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(
            state, client, TimeSpan.FromMilliseconds(1), isSuppressed: () => true);

        state.StarSystem = "Colonia";

        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task Rapid_changes_inside_the_throttle_window_coalesce_into_one_trailing_send()
    {
        var state = new CommanderState();
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(state, client, TimeSpan.FromMilliseconds(200));

        // The constructor's own push consumed the throttle slot; these three land inside the window
        // and must not each produce their own SetPresence call.
        state.StarSystem = "Sol";
        state.StarSystem = "Alpha Centauri";
        state.StarSystem = "Colonia";

        var sentDuringWindow = client.Sent.Count;
        Assert.True(sentDuringWindow <= 1, "no send should have escaped the throttle window yet");

        // Once the window reopens, the coalesced update carries the *latest* state, not an
        // intermediate one.
        await WaitForAsync(() => client.Sent.Any(p => p.State == "Exploring Colonia"));
        Assert.DoesNotContain(client.Sent, p => p.State == "Exploring Alpha Centauri");
    }

    [Fact]
    public void Repeated_identical_state_does_not_produce_duplicate_sends()
    {
        var state = new CommanderState { Ship = "Anaconda" };
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(state, client, TimeSpan.FromMilliseconds(1));

        var countAfterConstruction = client.Sent.Count;

        // Re-set the same ship name — no actual change, so the mapped payload is identical.
        state.Ship = "Anaconda";

        Assert.Equal(countAfterConstruction, client.Sent.Count);
    }

    [Fact]
    public void Dispose_clears_presence_and_releases_the_client()
    {
        var client = new FakeDiscordRpcClient();
        var service = new DiscordPresenceService(new CommanderState(), client, TimeSpan.FromSeconds(15));

        service.Dispose();

        Assert.Equal(1, client.ClearCalls);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public void A_failed_client_initialization_never_throws()
    {
        var client = new FakeDiscordRpcClient { InitializeResult = false };

        var exception = Record.Exception(() =>
        {
            using var service = new DiscordPresenceService(new CommanderState(), client, TimeSpan.FromSeconds(15));
        });

        Assert.Null(exception);
    }
}
