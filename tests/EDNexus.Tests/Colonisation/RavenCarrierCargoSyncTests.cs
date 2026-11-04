using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EDNexus.Core.Colonisation;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EliteDangerous.RavenColonial;
using Xunit;

namespace EDNexus.Tests.Colonisation;

/// <summary>
/// The opt-in sharing of the commander's own fleet carrier cargo. Everything runs against a fake
/// tracker or a fake HTTP handler: nothing here may ever reach Raven Colonial.
/// </summary>
public class RavenCarrierCargoSyncTests
{
    private const long Mine = 3711552256;
    private const long Other = 3700009999;

    private sealed record Patched(long MarketId, IReadOnlyDictionary<string, int> Deltas);

    /// <summary>An honest tracker: it holds cargo, applies what it is sent, and says what it now holds.</summary>
    private sealed class FakeCarrier : ISharedCarrierCargo
    {
        public SharedCarrierLookup Lookup { get; set; } = new(true, new Dictionary<string, int> { ["steel"] = 1000 });
        public Dictionary<string, int> Server { get; } = new() { ["steel"] = 1000 };
        public string? PatchError { get; set; }
        public bool KeyRejected { get; set; }
        public Exception? ThrowOnPatch { get; set; }
        public Action? OnFind { get; set; }
        public int FindCalls;
        public List<Patched> Patches { get; } = new();

        /// <summary>Another tool reporting the same movement: applied to the server on top of ours.</summary>
        public bool SecondReporter { get; set; }

        public Task<SharedCarrierLookup> FindAsync(long marketId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FindCalls);
            OnFind?.Invoke();
            return Task.FromResult(Lookup);
        }

        public Task<SharedCarrierWrite> PatchAsync(long marketId, IReadOnlyDictionary<string, int> deltas, CancellationToken ct = default)
        {
            if (ThrowOnPatch is not null) throw ThrowOnPatch;
            lock (Patches) Patches.Add(new Patched(marketId, new Dictionary<string, int>(deltas)));
            if (KeyRejected) return Task.FromResult(new SharedCarrierWrite(new Dictionary<string, int>(), "HTTP 401", KeyRejected: true));
            if (PatchError is not null) return Task.FromResult(new SharedCarrierWrite(new Dictionary<string, int>(), PatchError));

            foreach (var (name, units) in deltas)
                Server[name] = Math.Max(0, Server.GetValueOrDefault(name) + units * (SecondReporter ? 2 : 1));
            return Task.FromResult(new SharedCarrierWrite(Server.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value)));
        }
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Rig : IDisposable
    {
        public JournalEventBus Bus { get; } = new();
        public CommanderState State { get; } = new() { Name = "Jane Doe", CarrierId = Mine, MarketId = Mine };
        public FakeCarrier Carrier { get; } = new();
        public bool Enabled { get; set; } = true;
        public bool Suppressed { get; set; }
        public List<CarrierShareFault> Faults { get; } = new();
        public MovableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        public RavenCarrierCargoSync Sync { get; }
        public List<Exception> HandlerErrors { get; } = new();

        public Rig()
        {
            // A fault switches sharing off, as the app does, before the callback returns.
            Sync = new RavenCarrierCargoSync(Bus, State, Carrier, () => Enabled,
                fault => { Faults.Add(fault); Enabled = false; }, () => Suppressed, Clock);
            Bus.HandlerError += (_, ex) => HandlerErrors.Add(ex);
        }

        public async Task PublishAsync(string json, bool historical = false)
        {
            Assert.True(JournalEntry.TryParse(json, historical, out var entry), "sample JSON failed to parse");
            Bus.Publish(entry);
            await Sync.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Dispose() => Sync.Dispose();
    }

    private static string Transfer(string type = "steel", int count = 100, string direction = "tocarrier", string timestamp = "2026-11-02T04:05:52Z") => $$"""
    { "timestamp":"{{timestamp}}", "event":"CargoTransfer", "Transfers":[ { "Type":"{{type}}", "Count":{{count}}, "Direction":"{{direction}}" } ] }
    """;

    private static string Sell(long marketId = Mine, string type = "steel", int count = 100, string timestamp = "2026-11-02T04:10:00Z") => $$"""
    { "timestamp":"{{timestamp}}", "event":"MarketSell", "MarketID":{{marketId}}, "Type":"{{type}}", "Count":{{count}}, "SellPrice":1, "TotalSale":1, "AvgPricePaid":1 }
    """;

    private static string Buy(long marketId = Mine, string type = "steel", int count = 100, string timestamp = "2026-11-02T04:11:00Z") => $$"""
    { "timestamp":"{{timestamp}}", "event":"MarketBuy", "MarketID":{{marketId}}, "Type":"{{type}}", "Count":{{count}}, "BuyPrice":1, "TotalCost":1 }
    """;

    // --- When it sends ---

    [Fact]
    public async Task A_live_transfer_to_my_carrier_is_sent_once_as_a_signed_delta()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Transfer("steel", 841, "tocarrier"));

        var patch = Assert.Single(rig.Carrier.Patches);
        Assert.Equal(Mine, patch.MarketId);
        Assert.Equal(841, patch.Deltas["steel"]);
        Assert.Empty(rig.HandlerErrors);
        Assert.Empty(rig.Faults);
    }

    [Fact]
    public async Task Taking_cargo_off_the_carrier_is_a_negative_delta()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Transfer("steel", 276, "toship"));

        Assert.Equal(-276, Assert.Single(rig.Carrier.Patches).Deltas["steel"]);
    }

    [Fact]
    public async Task Trades_at_my_carriers_market_are_sent_in_the_right_direction()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Sell(count: 1276, type: "superconductors"));
        await rig.PublishAsync(Buy(count: 300, type: "steel"));

        Assert.Equal(1276, rig.Carrier.Patches[0].Deltas["superconductors"]);
        Assert.Equal(-300, rig.Carrier.Patches[1].Deltas["steel"]);
    }

    [Fact]
    public async Task Commodities_are_sent_in_ravens_spelling()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Sell(type: "$CMMComposite_name;", count: 5));

        Assert.Equal(5, Assert.Single(rig.Carrier.Patches).Deltas["cmmcomposite"]);
    }

    [Fact]
    public async Task One_event_with_several_commodities_is_one_patch_and_repeats_add_up()
    {
        using var rig = new Rig();

        await rig.PublishAsync("""
        { "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer", "Transfers":[
            { "Type":"steel", "Count":100, "Direction":"tocarrier" },
            { "Type":"steel", "Count":40, "Direction":"toship" },
            { "Type":"titanium", "Count":7, "Direction":"tocarrier" } ] }
        """);

        var patch = Assert.Single(rig.Carrier.Patches);
        Assert.Equal(60, patch.Deltas["steel"]);
        Assert.Equal(7, patch.Deltas["titanium"]);
    }

    [Fact]
    public async Task Movements_that_cancel_out_send_nothing()
    {
        using var rig = new Rig();

        await rig.PublishAsync("""
        { "timestamp":"2026-11-02T04:05:52Z", "event":"CargoTransfer", "Transfers":[
            { "Type":"steel", "Count":50, "Direction":"tocarrier" },
            { "Type":"steel", "Count":50, "Direction":"toship" } ] }
        """);

        Assert.Empty(rig.Carrier.Patches);
    }

    // --- Own carrier only ---

    [Fact]
    public async Task A_transfer_at_a_carrier_that_is_not_mine_sends_nothing()
    {
        using var rig = new Rig();
        rig.State.MarketId = Other;      // docked at a squadron carrier

        await rig.PublishAsync(Transfer());

        Assert.Empty(rig.Carrier.Patches);
        Assert.Equal(0, rig.Carrier.FindCalls);
    }

    [Fact]
    public async Task A_trade_at_any_other_market_sends_nothing()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Sell(marketId: 128666762));

        Assert.Empty(rig.Carrier.Patches);
    }

    [Fact]
    public async Task Nothing_is_sent_until_the_own_carrier_is_known()
    {
        using var rig = new Rig();
        rig.State.CarrierId = null;

        await rig.PublishAsync(Transfer());
        await rig.PublishAsync(Sell());

        Assert.Empty(rig.Carrier.Patches);
    }

    [Fact]
    public async Task A_transfer_with_no_known_dock_sends_nothing()
    {
        using var rig = new Rig();
        rig.State.MarketId = null;

        await rig.PublishAsync(Transfer());

        Assert.Empty(rig.Carrier.Patches);
    }

    // --- Gates ---

    [Fact]
    public async Task Nothing_is_sent_while_sharing_is_off()
    {
        using var rig = new Rig { Enabled = false };

        await rig.PublishAsync(Transfer());

        Assert.Empty(rig.Carrier.Patches);
        Assert.Equal(0, rig.Carrier.FindCalls);
    }

    [Fact]
    public async Task Replayed_history_is_never_sent()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Transfer(), historical: true);

        Assert.Empty(rig.Carrier.Patches);
    }

    [Fact]
    public async Task Developer_mode_events_never_leave_the_machine()
    {
        using var rig = new Rig { Suppressed = true };

        await rig.PublishAsync(Transfer());

        Assert.Empty(rig.Carrier.Patches);
    }

    [Fact]
    public async Task A_toggle_switched_off_while_a_movement_waits_is_honoured()
    {
        using var rig = new Rig();
        // The commander turns sharing off after the movement was accepted but before it is sent.
        rig.Carrier.OnFind = () => rig.Enabled = false;

        await rig.PublishAsync(Transfer());

        Assert.Empty(rig.Carrier.Patches);
        Assert.Empty(rig.Faults);
    }

    [Fact]
    public async Task The_same_journal_line_twice_is_sent_once()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Transfer());
        await rig.PublishAsync(Transfer());

        Assert.Single(rig.Carrier.Patches);
    }

    [Fact]
    public async Task Two_genuinely_separate_movements_are_both_sent()
    {
        using var rig = new Rig();

        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:05:52Z"));
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:06:52Z"));

        Assert.Equal(2, rig.Carrier.Patches.Count);
    }

    // --- Unknown carriers just skip ---

    [Fact]
    public async Task A_carrier_raven_does_not_know_is_skipped_quietly_and_remembered()
    {
        using var rig = new Rig();
        rig.Carrier.Lookup = new SharedCarrierLookup(false, new Dictionary<string, int>());

        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:05:52Z"));
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:06:52Z"));

        Assert.Empty(rig.Carrier.Patches);
        Assert.Empty(rig.Faults);                       // not an error: it simply is not tracked there
        Assert.Equal(1, rig.Carrier.FindCalls);         // asked once, then trusted for a while
    }

    [Fact]
    public async Task An_unknown_carrier_is_asked_about_again_after_a_short_while()
    {
        using var rig = new Rig();
        rig.Carrier.Lookup = new SharedCarrierLookup(false, new Dictionary<string, int>());
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:05:52Z"));

        // The commander links the carrier on Raven Colonial.
        rig.Carrier.Lookup = new SharedCarrierLookup(true, new Dictionary<string, int> { ["steel"] = 1000 });
        rig.Clock.Advance(RavenCarrierCargoSync.UnknownTtl + TimeSpan.FromSeconds(1));
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:06:52Z"));

        Assert.Single(rig.Carrier.Patches);
        Assert.Equal(2, rig.Carrier.FindCalls);
    }

    [Fact]
    public async Task A_failed_lookup_sends_nothing_and_is_not_remembered()
    {
        using var rig = new Rig();
        rig.Carrier.Lookup = new SharedCarrierLookup(false, new Dictionary<string, int>(), "HTTP 503");
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:05:52Z"));

        rig.Carrier.Lookup = new SharedCarrierLookup(true, new Dictionary<string, int> { ["steel"] = 1000 });
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:06:52Z"));

        Assert.Single(rig.Carrier.Patches);              // the second movement; the first was not queued for later
        Assert.Empty(rig.Faults);
    }

    // --- Failures ---

    [Fact]
    public async Task A_failed_write_is_not_retried_and_does_not_raise_a_fault()
    {
        using var rig = new Rig();
        rig.Carrier.PatchError = "HTTP 500";

        await rig.PublishAsync(Transfer());

        Assert.Single(rig.Carrier.Patches);             // a re-send could double count what Raven did receive
        Assert.Empty(rig.Faults);
        Assert.True(rig.Enabled);
    }

    [Fact]
    public async Task A_thrown_exception_does_not_stop_later_movements()
    {
        using var rig = new Rig();
        rig.Carrier.ThrowOnPatch = new InvalidOperationException("boom");
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:05:52Z"));

        rig.Carrier.ThrowOnPatch = null;
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:06:52Z"));

        Assert.Single(rig.Carrier.Patches);
        Assert.Empty(rig.HandlerErrors);
    }

    [Fact]
    public async Task A_rejected_key_raises_a_fault_once_and_stops_sending()
    {
        using var rig = new Rig();
        rig.Carrier.KeyRejected = true;

        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:05:52Z"));
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:06:52Z"));

        var fault = Assert.Single(rig.Faults);
        Assert.Equal(CarrierShareFaultKind.KeyRejected, fault.Kind);
        Assert.Contains("API key", fault.Message);
        Assert.False(rig.Enabled);                      // switched off by the fault callback
        Assert.Single(rig.Carrier.Patches);             // and nothing was tried after it
    }

    // --- Drift: a second reporter ---

    [Fact]
    public async Task A_second_reporter_doubling_every_movement_switches_sharing_off_with_a_fault()
    {
        using var rig = new Rig();
        rig.Carrier.SecondReporter = true;

        await rig.PublishAsync(Transfer("steel", 100, timestamp: "2026-11-02T04:05:52Z"));

        var fault = Assert.Single(rig.Faults);
        Assert.Equal(CarrierShareFaultKind.Drift, fault.Kind);
        Assert.Contains("another tool", fault.Message);
        Assert.False(rig.Enabled);
    }

    [Fact]
    public async Task Nothing_further_is_sent_after_drift()
    {
        using var rig = new Rig();
        rig.Carrier.SecondReporter = true;
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:05:52Z"));

        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:06:52Z"));
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:07:52Z"));

        Assert.Single(rig.Carrier.Patches);
        Assert.Single(rig.Faults);
    }

    [Fact]
    public async Task An_honest_tracker_over_many_movements_never_raises_drift()
    {
        using var rig = new Rig();

        for (var i = 0; i < 12; i++)
            await rig.PublishAsync(i % 3 == 0
                ? Buy(count: 10 + i, timestamp: $"2026-11-02T05:{i:00}:00Z")
                : Sell(count: 25 + i, timestamp: $"2026-11-02T05:{i:00}:30Z"));

        Assert.Equal(12, rig.Carrier.Patches.Count);
        Assert.Empty(rig.Faults);
        Assert.True(rig.Enabled);
    }

    [Fact]
    public async Task Switching_it_back_on_after_a_fault_starts_from_a_fresh_baseline()
    {
        using var rig = new Rig();
        rig.Carrier.SecondReporter = true;
        await rig.PublishAsync(Transfer(timestamp: "2026-11-02T04:05:52Z"));
        Assert.Single(rig.Faults);

        // The commander removes the other reporter and re-enables sharing.
        rig.Carrier.SecondReporter = false;
        rig.Enabled = true;
        rig.Carrier.Lookup = new SharedCarrierLookup(true, new Dictionary<string, int>(rig.Carrier.Server));
        rig.Clock.Advance(RavenCarrierCargoSync.KnownTtl + TimeSpan.FromSeconds(1));
        await rig.PublishAsync(Transfer("steel", 50, timestamp: "2026-11-02T04:09:52Z"));

        Assert.Equal(2, rig.Carrier.Patches.Count);
        Assert.Single(rig.Faults);                      // no new fault
        Assert.True(rig.Enabled);
    }

    // --- Lifetime ---

    [Fact]
    public async Task A_disposed_sync_sends_nothing_and_does_not_throw()
    {
        var rig = new Rig();
        rig.Dispose();

        await rig.PublishAsync(Transfer());

        Assert.Empty(rig.Carrier.Patches);
        Assert.Empty(rig.HandlerErrors);
    }

    // --- The adapter over the real client, against a fake handler ---

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Key)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.Headers.TryGetValues("rcc-key", out var values) ? values.Single() : null;
            lock (Requests) Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, key));
            return Task.FromResult(respond(request));
        }
    }

    private static (RavenColonialCarrierCargo adapter, FakeHandler handler) NewAdapter(
        Func<HttpRequestMessage, HttpResponseMessage> respond, Func<string> key)
    {
        var handler = new FakeHandler(respond);
        var client = new RavenColonialClient(
            new RavenColonialClientOptions { SoftwareName = "T", SoftwareVersion = "1", BaseUrl = "https://raven.test" },
            new HttpClient(handler));
        return (new RavenColonialCarrierCargo(client, key), handler);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body = "{}")
        => new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task The_adapter_sends_the_current_key_read_live()
    {
        var key = "first-key";
        var (adapter, handler) = NewAdapter(_ => Reply(HttpStatusCode.OK, """{ "steel": 5 }"""), () => key);
        var deltas = new Dictionary<string, int> { ["steel"] = 5 };

        await adapter.PatchAsync(Mine, deltas);
        key = "second-key";
        await adapter.PatchAsync(Mine, deltas);

        Assert.Equal(new[] { "first-key", "second-key" }, handler.Requests.Select(r => r.Key));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task The_adapter_maps_a_refused_key_to_KeyRejected(HttpStatusCode status)
    {
        var (adapter, _) = NewAdapter(_ => Reply(status), () => "k");

        var write = await adapter.PatchAsync(Mine, new Dictionary<string, int> { ["steel"] = 5 });

        Assert.True(write.KeyRejected);
    }

    [Fact]
    public async Task The_adapter_treats_a_server_error_as_a_plain_failure_not_a_rejected_key()
    {
        var (adapter, _) = NewAdapter(_ => Reply(HttpStatusCode.InternalServerError), () => "k");

        var write = await adapter.PatchAsync(Mine, new Dictionary<string, int> { ["steel"] = 5 });

        Assert.False(write.KeyRejected);
        Assert.NotNull(write.Error);
    }

    [Fact]
    public async Task The_adapter_with_no_key_does_not_call_out()
    {
        var (adapter, handler) = NewAdapter(_ => Reply(HttpStatusCode.OK), () => "  ");

        var write = await adapter.PatchAsync(Mine, new Dictionary<string, int> { ["steel"] = 5 });

        Assert.NotNull(write.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_adapter_reports_an_unknown_carrier_as_not_known()
    {
        var (adapter, _) = NewAdapter(_ => Reply(HttpStatusCode.NotFound, ""), () => "k");

        var lookup = await adapter.FindAsync(Mine);

        Assert.False(lookup.Known);
        Assert.Null(lookup.Error);
    }

    [Fact]
    public async Task The_adapter_hands_back_the_carriers_cargo_as_the_baseline()
    {
        var (adapter, _) = NewAdapter(_ => Reply(HttpStatusCode.OK, """{ "marketId": 3711552256, "name": "X8J-LVZ", "cargo": { "Steel": 1276 } }"""), () => "k");

        var lookup = await adapter.FindAsync(Mine);

        Assert.True(lookup.Known);
        Assert.Equal(1276, lookup.Cargo["steel"]);
    }
}
