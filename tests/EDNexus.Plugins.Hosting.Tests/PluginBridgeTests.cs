using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// Loaded into a collectible <see cref="AssemblyLoadContext"/> by
/// <see cref="PluginBridgeTests.DisposedSession_DoesNotPinACollectiblePlugin"/> to stand in for plugin code.
/// </summary>
[JsonSerializable(typeof(CollectibleProbe.ProbeShape))]
public sealed partial class ProbeJsonContext : JsonSerializerContext;

public static class CollectibleProbe
{
    private static readonly AsyncLocal<object?> Ambient = new();

    /// <summary>
    /// Subscribes with an <see cref="AsyncLocal{T}"/> holding an instance of a collectible type, as
    /// plugin code easily might (logging scopes, ambient contexts). The first subscription starts
    /// the dispatch thread; a plain <see cref="Thread.Start()"/> would capture this
    /// <see cref="ExecutionContext"/> and the thread would hold the plugin's objects for its whole
    /// life. <paramref name="onEvent"/> is told whether the handler saw the value, so the test
    /// fails if the thread inherited it. Cleared before returning so the caller's own context
    /// doesn't keep it.
    /// </summary>
    public static void Hook(IPluginEvents events, Action<bool> onEvent)
    {
        Ambient.Value = new Marker();
        try { events.SubscribeAny(_ => onEvent(Ambient.Value is not null)); }
        finally { Ambient.Value = null; }
    }

    private sealed class Marker;

    /// <summary>A plugin-defined payload type, as a plugin would bind with <c>System.Text.Json</c>.</summary>
    public sealed record ProbeShape(string? StarSystem);

    /// <summary>
    /// Reads <paramref name="journalEvent"/> the way the SDK tells plugins to: by navigating the
    /// <see cref="IJournalEvent.Payload"/> element, with no plugin type given to the serializer.
    /// </summary>
    public static string? ReadByNavigation(IJournalEvent journalEvent)
        => journalEvent.Payload.GetProperty("StarSystem").GetString();

    /// <summary>
    /// Binds the payload to a type that only exists in this (collectible) copy of the assembly with
    /// reflection-based <c>System.Text.Json</c> — the pattern the SDK warns against.
    /// </summary>
    public static string? ReadByReflectionBinding(IJournalEvent journalEvent)
        => journalEvent.Payload.Deserialize<ProbeShape>()?.StarSystem;

    /// <summary>
    /// Binds the payload through a source-generated context defined in this (collectible)
    /// assembly, the alternative the SDK documents.
    /// </summary>
    public static string? ReadBySourceGeneration(IJournalEvent journalEvent)
        => journalEvent.Payload.Deserialize(ProbeJsonContext.Default.ProbeShape)?.StarSystem;
}

public sealed class PluginBridgeTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private readonly JournalEventBus _bus = new();
    private readonly CommanderState _state = new();
    private readonly List<IDisposable> _owned = [];

    public PluginBridgeTests() => _ = new StateTracker(_bus, _state);

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }

    private PluginBridge Bridge(PluginBridgeOptions? options = null)
    {
        var bridge = new PluginBridge(_bus, _state, options);
        _owned.Add(bridge);
        return bridge;
    }

    private static PluginManifest Manifest(string id, params string[] capabilities)
        => new(id, id, "1.0.0", PluginSdk.CurrentVersionString) { Capabilities = capabilities };

    // Grants everything the manifest declares: the explicit "allow all" decision, test-only.
    private PluginBridgeSession Attach(PluginBridge bridge, string id, params string[] capabilities)
        => Track(bridge.Attach(Manifest(id, capabilities), capabilities));

    private PluginBridgeSession Track(PluginBridgeSession session)
    {
        _owned.Add(session);
        return session;
    }

    private void Publish(string json, bool historical = false)
    {
        Assert.True(JournalEntry.TryParse(json, historical, out var entry));
        _bus.Publish(entry);
    }

    private static string JumpTo(string system, string? body = null)
        => $$"""{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump","StarSystem":"{{system}}","Body":"{{body ?? system}}"}""";

    private static string CargoOf(int gold)
        => $$"""{"timestamp":"2026-09-26T10:00:01Z","event":"Cargo","Vessel":"Ship","Inventory":[{"Name":"gold","Name_Localised":"Gold","Count":{{gold}}}]}""";

    private const string Jump = """{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump","StarSystem":"Sol","Body":"Sol"}""";
    private const string Cargo = """{"timestamp":"2026-09-26T10:00:01Z","event":"Cargo","Vessel":"Ship","Inventory":[{"Name":"gold","Name_Localised":"Gold","Count":12}]}""";
    private const string Materials = """{"timestamp":"2026-09-26T10:00:02Z","event":"Materials","Raw":[{"Name":"iron","Count":40}],"Manufactured":[],"Encoded":[]}""";

    // ---- State: read-only, immutable snapshots, no path back to CommanderState ----

    [Fact]
    public void State_ReflectsCommanderState_AndCollectionsAreFrozenCopies()
    {
        var session = Attach(Bridge(), "a.state", PluginCapabilities.State);
        Publish(Jump);
        Publish(Cargo);
        Publish(Materials);

        var view = session.State;
        Assert.Equal("Sol", view.StarSystem);
        Assert.Equal(12, view.Cargo["Gold"]);
        Assert.Equal(40, view.RawMaterials["iron"]);

        // Neither the interface nor a cast back to a mutable type can reach the live collection.
        var cargo = view.Cargo;
        Assert.False(cargo is ConcurrentDictionary<string, int>);
        Assert.NotSame(_state.Cargo, cargo);
        var asDictionary = Assert.IsAssignableFrom<IDictionary<string, int>>(cargo);
        Assert.Throws<NotSupportedException>(() => asDictionary["Gold"] = 999);
        Assert.Throws<NotSupportedException>(() => asDictionary.Add("Silver", 1));
        Assert.Throws<NotSupportedException>(() => asDictionary.Clear());
        if (cargo is IDictionary nonGeneric)
            Assert.Throws<NotSupportedException>(() => nonGeneric["Gold"] = 999);
        Assert.Equal(12, _state.Cargo["Gold"]);

        // A copy handed out earlier does not change under the plugin; the view moves on.
        Publish(CargoOf(1));
        Assert.Equal(12, cargo["Gold"]);
        Assert.Equal(1, view.Cargo["Gold"]);
    }

    [Fact]
    public void State_ExposesNoSetters()
    {
        var session = Attach(Bridge(), "a.state", PluginCapabilities.State);
        Assert.All(session.State.GetType().GetProperties(), p => Assert.Null(p.GetSetMethod(nonPublic: false)));
        Assert.All(session.State.Snapshot().GetType().GetProperties(), p => Assert.Null(p.GetSetMethod(nonPublic: false)));
        Assert.IsNotType<CommanderState>(session.State);
    }

    [Fact]
    public void Snapshot_IsPointInTime()
    {
        var session = Attach(Bridge(), "a.state", PluginCapabilities.State);
        Publish(Jump);
        var snapshot = session.State.Snapshot();

        Publish(JumpTo("Achenar"));

        Assert.Equal("Sol", snapshot.StarSystem);
        Assert.Equal("Achenar", session.State.StarSystem);
        Assert.Same(snapshot, snapshot.Snapshot());
    }

    [Fact]
    public void State_ReflectsTheLastCompletedEvent_NotAMidEventMutation()
    {
        var session = Attach(Bridge(), "a.state", PluginCapabilities.State);
        Publish(Jump);

        // Written straight to the live object, outside any event: invisible until an event completes.
        _state.StarSystem = "Mid-event";
        Assert.Equal("Sol", session.State.StarSystem);

        Publish("""{"timestamp":"2026-09-26T10:00:00Z","event":"Music","MusicTrack":"Exploration"}""");
        Assert.Equal("Mid-event", session.State.StarSystem);
    }

    [Fact]
    public async Task Snapshot_NeverSeesAHalfAppliedEvent_WhileTheJournalThreadRebuildsState()
    {
        var session = Attach(Bridge(), "a.state", PluginCapabilities.State);
        const int Items = 60;
        string MaterialsSet(int count) =>
            $$"""{"timestamp":"2026-09-26T10:00:00Z","event":"Materials","Raw":[{{string.Join(",",
                Enumerable.Range(0, Items).Select(i => $$"""{"Name":"m{{i}}","Count":{{count}}}"""))}}],"Manufactured":[],"Encoded":[]}""";
        var sets = new[] { MaterialsSet(1), MaterialsSet(2) };
        Publish(sets[0]);
        Publish(JumpTo("A", "A 1"));

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                Publish(sets[i % 2]);
                Publish(i % 2 == 0 ? JumpTo("B", "B 1") : JumpTo("A", "A 1"));
            }
        });

        var reads = 0;
        var deadline = DateTime.UtcNow.AddSeconds(1.5);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = session.State.Snapshot();
            var raw = snapshot.RawMaterials;
            Assert.Equal(Items, raw.Count);
            Assert.Single(raw.Values.Distinct());
            Assert.StartsWith(snapshot.StarSystem + " ", snapshot.Body);
            reads++;
        }
        stop.Cancel();
        await writer.WaitAsync(Wait);

        Assert.True(reads > 100, $"only {reads} reads");
    }

    [Fact]
    public void DisposedSession_RevokesItsStateView()
    {
        var session = Attach(Bridge(), "a.state", PluginCapabilities.State);
        Publish(Jump);
        Publish(Cargo);
        var heldByPlugin = session.State;
        Assert.Equal("Sol", heldByPlugin.StarSystem);

        session.Dispose();
        Publish(JumpTo("Achenar"));

        Assert.Null(heldByPlugin.StarSystem);
        Assert.Empty(heldByPlugin.Cargo);
        Assert.Null(heldByPlugin.Snapshot().StarSystem);
    }

    // ---- Events: dispatch, isolation, unsubscribe ----

    [Fact]
    public void Events_DeliverAdaptedEvents_WithHistoricalFlag_AfterStateIsUpdated()
    {
        var session = Attach(Bridge(), "a.events", PluginCapabilities.Events, PluginCapabilities.State);
        var received = new BlockingCollection<(IJournalEvent Event, string? SystemInState)>();
        session.Events.Subscribe("FSDJump", e => received.Add((e, session.State.StarSystem)));

        Publish(Jump, historical: true);

        Assert.True(received.TryTake(out var got, Wait));
        Assert.Equal("FSDJump", got.Event.Event);
        Assert.True(got.Event.IsHistorical);
        Assert.False(got.Event.IsSimulated);
        Assert.Equal("Sol", got.Event.GetString("StarSystem"));
        Assert.Null(got.Event.GetString("NoSuchField"));
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero), got.Event.Timestamp);
        Assert.Equal("Sol", got.SystemInState);
        Assert.IsNotType<JournalEntry>(got.Event);
    }

    [Fact]
    public void Events_NameMatchingIsOrdinal()
    {
        var session = Attach(Bridge(), "a.events", PluginCapabilities.Events);
        var hits = new BlockingCollection<string>();
        session.Events.Subscribe("fsdjump", e => hits.Add("wrong-case"));
        session.Events.Subscribe("FSDJump", e => hits.Add("exact"));

        Publish(Jump);

        Assert.True(hits.TryTake(out var first, Wait));
        Assert.Equal("exact", first);
        DrainAndAssertIdle(session);
        Assert.Empty(hits);
    }

    [Fact]
    public void ThrowingHandler_IsReportedAndIsolated_FromItsSiblingsAndOtherPlugins()
    {
        var errors = new BlockingCollection<PluginHandlerError>();
        var bridge = Bridge(new PluginBridgeOptions { HandlerError = errors.Add });
        var bad = Attach(bridge, "bad.plugin", PluginCapabilities.Events);
        var good = Attach(bridge, "good.plugin", PluginCapabilities.Events);

        var badSibling = new BlockingCollection<string>();
        var goodSeen = new BlockingCollection<string>();
        bad.Events.SubscribeAny(_ => throw new InvalidOperationException("boom"));
        bad.Events.SubscribeAny(e => badSibling.Add(e.Event));
        good.Events.SubscribeAny(e => goodSeen.Add(e.Event));

        var engineSaw = 0;
        _bus.SubscribeAny(_ => engineSaw++);

        Publish(Jump);
        Publish(Cargo);

        Assert.True(errors.TryTake(out var error, Wait));
        Assert.Equal("bad.plugin", error.PluginId);
        Assert.Equal("FSDJump", error.EventName);
        Assert.Equal("System.InvalidOperationException", error.ExceptionType);
        Assert.Equal("boom", error.Message);

        Assert.Equal(new[] { "FSDJump", "Cargo" }, TakeN(badSibling, 2));
        Assert.Equal(new[] { "FSDJump", "Cargo" }, TakeN(goodSeen, 2));
        Assert.Equal(2, engineSaw);
        Assert.Equal("Sol", _state.StarSystem);
        Assert.True(SpinWait.SpinUntil(() => bad.HandlerErrorCount == 2, Wait));
        Assert.Equal(0, good.HandlerErrorCount);
    }

    [Fact]
    public async Task BlockedHandler_DoesNotBlockThePumpOrOtherPlugins()
    {
        var bridge = Bridge();
        var slow = Attach(bridge, "slow.plugin", PluginCapabilities.Events);
        var fast = Attach(bridge, "fast.plugin", PluginCapabilities.Events);

        using var release = new ManualResetEventSlim();
        using var slowEntered = new ManualResetEventSlim();
        slow.Events.SubscribeAny(_ => { slowEntered.Set(); release.Wait(Long); });
        var fastSeen = new BlockingCollection<string>();
        fast.Events.SubscribeAny(e => fastSeen.Add(e.Event));

        // The slow handler holds until released, so if Publish waited on it this would time out.
        var publish = Task.Run(() => { Publish(Jump); Publish(Cargo); });
        var finished = await Task.WhenAny(publish, Task.Delay(Wait));
        Assert.True(finished == publish, "Publish must not wait for plugin handlers.");
        Assert.True(slowEntered.Wait(Wait));
        Assert.Equal(new[] { "FSDJump", "Cargo" }, TakeN(fastSeen, 2));
        Assert.Equal(1, slow.PendingEventCount);   // Cargo waits behind the blocked FSDJump.

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => slow.PendingEventCount == 0, Wait));
    }

    [Fact]
    public void Dispose_OnSubscription_StopsDelivery_AndIsIdempotent()
    {
        var session = Attach(Bridge(), "a.events", PluginCapabilities.Events);
        var removable = new BlockingCollection<string>();
        var kept = new BlockingCollection<string>();
        var subscription = session.Events.On("FSDJump", e => removable.Add(e.Event));
        session.Events.OnAny(e => kept.Add(e.Event));

        Publish(Jump);
        Assert.True(removable.TryTake(out _, Wait));
        Assert.True(kept.TryTake(out _, Wait));

        subscription.Dispose();
        subscription.Dispose();
        Publish(Jump);

        Assert.True(kept.TryTake(out _, Wait));
        DrainAndAssertIdle(session);
        Assert.Empty(removable);
    }

    [Fact]
    public void DisposingTheSession_UnhooksFromTheBus_AndStopsItsThread()
    {
        var session = Attach(Bridge(), "a.events", PluginCapabilities.Events);
        var seen = 0;
        session.Events.SubscribeAny(_ => Interlocked.Increment(ref seen));
        Publish(Jump);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref seen) == 1, Wait));

        session.Dispose();
        Assert.True(session.WaitForExit(Wait));
        Publish(Jump);

        Assert.Equal(1, Volatile.Read(ref seen));
        Assert.Throws<ObjectDisposedException>(() => session.Events.SubscribeAny(_ => { }));
    }

    [Fact]
    public void NoHandlerStarts_AfterSessionDisposeReturns()
    {
        var session = Attach(Bridge(), "a.events", PluginCapabilities.Events);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var laterHandlerRuns = 0;
        session.Events.Subscribe("FSDJump", _ => { entered.Set(); release.Wait(Long); });
        session.Events.Subscribe("FSDJump", _ => Interlocked.Increment(ref laterHandlerRuns));

        Publish(Jump);
        Assert.True(entered.Wait(Wait));   // first handler is mid-flight with the second already captured
        session.Dispose();
        release.Set();

        Assert.True(session.WaitForExit(Wait));
        Assert.Equal(0, Volatile.Read(ref laterHandlerRuns));
    }

    [Fact]
    public void BlockedHandler_MakesWaitForExitReportCannotUnload()
    {
        var session = Attach(Bridge(), "stuck.plugin", PluginCapabilities.Events);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        session.Events.SubscribeAny(_ => { entered.Set(); release.Wait(Long); });
        Publish(Jump);
        Assert.True(entered.Wait(Wait));

        session.Dispose();
        Assert.False(session.WaitForExit(TimeSpan.FromMilliseconds(100)));

        release.Set();
        Assert.True(session.WaitForExit(Wait));
    }

    [Fact]
    public void FullQueue_DropsOldest_WithoutBlockingThePump()
    {
        var session = Attach(Bridge(new PluginBridgeOptions { QueueCapacity = 2 }), "slow.plugin", PluginCapabilities.Events);
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var seen = new BlockingCollection<string?>();
        session.Events.SubscribeAny(e =>
        {
            entered.Set();
            release.Wait(Long);
            seen.Add(e.GetString("StarSystem"));
        });

        Publish(JumpTo("A"));
        Assert.True(entered.Wait(Wait));
        foreach (var system in new[] { "B", "C", "D" })
            Publish(JumpTo(system));

        Assert.Equal(1, session.DroppedEventCount);
        release.Set();
        Assert.Equal(new[] { "A", "C", "D" }, TakeN(seen, 3));
    }

    [Fact]
    public void UnsubscribedEvents_AreNeitherQueuedNorAllowedToEvictWantedOnes()
    {
        var session = Attach(Bridge(new PluginBridgeOptions { QueueCapacity = 2 }), "jumps.only", PluginCapabilities.Events);
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var seen = new BlockingCollection<string?>();
        session.Events.Subscribe("FSDJump", e =>
        {
            entered.Set();
            release.Wait(Long);
            seen.Add(e.GetString("StarSystem"));
        });

        Publish(JumpTo("A"));
        Assert.True(entered.Wait(Wait));
        Publish(JumpTo("B"));
        for (var i = 0; i < 20; i++) Publish(Cargo);
        Publish(JumpTo("C"));

        Assert.Equal(2, session.PendingEventCount);
        Assert.Equal(0, session.DroppedEventCount);
        release.Set();
        Assert.Equal(new[] { "A", "B", "C" }, TakeN(seen, 3));
    }

    [Fact]
    public void DisposedSession_DoesNotPinACollectiblePlugin()
    {
        var context = RunProbePluginAndUnload();

        for (var i = 0; i < 20 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(context.IsAlive, "The plugin's AssemblyLoadContext was kept alive after its session was disposed.");
    }

    [Theory]
    [InlineData(nameof(CollectibleProbe.ReadByNavigation))]
    [InlineData(nameof(CollectibleProbe.ReadBySourceGeneration))]
    public void ReadingThePayloadInPluginCode_DoesNotPinACollectiblePlugin(string reader)
    {
        var (context, decoded) = ReadPayloadInProbePluginAndUnload(reader);

        Assert.Equal("Sol", decoded);
        Assert.True(IsCollected(context), $"Reading the payload with {reader} kept the plugin's AssemblyLoadContext alive.");
    }

    /// <summary>
    /// The reason IJournalEvent has no Deserialize&lt;T&gt;: reflection-based System.Text.Json caches
    /// every type it binds in process-wide state, so binding a plugin-defined type pins the plugin
    /// (verified here; if a future runtime stops doing this, the SDK may offer Deserialize again).
    /// </summary>
    [Fact]
    public void BindingAPluginDefinedTypeWithReflectionJson_PinsTheLoadContext_WhichIsWhyTheSdkHasNoDeserialize()
    {
        var (context, decoded) = ReadPayloadInProbePluginAndUnload(nameof(CollectibleProbe.ReadByReflectionBinding));

        Assert.Equal("Sol", decoded);
        Assert.False(IsCollected(context, attempts: 5), "System.Text.Json no longer pins collectible types: Deserialize<T> can be offered again.");
        Assert.DoesNotContain(typeof(IJournalEvent).GetMethods(), m => m.Name == "Deserialize");
    }

    private static bool IsCollected(WeakReference context, int attempts = 20)
    {
        for (var i = 0; i < attempts && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return !context.IsAlive;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference Context, string? Decoded) ReadPayloadInProbePluginAndUnload(string reader)
    {
        var context = new AssemblyLoadContext("bridge-payload-probe-" + reader, isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(typeof(CollectibleProbe).Assembly.Location);
        var probeType = assembly.GetType(typeof(CollectibleProbe).FullName!)!;
        Assert.NotSame(typeof(CollectibleProbe), probeType);   // really the collectible copy

        Assert.True(JournalEntry.TryParse(Jump, false, out var entry));
        IJournalEvent adapted = new JournalEventAdapter(entry, isSimulated: false);
        var decoded = (string?)probeType.GetMethod(reader)!.Invoke(null, [adapted]);

        context.Unload();
        return (new WeakReference(context), decoded);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference RunProbePluginAndUnload()
    {
        var context = new AssemblyLoadContext("bridge-probe", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(typeof(CollectibleProbe).Assembly.Location);
        var probeType = assembly.GetType(typeof(CollectibleProbe).FullName!)!;
        Assert.NotSame(typeof(CollectibleProbe), probeType);   // really the collectible copy

        var session = Attach(Bridge(), "probe.plugin", PluginCapabilities.Events);
        using var seen = new ManualResetEventSlim();
        var sawAmbient = true;
        probeType.GetMethod(nameof(CollectibleProbe.Hook))!.Invoke(null,
        [
            session.Events,
            (Action<bool>)(saw => { Volatile.Write(ref sawAmbient, saw); seen.Set(); }),
        ]);
        Publish(Jump);
        Assert.True(seen.Wait(Wait));
        Assert.False(Volatile.Read(ref sawAmbient), "The dispatch thread inherited the plugin's ExecutionContext.");

        session.Dispose();
        Assert.True(session.WaitForExit(Wait));
        context.Unload();
        return new WeakReference(context);
    }

    // ---- Capability gating ----

    [Fact]
    public void WithoutEventsCapability_SubscriptionIsDenied()
    {
        var session = Attach(Bridge(), "no.events", PluginCapabilities.State);

        var ex = Assert.Throws<UnauthorizedAccessException>(() => session.Events.Subscribe("FSDJump", _ => { }));
        Assert.Contains("no.events", ex.Message);
        Assert.Contains(PluginCapabilities.Events, ex.Message);
        Assert.Throws<UnauthorizedAccessException>(() => session.Events.SubscribeAny(_ => { }));
        Assert.Throws<UnauthorizedAccessException>(() => session.Events.On("FSDJump", _ => { }));
        Assert.Throws<UnauthorizedAccessException>(() => session.Events.OnAny(_ => { }));
    }

    [Fact]
    public void WithoutStateCapability_EveryReadIsDenied()
    {
        var session = Attach(Bridge(), "no.state", PluginCapabilities.Events);
        var state = session.State;

        Assert.Throws<UnauthorizedAccessException>(() => state.Name);
        Assert.Throws<UnauthorizedAccessException>(() => state.StarSystem);
        Assert.Throws<UnauthorizedAccessException>(() => state.Body);   // a DIM member on the SDK side
        Assert.Throws<UnauthorizedAccessException>(() => state.Cargo);
        Assert.Throws<UnauthorizedAccessException>(() => state.Snapshot());
    }

    [Fact]
    public void GrantedCapabilities_NarrowTheDeclaredSet_AndCannotWidenIt()
    {
        var bridge = Bridge();
        var revoked = Track(bridge.Attach(Manifest("revoked", PluginCapabilities.Events, PluginCapabilities.State), [PluginCapabilities.Events]));
        var widened = Track(bridge.Attach(Manifest("widened", PluginCapabilities.Events), [PluginCapabilities.Events, PluginCapabilities.State]));

        Assert.Throws<UnauthorizedAccessException>(() => revoked.State.Name);
        Assert.Throws<UnauthorizedAccessException>(() => widened.State.Name);
        revoked.Events.SubscribeAny(_ => { });
    }

    // ---- Developer mode ----

    [Fact]
    public void DeveloperMode_FlagsEvents_AndWithholdsThemFromNetworkPlugins()
    {
        var simulated = true;
        var bridge = Bridge(new PluginBridgeOptions { IsSimulated = () => Volatile.Read(ref simulated) });
        var local = Attach(bridge, "local.plugin", PluginCapabilities.Events, PluginCapabilities.State);
        var networked = Attach(bridge, "net.plugin", PluginCapabilities.Events, PluginCapabilities.State, PluginCapabilities.Network);

        var localSeen = new BlockingCollection<IJournalEvent>();
        var netSeen = new BlockingCollection<IJournalEvent>();
        local.Events.SubscribeAny(localSeen.Add);
        networked.Events.SubscribeAny(netSeen.Add);

        Publish(Jump);

        Assert.True(localSeen.TryTake(out var localEvent, Wait));
        Assert.True(localEvent.IsSimulated);
        Assert.Equal("Sol", local.State.StarSystem);
        Assert.Null(networked.State.StarSystem);          // fabricated state is hidden too
        Assert.Empty(networked.State.Cargo);
        DrainAndAssertIdle(networked, withheld: true);
        Assert.Empty(netSeen);

        Volatile.Write(ref simulated, false);
        Publish(Jump);
        Assert.True(netSeen.TryTake(out var live, Wait));
        Assert.False(live.IsSimulated);                   // each event is stamped on its own...
        Assert.Null(networked.State.StarSystem);          // ...but the commander is still the fabricated one (see below)
    }

    [Fact]
    public void DeveloperMode_StateStaysSimulated_UntilTheAppRebuildsTheHostAndBridge()
    {
        // CommanderState is not reset when a developer-mode source goes quiet, so the fabricated
        // balance, cargo and materials are still in it after the flag flips: a snapshot built then
        // must not be handed to a networked plugin as live data.
        var simulated = true;
        var bridge = Bridge(new PluginBridgeOptions { IsSimulated = () => Volatile.Read(ref simulated) });
        var networked = Attach(bridge, "net.plugin", PluginCapabilities.State, PluginCapabilities.Network);
        var local = Attach(bridge, "local.plugin", PluginCapabilities.State);
        Publish(Jump);
        Publish(CargoOf(500));
        Publish("""{"timestamp":"2026-09-26T10:00:03Z","event":"LoadGame","Commander":"Fabricated","Credits":123456789}""");

        Volatile.Write(ref simulated, false);
        Publish(JumpTo("Real"));                           // a genuine event after developer mode

        Assert.Null(networked.State.StarSystem);
        Assert.Equal(0, networked.State.Balance);
        Assert.Null(networked.State.Name);
        Assert.Empty(networked.State.Cargo);
        Assert.Equal("Real", local.State.StarSystem);      // non-networked plugins always see the engine's state
        Assert.Equal(500, local.State.Cargo["Gold"]);

        // The app's rebuild: a fresh state and bus, and a bridge over them. That one is live.
        var freshBus = new JournalEventBus();
        var freshState = new CommanderState();
        _ = new StateTracker(freshBus, freshState);
        using var freshBridge = new PluginBridge(freshBus, freshState, new PluginBridgeOptions { IsSimulated = () => Volatile.Read(ref simulated) });
        using var freshSession = freshBridge.Attach(
            Manifest("net.plugin", PluginCapabilities.State, PluginCapabilities.Network),
            [PluginCapabilities.State, PluginCapabilities.Network]);
        Assert.True(JournalEntry.TryParse(JumpTo("Fresh"), false, out var entry));
        freshBus.Publish(entry);
        Assert.Equal("Fresh", freshSession.State.StarSystem);
    }

    [Fact]
    public void DeveloperMode_StateSimulatedFlag_IsStickyEvenWhenDeveloperModeWasAlreadyOnAtConstruction()
    {
        var simulated = true;
        var bridge = Bridge(new PluginBridgeOptions { IsSimulated = () => Volatile.Read(ref simulated) });   // dev mode already on
        var networked = Attach(bridge, "net.plugin", PluginCapabilities.State, PluginCapabilities.Network);
        Volatile.Write(ref simulated, false);

        Publish(Jump);

        Assert.Null(networked.State.StarSystem);
    }

    [Fact]
    public void DeveloperMode_StillWithholds_WhenTheDeclaredNetworkCapabilityIsRevoked()
    {
        var bridge = Bridge(new PluginBridgeOptions { IsSimulated = () => true });
        var session = Track(bridge.Attach(
            Manifest("net.plugin", PluginCapabilities.Events, PluginCapabilities.State, PluginCapabilities.Network),
            [PluginCapabilities.Events, PluginCapabilities.State]));
        var seen = new BlockingCollection<IJournalEvent>();
        session.Events.SubscribeAny(seen.Add);

        Publish(Jump);

        DrainAndAssertIdle(session, withheld: true);
        Assert.Empty(seen);
        Assert.Null(session.State.StarSystem);
    }

    [Fact]
    public void ThrowingDeveloperModePredicate_FailsClosed()
    {
        var bridge = Bridge(new PluginBridgeOptions { IsSimulated = () => throw new InvalidOperationException() });
        var networked = Attach(bridge, "net.plugin", PluginCapabilities.Events, PluginCapabilities.State, PluginCapabilities.Network);
        var seen = new BlockingCollection<IJournalEvent>();
        networked.Events.SubscribeAny(seen.Add);

        Publish(Jump);

        DrainAndAssertIdle(networked, withheld: true);
        Assert.Empty(seen);
        Assert.Null(networked.State.StarSystem);
    }

    [Fact]
    public void DeveloperMode_FabricatedStateStaysHidden_WhenThePredicateFlipsBeforeTeardown()
    {
        // MainWindowViewModel.RebuildHost disposes the dev-mode host, then flips the predicate
        // false, then builds the new host — the loader may dispose the old sessions only after that.
        var simulated = true;
        var bridge = Bridge(new PluginBridgeOptions { IsSimulated = () => Volatile.Read(ref simulated) });
        var local = Attach(bridge, "local.plugin", PluginCapabilities.State);
        var networked = Attach(bridge, "net.plugin", PluginCapabilities.State, PluginCapabilities.Network);

        Publish(Jump);
        Publish(Cargo);

        // No further events reach the old bus; the predicate flips while the sessions are still live.
        Volatile.Write(ref simulated, false);
        Assert.Null(networked.State.StarSystem);
        Assert.Empty(networked.State.Cargo);
        Assert.Null(networked.State.Snapshot().StarSystem);
        Assert.Equal("Sol", local.State.StarSystem);

        // Nor does disposing the bridge ahead of the sessions reveal it.
        bridge.Dispose();
        Assert.Null(networked.State.StarSystem);
        Assert.Equal("Sol", local.State.StarSystem);
    }

    [Fact]
    public void DeveloperMode_SnapshotBuiltWhileThePredicateThrew_StaysHidden()
    {
        var throwing = true;
        var bridge = Bridge(new PluginBridgeOptions
        {
            IsSimulated = () => Volatile.Read(ref throwing) ? throw new InvalidOperationException() : false,
        });
        var networked = Attach(bridge, "net.plugin", PluginCapabilities.State, PluginCapabilities.Network);

        Publish(Jump);
        Volatile.Write(ref throwing, false);

        Assert.Null(networked.State.StarSystem);
    }

    [Fact]
    public void DisposedBridge_DoesNotLetALeftoverViewPinTheEngineState()
    {
        var (view, state) = LeaveAViewBehind();

        for (var i = 0; i < 20 && state.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(state.IsAlive, "A state view that outlived its bridge kept the CommanderState alive.");
        Assert.Equal("Sol", view.StarSystem);   // still reads the last snapshot
        GC.KeepAlive(view);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IReadOnlyCommanderState View, WeakReference State) LeaveAViewBehind()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        var bridge = new PluginBridge(bus, state);
        var session = bridge.Attach(Manifest("leaky.plugin", PluginCapabilities.State), [PluginCapabilities.State]);

        Assert.True(JournalEntry.TryParse(Jump, false, out var entry));
        bus.Publish(entry);

        bridge.Dispose();   // the session is deliberately left undisposed
        return (session.State, new WeakReference(state));
    }

    // ---- Consent: Attach has no "grant everything" default ----

    [Fact]
    public void Attach_RequiresTheGrantedSet_AndAnEmptyOneGrantsNothing()
    {
        var bridge = Bridge();
        var manifest = Manifest("nothing.granted", PluginCapabilities.Events, PluginCapabilities.State);

        Assert.Throws<ArgumentNullException>(() => bridge.Attach(manifest, null!));

        var session = Track(bridge.Attach(manifest, []));
        Assert.Throws<UnauthorizedAccessException>(() => session.Events.SubscribeAny(_ => { }));
        Assert.Throws<UnauthorizedAccessException>(() => session.State.Name);
    }

    [Fact]
    public void Attach_DoesNotHaveAnOptionalGrantedParameter()
    {
        var attach = typeof(PluginBridge).GetMethod(nameof(PluginBridge.Attach))!;

        Assert.All(attach.GetParameters(), parameter => Assert.False(parameter.IsOptional, parameter.Name));
    }

    [Fact]
    public void Storage_AndUi_FailClosedUntilTheBridgeHasBackendsForThem()
    {
        var session = Attach(Bridge(), "greedy.plugin", PluginCapabilities.Storage, PluginCapabilities.UiDashboard, PluginCapabilities.UiOverlay);

        var storage = Assert.Throws<UnauthorizedAccessException>(() => session.Storage.SetString("k", "v"));
        Assert.Contains("greedy.plugin", storage.Message);
        Assert.Contains(PluginCapabilities.Storage, storage.Message);
        Assert.Throws<UnauthorizedAccessException>(() => session.Storage.GetString("k"));
        Assert.Throws<UnauthorizedAccessException>(() => session.Storage.Remove("k"));
        var ui = Assert.Throws<UnauthorizedAccessException>(() => session.Ui.Register("card", new object()));
        Assert.Contains(PluginCapabilities.UiDashboard, ui.Message);
        Assert.Contains(PluginCapabilities.UiOverlay, ui.Message);
    }

    // ---- Handler errors carry text, never the plugin's exception ----

    [Fact]
    public void HandlerError_IsPlainText_EvenForAnExceptionWhoseMessageThrows()
    {
        Assert.DoesNotContain(typeof(PluginHandlerError).GetProperties(), p => typeof(Exception).IsAssignableFrom(p.PropertyType));
        var errors = new BlockingCollection<PluginHandlerError>();
        var session = Attach(Bridge(new PluginBridgeOptions { HandlerError = errors.Add }), "evil.plugin", PluginCapabilities.Events);
        session.Events.SubscribeAny(_ => throw new HostileException());

        Publish(Jump);

        Assert.True(errors.TryTake(out var error, Wait));
        Assert.Equal("evil.plugin", error.PluginId);
        Assert.Equal("FSDJump", error.EventName);
        Assert.Equal(typeof(HostileException).FullName, error.ExceptionType);
        Assert.Equal($"<message unavailable: {typeof(HostileException).FullName}>", error.Message);
        Assert.Equal(1, session.HandlerErrorCount);
    }

    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException("Message getter");
        public override string? StackTrace => throw new InvalidOperationException("StackTrace getter");
        public override string ToString() => throw new InvalidOperationException("ToString");
    }

    // ---- Queue bounds: count and bytes ----

    private static string Padded(int padding, string seq)
        => $$"""{"timestamp":"2026-09-26T10:00:00Z","event":"Shipyard","Seq":"{{seq}}","Pad":"{{new string('x', padding)}}"}""";

    [Fact]
    public void QueueByteCapacity_BoundsWhatABlockedPluginRetains_AndDropsTheOldest()
    {
        const int Payload = 10_000;
        var bridge = Bridge(new PluginBridgeOptions { QueueByteCapacity = 50_000 });   // room for four ~10 kB events
        var session = Attach(bridge, "blocked.plugin", PluginCapabilities.Events);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var seen = new List<string>();
        session.Events.SubscribeAny(e =>
        {
            lock (seen) seen.Add(e.GetString("Seq")!);
            entered.Set();
            release.Wait(Long);
        });
        Publish(Padded(Payload, "first"));
        Assert.True(entered.Wait(Wait));   // the plugin is now blocked inside its handler

        for (var i = 0; i < 20; i++)
            Publish(Padded(Payload, i.ToString()));

        Assert.InRange(session.PendingEventBytes, 1, 50_000);
        Assert.InRange(session.PendingEventCount, 1, 4);
        Assert.True(session.DroppedEventCount >= 16, $"dropped {session.DroppedEventCount}");
        release.Set();
        for (var i = 0; i < 250 && session.PendingEventCount > 0; i++) Thread.Sleep(20);
        Assert.Equal(0, session.PendingEventBytes);
        lock (seen)
        {
            Assert.Equal("first", seen[0]);
            Assert.Equal("19", seen[^1]);   // the newest survived
        }
    }

    [Fact]
    public void QueueByteCapacity_AnEventLargerThanTheWholeBudget_IsDroppedWithoutLosingTheBacklog()
    {
        var bridge = Bridge(new PluginBridgeOptions { QueueByteCapacity = 5_000 });
        var session = Attach(bridge, "huge.plugin", PluginCapabilities.Events);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        session.Events.SubscribeAny(_ => { entered.Set(); release.Wait(Long); });
        Publish(Jump);
        Assert.True(entered.Wait(Wait));
        Publish(Cargo);                       // small: queued
        var pending = session.PendingEventBytes;

        Publish(Padded(10_000, "huge"));      // can never fit

        Assert.Equal(1, session.DroppedEventCount);
        Assert.Equal(1, session.PendingEventCount);
        Assert.Equal(pending, session.PendingEventBytes);
        release.Set();
    }

    [Fact]
    public void QueueByteCapacity_MustBePositive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new PluginBridge(_bus, _state, new PluginBridgeOptions { QueueByteCapacity = 0 }));

    [Fact]
    public void QueueCapacity_StillBoundsTheCountOfSmallEvents()
    {
        var bridge = Bridge(new PluginBridgeOptions { QueueCapacity = 3 });
        var session = Attach(bridge, "chatty.plugin", PluginCapabilities.Events);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        session.Events.SubscribeAny(_ => { entered.Set(); release.Wait(Long); });
        Publish(Jump);
        Assert.True(entered.Wait(Wait));

        for (var i = 0; i < 10; i++) Publish(Cargo);

        Assert.Equal(3, session.PendingEventCount);
        Assert.Equal(7, session.DroppedEventCount);
        release.Set();
    }

    // ---- Adapter ----

    [Fact]
    public void Adapter_PrefersLocalised_AndExposesTheDetachedPayload()
    {
        Assert.True(JournalEntry.TryParse(
            """{"timestamp":"2026-09-26T10:00:00Z","event":"Died","KillerShip":"python","KillerShip_Localised":"Python","Count":"nope"}""",
            false, out var entry));
        IJournalEvent adapted = new JournalEventAdapter(entry, isSimulated: false);

        Assert.Equal("Python", adapted.GetLocalised("KillerShip"));
        Assert.Null(adapted.GetInt64("Count"));
        Assert.Null(adapted.GetString(null!));
        Assert.Equal(JsonValueKind.Object, adapted.Payload.ValueKind);
        Assert.Equal("nope", adapted.Payload.GetProperty("Count").GetString());
        Assert.Equal("Died", adapted.Payload.GetProperty("event").GetString());
    }

    // ---- Engine bus hook ----

    [Fact]
    public void BusCompletedHook_RunsAfterEngineHandlers_AndDisposes()
    {
        var bus = new JournalEventBus();
        var order = new List<string>();
        var hook = bus.SubscribeCompleted(_ => order.Add("completed"));
        bus.SubscribeAny(_ => order.Add("any"));
        bus.Subscribe("FSDJump", _ => order.Add("named"));

        Assert.True(JournalEntry.TryParse(Jump, false, out var entry));
        bus.Publish(entry);
        hook.Dispose();
        hook.Dispose();
        bus.Publish(entry);

        Assert.Equal(new[] { "any", "named", "completed", "any", "named" }, order);
    }

    [Fact]
    public void BusCompletedHook_IsolatesExceptions()
    {
        var bus = new JournalEventBus();
        var errors = new List<Exception>();
        bus.HandlerError += (_, ex) => errors.Add(ex);
        var ran = 0;
        using var bad = bus.SubscribeCompleted(_ => throw new InvalidOperationException("boom"));
        using var good = bus.SubscribeCompleted(_ => ran++);

        Assert.True(JournalEntry.TryParse(Jump, false, out var entry));
        bus.Publish(entry);

        Assert.Equal(1, ran);
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
    }

    [Fact]
    public void BusCompletedHook_ToleratesReentrantSubscribeDisposeAndPublish()
    {
        var bus = new JournalEventBus();
        Assert.True(JournalEntry.TryParse(Jump, false, out var outer));
        Assert.True(JournalEntry.TryParse(Cargo, false, out var inner));
        var log = new List<string>();
        var errors = new List<Exception>();
        bus.HandlerError += (_, ex) => errors.Add(ex);

        IDisposable? added = null;
        IDisposable? self = null;
        self = bus.SubscribeCompleted(e =>
        {
            log.Add("self:" + e.Event);
            self!.Dispose();                                                    // remove itself mid-publish
            added = bus.SubscribeCompleted(x => log.Add("added:" + x.Event));   // add another mid-publish
            bus.Publish(inner);                                                 // nested publish
        });

        bus.Publish(outer);
        bus.Publish(outer);
        added?.Dispose();

        Assert.Empty(errors);
        // The handler added during the outer publish is not in that publish's snapshot, but does see
        // the nested one; the self-removed handler never runs again.
        Assert.Equal(new[] { "self:FSDJump", "added:Cargo", "added:FSDJump" }, log);
    }

    // ---- helpers ----

    private static string[] TakeN<T>(BlockingCollection<T> source, int count)
    {
        var items = new List<string>();
        for (var i = 0; i < count; i++)
        {
            Assert.True(source.TryTake(out var item, Wait), $"Timed out waiting for item {i + 1} of {count}.");
            items.Add(item?.ToString() ?? "<null>");
        }
        return [.. items];
    }

    /// <summary>
    /// Waits until everything published so far has been through the plugin's queue, by publishing a
    /// sentinel and waiting for a probe handler to see it (delivery is in order). With
    /// <paramref name="withheld"/>, the plugin is expected to be receiving nothing at all, so the
    /// sentinel must not arrive and nothing may be queued.
    /// </summary>
    private void DrainAndAssertIdle(PluginBridgeSession session, bool withheld = false)
    {
        using var seen = new ManualResetEventSlim();
        using var probe = session.Events.On("DrainProbe", _ => seen.Set());
        var droppedBefore = session.DroppedEventCount;
        Publish("""{"timestamp":"2026-09-26T10:00:00Z","event":"DrainProbe"}""");
        if (withheld)
        {
            Assert.Equal(0, session.PendingEventCount);
            Assert.False(seen.Wait(TimeSpan.FromMilliseconds(200)));
        }
        else
        {
            Assert.True(seen.Wait(Wait));
        }
        Assert.Equal(droppedBefore, session.DroppedEventCount);
    }
}
