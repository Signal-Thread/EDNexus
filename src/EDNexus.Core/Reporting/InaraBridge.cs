using System.Text.Json;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EliteDangerous.Inara;

namespace EDNexus.Core.Reporting;

/// <summary>
/// Connects the journal event bus to the reusable <see cref="EliteDangerous.Inara"/> client, honouring
/// Inara's "don't spam us" guidance: events are accumulated into a batch and the batch is only sent on
/// the moments that matter — <b>session start</b> (LoadGame), <b>docking</b>, <b>FSD jump</b>, and
/// <b>session end</b> (Shutdown / dispose) — and never more often than a cooldown. It captures commander
/// identity from historical replay too, but only ever transmits live events, and only while enabled.
/// </summary>
public sealed class InaraBridge : IAsyncDisposable
{
    private static readonly TimeSpan SessionStartDebounce = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromMinutes(1);

    // A batch that hits transport/5xx errors is re-queued with exponential backoff up to this many times,
    // then dropped; the pending list is also capped so a long outage cannot grow it without bound.
    private const int MaxRetries = 3;
    private const int MaxPending = 200;

    private readonly AppSettings _settings;
    private readonly InaraClient _client;
    private readonly Func<bool> _isSuppressed;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _minInterval;
    private readonly TimeSpan _retryBackoff;
    private readonly IReportingLog? _log;
    private readonly object _gate = new();

    // Captured identity (from any event, incl. historical) so we can attribute live sends.
    private string? _commander;
    private string? _frontierId;
    private string? _shipType;
    private long? _shipId;
    private string? _shipName;
    private string? _shipIdent;
    private string? _system;

    private readonly Dictionary<string, int> _rankValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _rankProgress = new(StringComparer.Ordinal);

    // The pending batch (events observed since the last flush). Set-events are deduped by name;
    // travel events accumulate.
    private readonly List<InaraEvent> _pending = new();

    private Task _tail = Task.CompletedTask;
    private CancellationTokenSource? _debounce;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
    private bool _stopped;   // set on a hard error (e.g. bad API key); cleared when disabled again
    private bool _disposing; // final flush in progress: no throttle wait, no re-queue
    private int _failures;   // consecutive transient send failures of the current batch

    public InaraBridge(JournalEventBus bus, AppSettings settings, InaraClient client, Func<bool>? isSuppressed = null, IReportingLog? log = null)
        : this(bus, settings, client, SessionStartDebounce, MinInterval, isSuppressed, log) { }

    /// <summary>Test-only constructor allowing the debounce/throttle windows to be shortened.</summary>
    internal InaraBridge(JournalEventBus bus, AppSettings settings, InaraClient client, TimeSpan debounceDelay, TimeSpan minInterval, Func<bool>? isSuppressed = null, IReportingLog? log = null, TimeSpan? retryBackoff = null)
    {
        _retryBackoff = retryBackoff ?? RetryBackoff;
        _settings = settings;
        _client = client;
        _isSuppressed = isSuppressed ?? (static () => false);
        _debounceDelay = debounceDelay;
        _minInterval = minInterval;
        _log = log;

        bus.Subscribe("Commander", e => Capture(() => _commander = e.GetString("Name") ?? _commander));
        bus.Subscribe("LoadGame", OnLoadGame);
        bus.Subscribe("Loadout", OnLoadout);
        bus.Subscribe("Rank", OnRank);
        bus.Subscribe("Progress", OnProgress);
        bus.Subscribe("Reputation", OnReputation);
        bus.Subscribe("Location", e => Capture(() => TrackLocation(e)));
        bus.Subscribe("CarrierJump", e => Capture(() => TrackLocation(e)));
        bus.Subscribe("Docked", OnDocked);
        bus.Subscribe("FSDJump", OnFsdJump);
        // Only a live Shutdown ends a session worth reporting. A replayed one (EDNexus opened after the game
        // was closed) must not push the last session snapshot to Inara.
        bus.Subscribe("Shutdown", e => { if (!e.IsHistorical) FlushImmediate(urgent: true); });
    }

    private bool Enabled => _settings.Reporting.Inara.Enabled
                            && !string.IsNullOrWhiteSpace(_settings.Reporting.Inara.ApiKey);

    // --- Identity / stat capture (accumulate; no send on its own). ---

    private void OnLoadGame(JournalEntry e) => Capture(() =>
    {
        _commander = e.GetString("Commander") ?? _commander;
        _frontierId = e.GetString("FID") ?? _frontierId;
        TrackLocation(e);
        if (e.IsHistorical) return;   // identity only: replayed history is never reported
        if (e.GetInt64("Credits") is long credits)
            AddOrReplaceSet(InaraEvent.SetCommanderCredits(e.Timestamp, credits, e.GetInt64("Loan")));

        // Session start: give the immediately-following Rank/Reputation/Loadout events a moment to
        // land, then send the assembled snapshot.
        ScheduleFlush(_debounceDelay);
    });

    private void OnLoadout(JournalEntry e) => Capture(() =>
    {
        _shipType = e.GetString("Ship") ?? _shipType;
        _shipId = e.GetInt64("ShipID") ?? _shipId;
        _shipName = e.GetString("ShipName") ?? _shipName;
        _shipIdent = e.GetString("ShipIdent") ?? _shipIdent;
        if (_shipType is not null && !e.IsHistorical)
            AddOrReplaceSet(InaraEvent.SetCommanderShip(e.Timestamp, _shipType, _shipId, _shipName, _shipIdent));
    });

    private void OnRank(JournalEntry e) => Capture(() =>
    {
        foreach (var name in RankNames)
            if (e.GetInt64(name) is long v) _rankValues[name.ToLowerInvariant()] = (int)v;
        if (!e.IsHistorical) AddRanks(e.Timestamp);
    });

    private void OnProgress(JournalEntry e) => Capture(() =>
    {
        foreach (var name in RankNames)
            if (e.GetInt64(name) is long p) _rankProgress[name.ToLowerInvariant()] = p / 100.0;
        if (!e.IsHistorical) AddRanks(e.Timestamp);
    });

    private void OnReputation(JournalEntry e) => Capture(() =>
    {
        if (e.IsHistorical) return;
        var factions = new List<(string, double)>();
        foreach (var f in new[] { "Empire", "Federation", "Alliance", "Independent" })
            if (e.GetDouble(f) is double rep) factions.Add((f.ToLowerInvariant(), rep));
        if (factions.Count > 0)
            AddOrReplaceSet(InaraEvent.SetCommanderReputationMajorFaction(e.Timestamp, factions));
    });

    // --- Flush triggers. ---

    private void OnDocked(JournalEntry e) => Capture(() =>
    {
        TrackLocation(e);
        if (!e.IsHistorical && _system is not null && e.GetString("StationName") is string station)
        {
            _pending.Add(InaraEvent.AddCommanderTravelDock(e.Timestamp, _system, station, e.GetInt64("MarketID"), _shipType));
            _pending.Add(InaraEvent.SetCommanderTravelLocation(e.Timestamp, _system, station, e.GetInt64("MarketID")));
            FlushImmediate();
        }
    });

    private void OnFsdJump(JournalEntry e) => Capture(() =>
    {
        TrackLocation(e);
        if (!e.IsHistorical && _system is not null)
        {
            _pending.Add(InaraEvent.AddCommanderTravelFSDJump(e.Timestamp, _system, e.GetDouble("JumpDist") ?? 0, _shipType));
            _pending.Add(InaraEvent.SetCommanderTravelLocation(e.Timestamp, _system));
            FlushImmediate();
        }
    });

    // --- Batch plumbing. ---

    private void TrackLocation(JournalEntry e) => _system = e.GetString("StarSystem") ?? _system;

    private void AddRanks(DateTimeOffset ts)
    {
        if (_rankValues.Count == 0) return;
        var ranks = _rankValues.Select(kv => (kv.Key, kv.Value, _rankProgress.GetValueOrDefault(kv.Key)));
        AddOrReplaceSet(InaraEvent.SetCommanderRankPilot(ts, ranks));
    }

    /// <summary>Adds a "set" event, replacing any earlier one of the same name so we never send stale dupes.</summary>
    private void AddOrReplaceSet(InaraEvent ev)
    {
        _pending.RemoveAll(p => p.EventName == ev.EventName);
        _pending.Add(ev);
    }

    private void Capture(Action mutate)
    {
        // While developer mode fabricates events, don't capture or batch them — this keeps synthetic
        // data out of the Inara profile. Real capture/sends resume once dev mode is switched off.
        if (_isSuppressed()) return;
        lock (_gate) mutate();
    }

    private void ScheduleFlush(TimeSpan delay)
    {
        _debounce?.Cancel();
        var cts = new CancellationTokenSource();
        _debounce = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
            FlushNow();
        });
    }

    private void FlushImmediate(bool urgent = false)
    {
        _debounce?.Cancel();
        FlushNow(urgent);
    }

    /// <summary>Snapshots the batch and chains an async send. Runs whatever thread triggered it.</summary>
    private void FlushNow(bool urgent = false)
    {
        if (_isSuppressed()) return;   // dev mode active — the Shutdown/debounce paths must not send either

        InaraIdentity? identity;
        List<InaraEvent> batch;
        lock (_gate)
        {
            if (!Enabled) { _stopped = false; return; }  // disabling clears any prior hard-error stop
            if (_stopped || _pending.Count == 0 || _commander is null) return;

            identity = new InaraIdentity
            {
                ApiKey = _settings.Reporting.Inara.ApiKey,
                CommanderName = _commander,
                CommanderFrontierID = _frontierId,
            };
            batch = new List<InaraEvent>(_pending);
            _pending.Clear();

            var prev = _tail;
            _tail = SendAsync(prev, identity, batch, urgent);
        }
    }

    private async Task SendAsync(Task previous, InaraIdentity identity, List<InaraEvent> batch, bool urgent)
    {
        try { await previous.ConfigureAwait(false); } catch { }

        // "As requested by the site": never exceed the minimum interval between sends. A session-end
        // flush skips the wait: there may be no later chance to send, and shutdown must not stall on it.
        var since = DateTimeOffset.UtcNow - _lastFlush;
        if (since < _minInterval && !urgent && !_disposing)
        {
            try { await Task.Delay(_minInterval - since).ConfigureAwait(false); }
            catch { }
        }

        var response = await _client.SendAsync(identity, batch).ConfigureAwait(false);
        _lastFlush = DateTimeOffset.UtcNow;

        LogUpload(batch, response);

        if (response.IsHardError)
        {
            lock (_gate) _stopped = true;   // e.g. invalid API key — stop until re-enabled
            return;
        }

        if (response.IsTransient)
            RequeueOrDrop(batch, response);
        else
            lock (_gate) _failures = 0;
    }

    /// <summary>
    /// A transport / 5xx failure means Inara never recorded the batch. Put it back at the front of the
    /// pending list (set-events superseded by newer ones are collapsed) and retry after a backoff, up to
    /// <see cref="MaxRetries"/> times; after that the batch is dropped so a long outage cannot pile up.
    /// </summary>
    private void RequeueOrDrop(List<InaraEvent> batch, InaraResponse response)
    {
        TimeSpan delay;
        lock (_gate)
        {
            if (_disposing || ++_failures > MaxRetries)
            {
                _failures = 0;
                return;
            }

            _pending.InsertRange(0, batch);
            CollapseAndCap();
            delay = _retryBackoff * Math.Pow(2, _failures - 1);
            if (response.RetryAfter is { } wait && wait > delay) delay = wait;
            if (delay > TimeSpan.FromMinutes(10)) delay = TimeSpan.FromMinutes(10);
        }
        ScheduleFlush(delay);
    }

    /// <summary>Keeps only the newest of each "set" event and bounds the list. Caller holds <see cref="_gate"/>.</summary>
    private void CollapseAndCap()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var name = _pending[i].EventName;
            if (name.StartsWith("set", StringComparison.Ordinal) && !seen.Add(name))
                _pending.RemoveAt(i);
        }
        if (_pending.Count > MaxPending)
            _pending.RemoveRange(0, _pending.Count - MaxPending);
    }

    /// <summary>Record the batch we just sent (and Inara's verdict) to the reporting log, if attached.</summary>
    private void LogUpload(List<InaraEvent> batch, InaraResponse response)
    {
        if (_log is null) return;

        var summary = $"{batch.Count} event(s): {string.Join(", ", batch.Select(e => e.EventName))}";
        var status = response.Status == 0
            ? $"transport error: {response.StatusText}"
            : $"{response.Status} {response.StatusText}".TrimEnd();

        // Payload logging serialises only the events (eventName/eventData) — never the request header,
        // which carries the commander's Inara API key.
        var payload = _settings.Reporting.LogPayloads ? SerializeEvents(batch) : null;

        _log.Record(new ReportingUpload(
            DateTimeOffset.UtcNow, ReportingTarget.Inara, summary, response.IsOk, status,
            Error: response.IsOk ? null : (response.StatusText ?? "error"),
            Payload: payload));
    }

    private static string? SerializeEvents(List<InaraEvent> batch)
    {
        try
        {
            return JsonSerializer.Serialize(batch.Select(e => new
            {
                eventName = e.EventName,
                eventTimestamp = e.EventTimestamp.UtcDateTime,
                eventData = e.EventData,
            }));
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        FlushImmediate(urgent: true);
        Task tail;
        lock (_gate) tail = _tail;
        try { await tail.ConfigureAwait(false); } catch { }
        _client.Dispose();
    }

    private static readonly string[] RankNames =
        { "Combat", "Trade", "Explore", "Soldier", "Exobiologist", "Empire", "Federation", "CQC" };
}
