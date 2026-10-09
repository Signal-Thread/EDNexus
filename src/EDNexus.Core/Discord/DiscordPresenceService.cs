using System.ComponentModel;
using EDNexus.Core.State;

namespace EDNexus.Core.Discord;

/// <summary>
/// Background feature module that mirrors <see cref="CommanderState"/> onto Discord Rich Presence.
/// A read-only consumer of <see cref="CommanderState"/> — like every feature module it never mutates
/// state, only <see cref="StateTracker"/> does that (see AGENTS.md, "one writer").
/// </summary>
/// <remarks>
/// Discord asks integrations not to push more than one presence update in a short window. Rather than
/// drop updates that land inside that window, changes are coalesced: the latest computed payload is
/// remembered and, if the throttle is currently closed, a single delayed send is scheduled for the
/// moment it reopens — so the commander's presence always catches up to the newest state without ever
/// exceeding the rate limit.
/// </remarks>
public sealed class DiscordPresenceService : IDisposable
{
    /// <summary>
    /// EDNexus's own Discord application (Client) ID, registered at
    /// https://discord.com/developers/applications. Rich Presence images (<c>ednexus_logo</c>,
    /// <c>docked</c>, <c>cruising</c>, ship keys) only render once art assets with those keys are uploaded
    /// to that application; presence works without them, just without pictures.
    /// </summary>
    public const string DefaultApplicationId = "1557985945393827911";

    /// <summary>
    /// The all-zeros placeholder earlier builds shipped as the default. It is not a real application, so
    /// Discord rejects it and no presence is ever shown; it was also saved into every commander's
    /// <c>settings.json</c>, so it must be recognised and replaced rather than honoured.
    /// </summary>
    public const string PlaceholderApplicationId = "0000000000000000000";

    /// <summary>
    /// The application id to connect with: the commander's override when they set a real one, otherwise
    /// <see cref="DefaultApplicationId"/>. A blank value or the old placeholder (persisted by earlier
    /// builds) counts as "not set".
    /// </summary>
    public static string ResolveApplicationId(string? configured)
    {
        var id = configured?.Trim();
        return string.IsNullOrEmpty(id) || id == PlaceholderApplicationId ? DefaultApplicationId : id;
    }

    /// <summary>Discord's own guidance: don't push presence updates more than once every 15 seconds.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(15);

    private static readonly string[] RelevantProperties =
    {
        nameof(CommanderState.StarSystem),
        nameof(CommanderState.Body),
        nameof(CommanderState.Docked),
        nameof(CommanderState.StationName),
        nameof(CommanderState.CarrierName),
        nameof(CommanderState.Ship),
        nameof(CommanderState.ShipIdent),
        nameof(CommanderState.CargoTons),
        nameof(CommanderState.Name),
    };

    private readonly CommanderState _state;
    private readonly IDiscordRpcClient _client;
    private readonly PresenceThrottle _throttle;
    private readonly Func<bool> _isSuppressed;
    private readonly Func<DateTimeOffset> _clock;
    private readonly DateTimeOffset _sessionStartedAt;
    private readonly object _gate = new();

    private string? _lastSystem;
    private DateTimeOffset _systemEnteredAt;
    private DiscordPresencePayload? _lastComputed;
    private DiscordPrivacyOptions _privacy;
    private bool _clearedForSuppression;
    private CancellationTokenSource? _pending;
    private bool _disposed;

    /// <param name="state">The live commander picture to mirror. Never written to.</param>
    /// <param name="client">
    /// The Discord transport. Pass a <see cref="DiscordRpcClientAdapter"/> for the real integration or
    /// <see cref="NoOpDiscordRpcClient"/> to disable it outright (still safe to construct either way).
    /// </param>
    /// <param name="isSuppressed">
    /// Optional live predicate; while it returns true no presence is computed or sent, and the last
    /// real presence is cleared. Wired to developer mode so fabricated sample data never reaches a
    /// commander's real Discord profile. Call <see cref="Refresh"/> when it flips.
    /// </param>
    /// <param name="privacy">
    /// The commander's initial privacy choices (everything visible when omitted). Change them live
    /// with <see cref="UpdatePrivacy"/>.
    /// </param>
    public DiscordPresenceService(
        CommanderState state, IDiscordRpcClient client, Func<bool>? isSuppressed = null,
        DiscordPrivacyOptions? privacy = null)
        : this(state, client, DefaultMinInterval, isSuppressed, null, privacy) { }

    /// <summary>Test-only constructor: a shortened throttle window and/or a controllable clock.</summary>
    internal DiscordPresenceService(
        CommanderState state, IDiscordRpcClient client, TimeSpan minInterval,
        Func<bool>? isSuppressed = null, Func<DateTimeOffset>? clock = null,
        DiscordPrivacyOptions? privacy = null)
    {
        _state = state;
        _client = client;
        _privacy = privacy ?? DiscordPrivacyOptions.Default;
        _isSuppressed = isSuppressed ?? (static () => false);
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _throttle = new PresenceThrottle(minInterval, _clock);

        _sessionStartedAt = _clock();
        _systemEnteredAt = _sessionStartedAt;
        _lastSystem = state.StarSystem;

        // Never let a failed/unsupported connection surface as an exception during construction.
        try { _client.TryInitialize(); } catch { /* graceful fallback */ }

        _state.PropertyChanged += OnStateChanged;

        // Push whatever the state already knows (e.g. warmed from a replayed journal) immediately.
        RequestUpdate();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not { } name || Array.IndexOf(RelevantProperties, name) < 0) return;

        if (name == nameof(CommanderState.StarSystem) && !string.Equals(_state.StarSystem, _lastSystem, StringComparison.OrdinalIgnoreCase))
        {
            _lastSystem = _state.StarSystem;
            _systemEnteredAt = _clock();
        }

        RequestUpdate();
    }

    /// <summary>The privacy choices currently applied to the outgoing presence.</summary>
    public DiscordPrivacyOptions Privacy
    {
        get { lock (_gate) return _privacy; }
    }

    /// <summary>
    /// Apply new privacy choices to the live presence. A change is pushed straight away rather than
    /// waiting out the throttle window: this is a rare, user-initiated action, and a commander who has
    /// just hidden their system must not keep broadcasting it for up to another 15 seconds.
    /// </summary>
    /// <remarks>
    /// The new choices are stored and the presence recomputed under one lock, so a delayed send that
    /// was already queued can never go out carrying the old (less private) payload. While suppressed,
    /// the presence is cleared instead.
    /// </remarks>
    public void UpdatePrivacy(DiscordPrivacyOptions privacy)
    {
        lock (_gate)
        {
            if (_disposed || _privacy == privacy) return;
            _privacy = privacy;
            RequestUpdateLocked(immediate: true, forceClearIfSuppressed: true);
        }
    }

    /// <summary>
    /// Re-evaluate the suppression predicate now. Call when it may have flipped on (developer mode
    /// switched on) so the last real presence is cleared straight away rather than on the next state
    /// change.
    /// </summary>
    /// <remarks>
    /// If suppression has since lifted, this pushes whatever <see cref="CommanderState"/> currently
    /// holds — which, on the same engine, may still be fabricated. Restoring the real presence after
    /// developer mode is the app's job: it rebuilds the engine (a fresh service re-warmed from the
    /// journal) rather than calling this.
    /// </remarks>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            RequestUpdateLocked(immediate: false, forceClearIfSuppressed: false);
        }
    }

    private void RequestUpdate()
    {
        lock (_gate)
        {
            if (_disposed) return;
            RequestUpdateLocked(immediate: false, forceClearIfSuppressed: false);
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void RequestUpdateLocked(bool immediate, bool forceClearIfSuppressed)
    {
        if (_isSuppressed())
        {
            // The last real presence must not linger while suppressed (e.g. developer mode): clear it
            // once on entering suppression, and forget it so the real state is re-pushed on leaving.
            if (!_clearedForSuppression || forceClearIfSuppressed)
            {
                _pending?.Cancel();
                _lastComputed = null;
                _clearedForSuppression = true;
                try { _client.Clear(); } catch { /* graceful fallback: never throw */ }
            }
            return;
        }
        _clearedForSuppression = false;

        var payload = DiscordPresenceMapper.Map(_state, _sessionStartedAt, _systemEnteredAt, _privacy);
        if (_lastComputed is not null && payload.Equals(_lastComputed)) return;
        _lastComputed = payload;

        if (immediate)
        {
            // Supersede any trailing send that would otherwise re-push an older payload later.
            _pending?.Cancel();
            _throttle.MarkSent();
            Send(payload);
            return;
        }

        if (_throttle.TryAcquire())
        {
            Send(payload);
            return;
        }

        SchedulePendingLocked(_throttle.TimeUntilNextSend());
    }

    private void Send(DiscordPresencePayload payload)
    {
        try { _client.SetPresence(payload); } catch { /* graceful fallback: never throw */ }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void SchedulePendingLocked(TimeSpan delay)
    {
        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }

            lock (_gate)
            {
                if (_disposed || cts.IsCancellationRequested || _isSuppressed()) return;
                if (_lastComputed is { } latest && _throttle.TryAcquire()) Send(latest);
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Cancel();
        }

        _state.PropertyChanged -= OnStateChanged;
        try { _client.Clear(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
