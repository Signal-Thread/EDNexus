using System.Diagnostics;
using System.Threading.Channels;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EliteDangerous.RavenColonial;

namespace EDNexus.Core.Colonisation;

/// <summary>Why carrier sharing stopped itself.</summary>
public enum CarrierShareFaultKind
{
    /// <summary>Raven Colonial refused the commander's API key.</summary>
    KeyRejected,

    /// <summary>The carrier moved by more than EDNexus reported: very likely a second tool reports it too.</summary>
    Drift,
}

/// <summary>
/// Sharing stopped itself, and the commander needs to be told: the app turns the setting off and shows
/// <see cref="Message"/> as an error on the main window.
/// </summary>
/// <param name="Kind">What went wrong.</param>
/// <param name="Message">Plain-language text for the commander, including what to do about it.</param>
/// <param name="Detail">Technical detail for the log (which commodities drifted); never shown as the headline.</param>
public sealed record CarrierShareFault(CarrierShareFaultKind Kind, string Message, string? Detail = null)
{
    internal static CarrierShareFault KeyRejected() => new(CarrierShareFaultKind.KeyRejected,
        "Raven Colonial rejected your API key, so your fleet carrier cargo is no longer being shared. " +
        "Check the key in Settings, then switch sharing back on.");

    internal static CarrierShareFault Drift(string? detail) => new(CarrierShareFaultKind.Drift,
        "Raven Colonial's record of your fleet carrier changed by more than EDNexus reported. That usually means " +
        "another tool (the RavenColonial EDMC plugin or SrvSurvey) is reporting your carrier too, which counts " +
        "every movement twice. Sharing has been switched off. Use only one reporter, then switch it back on in Settings.",
        detail);
}

/// <summary>
/// Pushes changes to the commander's own fleet carrier cargo up to the shared Raven Colonial tracker. It
/// listens for the journal's cargo movements and, when the commander has opted in, reports each one on
/// their carrier as a signed delta.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in, live and own-carrier only.</b> Nothing is sent unless <c>isEnabled</c> says so (read
/// live), the event is not replayed history, and reporting is not suppressed (developer mode fabricates
/// events that must never leave the machine) — the same gates <see cref="RavenContributionSync"/> applies.
/// A movement also has to be on the commander's <em>own</em> carrier: its market id must equal the
/// <c>CarrierID</c> from <c>CarrierStats</c>. A squadron carrier, or any other station's market, is
/// never reported, and neither is a movement while the own carrier is not yet known.
/// </para>
/// <para>
/// <b>Never in the way of the journal.</b> The bus handler only validates and enqueues; a single
/// background worker does the lookup and the write, so a slow or dead tracker costs nothing on the pump.
/// The queue is bounded and a movement that does not fit is dropped (and logged). There is no retry
/// beyond the one the client makes for a 429/503: re-sending could double count a change the tracker did
/// receive.
/// </para>
/// <para>
/// <b>Registered carriers only.</b> The carrier has to be registered to the commander on the tracker
/// (linked to their account, as listed under their name), not merely known to it. When it is not,
/// nothing is sent and nothing is reported as an error; the answer is remembered for
/// <see cref="UnknownTtl"/> so a carrier registered mid-session is picked up soon.
/// </para>
/// <para>
/// <b>Two reporters.</b> The tracker adds up whatever it is told, so another tool reporting the same
/// journal movements doubles them. Each reply is checked by a <see cref="CarrierDriftMonitor"/>; on a
/// suspected duplicate, or when the key is refused, the sync reports a <see cref="CarrierShareFault"/>
/// through <c>onFault</c> and sends nothing more until the commander switches it back on. The callback
/// must turn <c>isEnabled</c> off before it returns, which is what stops the sending.
/// </para>
/// </remarks>
public sealed class RavenCarrierCargoSync : IDisposable
{
    /// <summary>How long "this carrier is registered to the commander" is trusted before it is asked again.</summary>
    internal static readonly TimeSpan KnownTtl = TimeSpan.FromMinutes(10);

    /// <summary>How long "this carrier is not registered to the commander" is trusted — shorter, so a carrier registered mid-session is found.</summary>
    internal static readonly TimeSpan UnknownTtl = TimeSpan.FromMinutes(5);

    private const int QueueCapacity = 32;
    private const int RememberedEvents = 256;

    private readonly CommanderState _state;
    private readonly ISharedCarrierCargo _carrier;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isSuppressed;
    private readonly Action<CarrierShareFault> _onFault;
    private readonly TimeProvider _time;

    private readonly Channel<Movement> _queue = Channel.CreateBounded<Movement>(
        new BoundedChannelOptions(QueueCapacity)
        {
            // Wait (not a Drop mode) so TryWrite reports a full queue instead of silently succeeding.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    // Guards _disposed, _seen/_seenOrder and the idle bookkeeping; the handler runs on the pump thread
    // while Dispose may come from any other.
    private readonly object _gate = new();
    private bool _disposed;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private int _pending;
    private TaskCompletionSource _idle = CompletedIdle();

    // Touched only by the worker, so they need no lock.
    private readonly Dictionary<string, CachedLookup> _lookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly CarrierDriftMonitor _drift = new();
    private string? _driftFor;          // whose books _drift is keeping

    /// <param name="bus">The journal bus to listen on.</param>
    /// <param name="state">Supplies the commander's own carrier id and the market they are docked at.</param>
    /// <param name="carrier">Looks the carrier up and reports changes to it.</param>
    /// <param name="isEnabled">Live opt-in (including having an API key). Nothing is reported while this returns false.</param>
    /// <param name="onFault">Told when sharing stops itself; must make <paramref name="isEnabled"/> return false before returning.</param>
    /// <param name="isSuppressed">Optional live predicate; while it returns true nothing is sent (developer mode).</param>
    /// <param name="time">Clock for the lookup cache; the system clock when null.</param>
    public RavenCarrierCargoSync(
        JournalEventBus bus,
        CommanderState state,
        ISharedCarrierCargo carrier,
        Func<bool> isEnabled,
        Action<CarrierShareFault> onFault,
        Func<bool>? isSuppressed = null,
        TimeProvider? time = null)
    {
        _state = state;
        _carrier = carrier;
        _isEnabled = isEnabled;
        _onFault = onFault;
        _isSuppressed = isSuppressed ?? (static () => false);
        _time = time ?? TimeProvider.System;

        _worker = Task.Run(() => RunAsync(_cts.Token));
        // The worker catches its own failures; this only keeps the token source alive until it ends.
        _worker.ContinueWith(_ => _cts.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        bus.Subscribe("CargoTransfer", OnMovement);
        bus.Subscribe("MarketSell", OnMovement);
        bus.Subscribe("MarketBuy", OnMovement);
    }

    /// <summary>A task that completes once every movement accepted so far has been dealt with. Test hook.</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate) return _idle.Task;
    }

    private void OnMovement(JournalEntry e)
    {
        // Every early return below is a deliberate "do not report": the journal pump must never wait on us.
        if (e.IsHistorical) return;            // replayed history is never re-reported
        if (!_isEnabled()) return;             // opt-in, read live so toggles take effect
        if (_isSuppressed()) return;           // developer mode: fabricated events stay on this machine

        // Own, registered carrier only. The id the movement happened on must be the commander's own
        // carrier's, and the registry is asked about that commander by name; while either is unknown
        // there is no way to say, so nothing is sent.
        var commander = _state.Name?.Trim();
        if (string.IsNullOrEmpty(commander)) return;
        if (CarrierCargoMovements.Read(e, _state.MarketId) is not { } movement) return;
        if (_state.CarrierId is not long own || movement.MarketId != own) return;

        // The whole line, timestamp included, is what makes one movement different from the next.
        var key = e.Raw.GetRawText();

        lock (_gate)
        {
            if (_disposed) return;
            if (!_seen.Add(key)) return;       // the same event twice must not be reported twice
            _seenOrder.Enqueue(key);
            while (_seenOrder.Count > RememberedEvents) _seen.Remove(_seenOrder.Dequeue());

            if (_pending++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (!_queue.Writer.TryWrite(new Movement(commander, own, movement.Deltas)))
        {
            Trace.TraceWarning("Raven Colonial: carrier cargo change not shared (queue full or shutting down).");
            Complete();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var movement in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await ShareAsync(movement, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Trace.TraceWarning("Raven Colonial: sharing a carrier cargo change failed: " + ex.Message); }
                finally { Complete(); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { Trace.TraceError("Raven Colonial: carrier cargo worker stopped: " + ex); }
        finally
        {
            // Anything still queued will never run; release whoever is waiting on it.
            lock (_gate)
            {
                _pending = 0;
                _idle.TrySetResult();
            }
        }
    }

    private async Task ShareAsync(Movement movement, CancellationToken ct)
    {
        // Settings can change while a movement waits in the queue; honour the latest answer.
        if (!_isEnabled() || _isSuppressed()) return;

        // The drift monitor keeps one carrier's books. A different commander or carrier (a new carrier
        // registered, another account loaded) starts a fresh set, or its first send would be judged
        // against the old carrier's totals.
        var owner = $"{movement.Commander}|{movement.MarketId}";
        if (!string.Equals(_driftFor, owner, StringComparison.OrdinalIgnoreCase))
        {
            _drift.Reset();
            _driftFor = owner;
        }

        var known = await ResolveAsync(movement.Commander, movement.MarketId, ct).ConfigureAwait(false);
        if (known is null) return;
        if (!_isEnabled() || _isSuppressed()) return;

        // Raven's spelling, with a movement and its reverse netting out rather than being sent as two.
        var send = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var delta in movement.Deltas)
        {
            var commodity = RavenCommodityKey.Normalize(delta.Commodity);
            if (commodity.Length == 0) continue;
            send[commodity] = send.TryGetValue(commodity, out var already) ? already + delta.Units : delta.Units;
        }
        foreach (var commodity in send.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList()) send.Remove(commodity);
        if (send.Count == 0) return;

        // The first send is compared against what the tracker held just before it, not against nothing.
        if (!_drift.HasBaseline) _drift.SetBaseline(known.Cargo);

        var write = await _carrier.PatchAsync(movement.MarketId, send, ct).ConfigureAwait(false);
        if (write.KeyRejected)
        {
            Trace.TraceWarning("Raven Colonial: the API key was refused; carrier sharing stopped.");
            Fault(CarrierShareFault.KeyRejected());
            return;
        }
        if (write.Error is not null)
        {
            Trace.TraceWarning($"Raven Colonial: could not share a carrier cargo change ({write.Error}).");
            return;
        }

        Trace.TraceInformation($"Raven Colonial: shared {send.Count} carrier commodit{(send.Count == 1 ? "y" : "ies")}.");
        var verdict = _drift.Observe(send, write.Totals);
        if (verdict.IsDrift)
        {
            Trace.TraceWarning($"Raven Colonial: carrier cargo drift ({verdict.Detail}); sharing stopped.");
            Fault(CarrierShareFault.Drift(verdict.Detail));
        }
    }

    /// <summary>What the tracker holds for the carrier, from the cache when fresh; null when it is not registered to the commander or could not be asked. A failed lookup is not cached.</summary>
    private async Task<SharedCarrierLookup?> ResolveAsync(string commander, long marketId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var key = $"{commander}|{marketId}";
        if (_lookups.TryGetValue(key, out var cached) && cached.Expires > now)
            return cached.Lookup.Known ? cached.Lookup : null;

        var found = await _carrier.FindAsync(commander, marketId, ct).ConfigureAwait(false);
        if (found.Error is not null)
        {
            Trace.TraceWarning($"Raven Colonial: could not look up the carrier ({found.Error}); change not shared.");
            return null;
        }

        _lookups[key] = new CachedLookup(found, now + (found.Known ? KnownTtl : UnknownTtl));
        if (!found.Known) Trace.TraceInformation("Raven Colonial: the carrier is not registered to this commander there; change not shared.");
        return found.Known ? found : null;
    }

    /// <summary>Tells the app, and forgets everything learned so that switching sharing back on starts clean.</summary>
    private void Fault(CarrierShareFault fault)
    {
        _drift.Reset();
        _lookups.Clear();
        try { _onFault(fault); }
        catch (Exception ex) { Trace.TraceError("Raven Colonial: reporting a carrier sharing fault failed: " + ex.Message); }
    }

    private void Complete()
    {
        lock (_gate)
        {
            if (_pending > 0 && --_pending == 0) _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetResult();
        return tcs;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _queue.Writer.TryComplete();
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* the worker already ended */ }
    }

    private sealed record Movement(string Commander, long MarketId, IReadOnlyList<CarrierCargoDelta> Deltas);

    private sealed record CachedLookup(SharedCarrierLookup Lookup, DateTimeOffset Expires);
}
