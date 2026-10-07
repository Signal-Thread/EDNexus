using System.ComponentModel;
using EDNexus.Core.State;

namespace EDNexus.Core.Twitch;

/// <summary>
/// Background feature module that mirrors the live commander picture onto the broadcaster's Twitch
/// extension, by publishing <see cref="StreamCardSnapshot"/>s to the EBS. A read-only consumer of
/// <see cref="CommanderState"/> — like every feature module it never mutates state, only
/// <c>StateTracker</c> does that (see AGENTS.md, "one writer").
/// </summary>
/// <remarks>
/// A journal burst (one jump touches system, body, fuel and cargo) far outpaces Twitch's PubSub
/// quota, so changes are coalesced rather than dropped: they mark the card dirty and one pump
/// publishes the newest snapshot per <see cref="MinInterval"/>. A <c>401</c> stops publishing and
/// raises <see cref="ReauthRequired"/> — a revoked grant never recovers by retrying — and resumes
/// once a different token appears.
/// </remarks>
public sealed class TwitchStreamCardService : IDisposable
{
    /// <summary>
    /// Floor between two publishes. The EBS allows one every two seconds; five leaves headroom and is
    /// still faster than a viewer can read the card.
    /// </summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Retries after a failed publish. The pump only wakes on a state change, so without this an EBS
    /// that was not yet listening at startup silences the card for the whole session. Bounded, so one
    /// that is simply down is not polled forever.
    /// </summary>
    public const int MaxPublishRetries = 5;

    /// <summary>
    /// How often an unchanged card is republished while it is on the air. The EBS stops serving a
    /// snapshot it has not heard about for a day (<c>Ebs:ChannelStateMaxAgeHours</c>), so without this
    /// a card that simply has not changed would vanish for new viewers. The EBS refuses to start with
    /// a limit under two of these periods (<c>EbsOptions.MinChannelStateMaxAgeHours</c>); change both
    /// together.
    /// </summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromHours(6);

    /// <summary>Longest <c>Retry-After</c> honoured. The EBS's own windows are a minute at most.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(2);

    /// <summary>Cannot occur in a URL, token or JSON, so no two key part combinations collide.</summary>
    private const string KeySeparator = "\u001f";

    /// <summary>Not every property: <c>LastUpdated</c> ticks on virtually every journal line.</summary>
    private static readonly string[] RelevantProperties =
    {
        nameof(CommanderState.Name),
        nameof(CommanderState.Balance),
        nameof(CommanderState.Ship),
        nameof(CommanderState.ShipName),
        nameof(CommanderState.ShipIdent),
        nameof(CommanderState.StarSystem),
        nameof(CommanderState.Body),
        nameof(CommanderState.Docked),
        nameof(CommanderState.StationName),
        nameof(CommanderState.StationType),
        nameof(CommanderState.CarrierName),
        nameof(CommanderState.CarrierCallsign),
        nameof(CommanderState.CarrierFuel),
        nameof(CommanderState.CarrierJumpRange),
        nameof(CommanderState.CarrierPendingSystem),
        nameof(CommanderState.CarrierPendingDeparture),
        nameof(CommanderState.FuelMain),
        nameof(CommanderState.FuelCapacity),
        nameof(CommanderState.CargoTons),
        nameof(CommanderState.Fsd),
    };

    private readonly CommanderState _state;
    private readonly StreamCardSources _sources;
    private readonly IStreamStateApiClient _client;
    private readonly Func<string> _endpoint;
    private readonly Func<string?> _token;
    private readonly Func<StreamCardVisibility> _visibility;
    private readonly Func<bool> _isSuppressed;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _minInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _dirty = new(0, 1);
    /// <summary>Serialises publishes and clears, so a publish already in flight cannot land after a clear.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Task _pump;

    private readonly TimeSpan _refreshInterval;
    private string? _lastPublishedKey;
    private DateTimeOffset _lastPublishedAt;
    private int _consecutiveFailures;
    private bool _stoppedForReauth;
    /// <summary>The token that earned the 401, so a later login with a different one can resume.</summary>
    private string? _rejectedToken;
    /// <summary>1 once disposed. Read from journal threads and the pump, so never a plain field.</summary>
    private int _disposed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Held while an event is raised and while <see cref="Dispose"/> sets <see cref="_disposed"/>, so
    /// once Dispose returns no handler is running or about to run. Handlers must not block on the
    /// thread that disposes the service (the app's only Post to the UI thread).
    /// </summary>
    private readonly Lock _notifyGate = new();

    /// <summary>Raised after every publish attempt, successful or not, for the UI's status line and logs.</summary>
    public event Action<StreamStatePublishResult>? PublishCompleted;

    /// <summary>
    /// Raised when the EBS rejects the token. Publishing stays stopped until a different token is
    /// offered, so the UI should use this to prompt the commander to log in again.
    /// </summary>
    public event Action? ReauthRequired;

    /// <param name="state">The live commander picture to mirror. Never written to.</param>
    /// <param name="sources">Feature trackers the richer card sections are drawn from.</param>
    /// <param name="client">Transport to the EBS.</param>
    /// <remarks>
    /// The endpoint, token and visibility are callbacks rather than values so signing in, switching
    /// the card off or repointing the EBS takes effect without rebuilding the service; a blank token
    /// simply leaves the pump idle. <paramref name="isSuppressed"/> is wired to developer mode, so
    /// fabricated sample data never reaches a real audience.
    /// </remarks>
    public TwitchStreamCardService(
        CommanderState state,
        StreamCardSources sources,
        IStreamStateApiClient client,
        Func<string> updateStateEndpoint,
        Func<string?> token,
        Func<StreamCardVisibility>? visibility = null,
        Func<bool>? isSuppressed = null)
        : this(state, sources, client, updateStateEndpoint, token, visibility, isSuppressed, DefaultMinInterval, null) { }

    /// <summary>Test-only constructor: a shortened publish interval and/or a controllable clock.</summary>
    internal TwitchStreamCardService(
        CommanderState state,
        StreamCardSources sources,
        IStreamStateApiClient client,
        Func<string> updateStateEndpoint,
        Func<string?> token,
        Func<StreamCardVisibility>? visibility,
        Func<bool>? isSuppressed,
        TimeSpan minInterval,
        Func<DateTimeOffset>? clock,
        TimeSpan? refreshInterval = null)
    {
        _refreshInterval = refreshInterval ?? DefaultRefreshInterval;
        _state = state;
        _sources = sources;
        _client = client;
        _endpoint = updateStateEndpoint;
        _token = token;
        _visibility = visibility ?? (static () => StreamCardVisibility.Default);
        _isSuppressed = isSuppressed ?? (static () => false);
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _minInterval = minInterval;

        _state.PropertyChanged += OnStateChanged;
        _state.CargoChanged += MarkDirty;
        if (_sources.Ranks is { } ranks) ranks.Changed += MarkDirty;
        if (_sources.Exobiology is { } exo) exo.Changed += MarkDirty;
        if (_sources.Mining is { } mining) mining.Changed += MarkDirty;
        if (_sources.Missions is { } missions) missions.Changed += MarkDirty;

        _pump = Task.Run(() => PumpAsync(_cts.Token));

        // No publish here: constructed before the journal replay, so this would make "In the black"
        // the EBS's initial state. The host calls RequestPublish() once the replay is done.
    }

    /// <summary>The floor between two publishes this service was built with.</summary>
    public TimeSpan MinInterval => _minInterval;

    /// <summary>
    /// True once a <c>401</c> has stopped publishing. Clears itself the first time a different token
    /// is offered — i.e. once the commander has logged in again.
    /// </summary>
    public bool StoppedForReauth => Volatile.Read(ref _stoppedForReauth);

    /// <summary>
    /// Publish without waiting for a journal event. Signing in or switching the card on changes what
    /// would be published without changing the commander picture, and with the game closed no journal
    /// event is coming — so the caller has to ask.
    /// </summary>
    public void RequestPublish() => MarkDirty();

    /// <summary>
    /// The snapshot this service would publish right now. Exposed for the settings UI's "what viewers
    /// will see" preview, and for diagnostics — computing one has no side effects.
    /// </summary>
    /// <param name="visibility">
    /// Sections to map against, for previewing a choice the commander has not saved yet. Defaults to
    /// the visibility currently in force.
    /// </param>
    public StreamCardSnapshot Preview(StreamCardVisibility? visibility = null) =>
        StreamCardMapper.Map(_state, _sources, visibility ?? _visibility(), _clock());

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && Array.IndexOf(RelevantProperties, name) >= 0) MarkDirty();
    }

    /// <summary>
    /// Marks the card as needing a republish. Called from journal-processing threads, so it does no
    /// work beyond releasing the pump's signal — never any I/O, and never a lock the bus could contend.
    /// </summary>
    private void MarkDirty()
    {
        // A service stopped for reauth stays asleep until the commander logs in again — at which
        // point the token they are publishing with is a different one, and the pump can resume.
        if (IsDisposed || (StoppedForReauth && _token() == Volatile.Read(ref _rejectedToken))) return;
        try { _dirty.Release(); }
        catch (SemaphoreFullException) { /* already dirty — the pump will pick up the newest state */ }
        catch (ObjectDisposedException) { /* raced with Dispose */ }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool changed;
            try { changed = await _dirty.WaitAsync(_refreshInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            // A quiet interval is a refresh, which only applies once something is on the air: before
            // the host has replayed the journal there is nothing that should go out.
            if (!changed && _lastPublishedKey is null) continue;

            var backoff = _minInterval;
            var retry = false;
            try
            {
                var published = await PublishIfChangedAsync(ct).ConfigureAwait(false);
                if (published is { Status: StreamStatePublishStatus.RateLimited or StreamStatePublishStatus.Failed })
                {
                    // Give a struggling EBS (or a tighter-than-expected rate limit) room to recover
                    // rather than retrying at the floor interval.
                    backoff = _minInterval * 2;
                    // The EBS says exactly how long its window has left; retrying sooner is a wasted 429.
                    // Capped, so a misconfigured proxy's header cannot silence the card for the session.
                    if (published.RetryAfter is { } wait && wait > backoff)
                        backoff = wait < MaxRetryAfter ? wait : MaxRetryAfter;
                    retry = ++_consecutiveFailures <= MaxPublishRetries;
                }
                else if (published is not null)
                {
                    _consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) when (IsDisposed) { return; }
            catch (Exception ex)
            {
                // Best-effort telemetry: never let a card failure take down the engine's task.
                Raise(new StreamStatePublishResult(StreamStatePublishStatus.Failed, ex.Message));
                retry = ++_consecutiveFailures <= MaxPublishRetries;
            }

            // Rate-limit floor. Any change arriving during this wait has already re-armed the signal,
            // so the next iteration publishes the newest state immediately after it elapses.
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // Nothing else will wake the pump for a retry: MarkDirty only fires on journal activity,
            // and a commander configuring this typically has the game closed.
            if (retry) MarkDirty();
        }
    }

    /// <summary>Returns the publish result, or null when there was nothing to send.</summary>
    private async Task<StreamStatePublishResult?> PublishIfChangedAsync(CancellationToken ct)
    {
        if (_isSuppressed()) return null;

        var token = _token();
        if (string.IsNullOrWhiteSpace(token)) return null;

        if (StoppedForReauth)
        {
            if (token == Volatile.Read(ref _rejectedToken)) return null;
            // A fresh login: clear the latch and give the new credential a try.
            Volatile.Write(ref _rejectedToken, null);
            Volatile.Write(ref _stoppedForReauth, false);
        }

        var snapshot = StreamCardMapper.Map(_state, _sources, _visibility(), _clock());

        // Keyed on destination and credential too: an unchanged card still has to reach a different
        // EBS, or the same one after a restart wiped its in-memory tokens.
        var endpoint = _endpoint();
        var key = string.Join(KeySeparator, endpoint, token, snapshot.ContentFingerprint());
        if (key == _lastPublishedKey && _clock() - _lastPublishedAt < _refreshInterval) return null;

        StreamStatePublishResult result;
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The card may have been switched off while the snapshot was being built; publishing
            // now would put it back on the air straight after the clear.
            if (_token() != token) return null;
            result = await _client.PublishAsync(endpoint, token!, snapshot, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _lastPublishedKey = key;
                _lastPublishedAt = _clock();
            }
        }
        finally { _sendGate.Release(); }

        if (result.RequiresReauth)
        {
            Volatile.Write(ref _rejectedToken, token);
            Volatile.Write(ref _stoppedForReauth, true);
            lock (_notifyGate)
            {
                if (!IsDisposed)
                    try { ReauthRequired?.Invoke(); } catch { /* never let a handler break the pump */ }
            }
        }

        Raise(result);
        return result;
    }

    /// <summary>
    /// Takes the card off the air: the EBS forgets the last snapshot and tells viewers to hide it.
    /// Call it when the commander switches the card off or points it at a different EBS — merely
    /// stopping publishes would leave the last snapshot being served to every new viewer.
    /// </summary>
    /// <param name="updateStateEndpoint">The EBS the card was published to.</param>
    /// <param name="token">The token it was published with (the card's token callback may already return null).</param>
    public async Task<StreamStatePublishResult> TakeOffAirAsync(
        string updateStateEndpoint, string token, CancellationToken ct = default)
    {
        StreamStatePublishResult result;
        try
        {
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                result = await _client.ClearAsync(updateStateEndpoint, token, ct).ConfigureAwait(false);
                // Switching back on must republish even if the commander picture has not changed.
                _lastPublishedKey = null;
            }
            finally { _sendGate.Release(); }
        }
        catch (ObjectDisposedException)
        {
            result = new StreamStatePublishResult(StreamStatePublishStatus.Failed, "The stream card service has shut down.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new StreamStatePublishResult(StreamStatePublishStatus.Failed, ex.Message);
        }

        Raise(result);
        return result;
    }

    private void Raise(StreamStatePublishResult result)
    {
        // A publish that finishes after Dispose reports to a UI that has already let go of us. The
        // check and the call share the gate with Dispose, so Dispose cannot land between them.
        lock (_notifyGate)
        {
            if (IsDisposed) return;
            try { PublishCompleted?.Invoke(result); } catch { /* never let a handler break the pump */ }
        }
    }

    /// <summary>The pump's own task, so tests can see it finish cleanly after <see cref="Dispose"/>.</summary>
    internal Task Completion => _pump;

    public void Dispose()
    {
        lock (_notifyGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        }

        _state.PropertyChanged -= OnStateChanged;
        _state.CargoChanged -= MarkDirty;
        if (_sources.Ranks is { } ranks) ranks.Changed -= MarkDirty;
        if (_sources.Exobiology is { } exo) exo.Changed -= MarkDirty;
        if (_sources.Mining is { } mining) mining.Changed -= MarkDirty;
        if (_sources.Missions is { } missions) missions.Changed -= MarkDirty;

        _cts.Cancel();
        try { _pump.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* cancellation */ }

        // A publish can still be in flight past the wait (an EBS that is slow to answer): the pump
        // releases _sendGate and reads the token when it returns, so tear down only once it has.
        if (_pump.IsCompleted) DisposeSynchronization();
        else _pump.ContinueWith(_ => DisposeSynchronization(), TaskScheduler.Default);
    }

    private void DisposeSynchronization()
    {
        _cts.Dispose();
        _dirty.Dispose();
        _sendGate.Dispose();
    }
}
