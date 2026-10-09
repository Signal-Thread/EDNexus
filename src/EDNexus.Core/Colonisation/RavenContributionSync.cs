using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using EDNexus.Core.Journal;
using EDNexus.Core.State;

namespace EDNexus.Core.Colonisation;

/// <summary>
/// Pushes the commander's own colonisation deliveries up to the shared project tracker. It listens for
/// the journal's <c>ColonisationContribution</c> event and, when the commander has opted in, reports
/// that one delivery to the project registered against the depot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in and live only.</b> Nothing is sent unless <c>isEnabled</c> says so (read live, so a
/// Settings toggle takes effect on the next delivery), the event is not replayed history, and reporting
/// is not suppressed (developer mode fabricates events that must never leave the machine) — the same
/// gates <see cref="Reporting.EddnBridge"/> and <see cref="Reporting.InaraBridge"/> apply. A commander
/// name and a matched project are required too; with neither there is nothing to attribute or credit.
/// </para>
/// <para>
/// <b>Never in the way of the journal.</b> The bus handler only validates and enqueues. A single
/// background worker does the lookups and the POST, so a slow or dead tracker costs nothing on the
/// pump; the queue is bounded and a delivery that does not fit is dropped (and logged) rather than
/// waited for. There is no retry beyond the one the client makes for a 429/503, and a failure is only
/// traced: re-sending later could double-count a delivery the tracker did receive.
/// </para>
/// <para>
/// <b>Caveats.</b> The tracker's contribute endpoint is unauthenticated — the commander is just a name
/// in the URL — so it cannot tell this from a spoofed delivery. And it adds up whatever it is told, so
/// if another tool (SrvSurvey, for instance) also reports the same deliveries to the same project they
/// are counted twice. Both are why this is off by default.
/// </para>
/// </remarks>
public sealed class RavenContributionSync : IDisposable
{
    /// <summary>How long a matched project is trusted before the tracker is asked again.</summary>
    internal static readonly TimeSpan TargetTtl = TimeSpan.FromMinutes(10);

    /// <summary>How long "this depot has no shared project" is trusted — shorter, so a project registered mid-session is found.</summary>
    internal static readonly TimeSpan NoTargetTtl = TimeSpan.FromMinutes(5);

    private const int QueueCapacity = 32;
    private const int RememberedEvents = 256;

    private readonly CommanderState _state;
    private readonly ISharedProjectContributor _contributor;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isSuppressed;
    private readonly TimeProvider _time;

    private readonly Channel<Delivery> _queue = Channel.CreateBounded<Delivery>(
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

    // Touched only by the worker, so it needs no lock.
    private readonly Dictionary<long, CachedTarget> _targets = new();

    /// <param name="bus">The journal bus to listen on.</param>
    /// <param name="state">Supplies the commander name and the system the depot is in.</param>
    /// <param name="contributor">Resolves the shared project and posts to it.</param>
    /// <param name="isEnabled">Live opt-in. Deliveries are not reported while this returns false.</param>
    /// <param name="isSuppressed">Optional live predicate; while it returns true nothing is sent (developer mode).</param>
    /// <param name="time">Clock for the project cache; the system clock when null.</param>
    public RavenContributionSync(
        JournalEventBus bus,
        CommanderState state,
        ISharedProjectContributor contributor,
        Func<bool> isEnabled,
        Func<bool>? isSuppressed = null,
        TimeProvider? time = null)
    {
        _state = state;
        _contributor = contributor;
        _isEnabled = isEnabled;
        _isSuppressed = isSuppressed ?? (static () => false);
        _time = time ?? TimeProvider.System;

        _worker = Task.Run(() => RunAsync(_cts.Token));
        // The worker catches its own failures; this only keeps the token source alive until it ends.
        _worker.ContinueWith(_ => _cts.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        bus.Subscribe("ColonisationContribution", OnContribution);
    }

    /// <summary>A task that completes once every delivery accepted so far has been dealt with. Test hook.</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate) return _idle.Task;
    }

    private void OnContribution(JournalEntry e)
    {
        // Every early return below is a deliberate "do not report": the journal pump must never wait on us.
        if (e.IsHistorical) return;            // replayed history is never re-reported
        if (!_isEnabled()) return;             // opt-in, read live so toggles take effect
        if (_isSuppressed()) return;           // developer mode: fabricated events stay on this machine

        var commander = _state.Name?.Trim();
        if (string.IsNullOrEmpty(commander)) return;
        var system = _state.StarSystem?.Trim();
        if (string.IsNullOrEmpty(system)) return;
        if (e.GetInt64("MarketID") is not long marketId || marketId <= 0) return;

        var deltas = ReadDeltas(e);
        if (deltas.Count == 0) return;

        // The whole line, timestamp included, is what makes one delivery different from the next.
        var key = e.Raw.GetRawText();

        lock (_gate)
        {
            if (_disposed) return;
            if (!_seen.Add(key)) return;       // the same event twice must not be reported twice
            _seenOrder.Enqueue(key);
            while (_seenOrder.Count > RememberedEvents) _seen.Remove(_seenOrder.Dequeue());

            if (_pending++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (!_queue.Writer.TryWrite(new Delivery(marketId, system, commander, deltas)))
        {
            Trace.TraceWarning("Raven Colonial: delivery not shared (queue full or shutting down).");
            Complete();
        }
    }

    /// <summary>Folds the event into canonical commodity symbol to tons delivered, dropping anything that is not a positive amount.</summary>
    private static Dictionary<string, int> ReadDeltas(JournalEntry e)
    {
        var deltas = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!e.Raw.TryGetProperty("Contributions", out var items) || items.ValueKind != JsonValueKind.Array)
            return deltas;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var symbol = CommodityName.Canonicalize(ReadString(item, "Name") ?? ReadString(item, "Name_Localised"));
            if (symbol.Length == 0) continue;
            if (!item.TryGetProperty("Amount", out var amount)
                || amount.ValueKind != JsonValueKind.Number || !amount.TryGetInt32(out var units) || units <= 0)
                continue;
            deltas[symbol] = deltas.TryGetValue(symbol, out var already) ? already + units : units;
        }
        return deltas;
    }

    private static string? ReadString(JsonElement item, string prop)
        => item.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var delivery in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await ShareAsync(delivery, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Trace.TraceWarning("Raven Colonial: sharing a delivery failed: " + ex.Message); }
                finally { Complete(); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { Trace.TraceError("Raven Colonial: delivery worker stopped: " + ex); }
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

    private async Task ShareAsync(Delivery delivery, CancellationToken ct)
    {
        // Settings can change while a delivery waits in the queue; honour the latest answer.
        if (!_isEnabled() || _isSuppressed()) return;

        var target = await ResolveAsync(delivery, ct).ConfigureAwait(false);
        if (target is null) return;
        if (target.Complete)
        {
            Trace.TraceInformation($"Raven Colonial: '{target.BuildName}' is already complete; delivery not shared.");
            return;
        }

        // Send each commodity under the spelling the project itself uses, and nothing it does not list.
        var send = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (symbol, units) in delivery.Deltas)
        {
            if (target.CommodityNames.TryGetValue(symbol, out var name))
                send[name] = send.TryGetValue(name, out var already) ? already + units : units;
            else
                Trace.TraceInformation($"Raven Colonial: '{target.BuildName}' does not list '{symbol}'; not shared.");
        }
        if (send.Count == 0) return;

        var error = await _contributor.ContributeAsync(target.BuildId, delivery.Commander, send, ct).ConfigureAwait(false);
        if (error is null)
            Trace.TraceInformation($"Raven Colonial: shared {send.Count} commodit{(send.Count == 1 ? "y" : "ies")} to '{target.BuildName}'.");
        else
            Trace.TraceWarning($"Raven Colonial: could not share a delivery to '{target.BuildName}' ({error}).");
    }

    /// <summary>The project for the depot, from the cache when fresh. A lookup that failed is not cached, so the next delivery tries again.</summary>
    private async Task<SharedProjectTarget?> ResolveAsync(Delivery delivery, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_targets.TryGetValue(delivery.MarketId, out var cached) && cached.Expires > now)
            return cached.Target;

        var found = await _contributor.FindTargetAsync(delivery.SystemName, delivery.MarketId, ct).ConfigureAwait(false);
        if (found.Error is not null)
        {
            Trace.TraceWarning($"Raven Colonial: could not look up the shared project ({found.Error}); delivery not shared.");
            return null;
        }

        _targets[delivery.MarketId] = new CachedTarget(found.Target, now + (found.Target is null ? NoTargetTtl : TargetTtl));
        if (found.Target is null)
            Trace.TraceInformation("Raven Colonial: no shared project for this depot; delivery not shared.");
        return found.Target;
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

    private sealed record Delivery(long MarketId, string SystemName, string Commander, IReadOnlyDictionary<string, int> Deltas);

    private sealed record CachedTarget(SharedProjectTarget? Target, DateTimeOffset Expires);
}
