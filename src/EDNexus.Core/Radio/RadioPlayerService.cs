using EDNexus.Core.Settings;
using LibVLCSharp.Shared;

namespace EDNexus.Core.Radio;

/// <summary>
/// Background audio playback for the built-in radio stations (<see cref="RadioStationCatalog"/>),
/// backed by LibVLC. Owns its own derived state (current station, playback status, volume, mute) and
/// — when constructed with a <see cref="SettingsStore"/> — persists user choices to
/// <c>settings.json</c> as they change and restores them on <see cref="RestoreAsync"/>.
/// </summary>
/// <remarks>
/// All playback operations (initializing the engine, loading media, changing volume/mute) run on a
/// background thread via <see cref="Task.Run(Action)"/> so a caller on the Avalonia UI thread never
/// blocks on the network or on native LibVLC calls. LibVLC itself is loaded lazily on first use so
/// constructing this service (e.g. for the CLI harness, which never touches audio) has no cost and
/// cannot throw even if no native VLC runtime is present on the machine — a missing/broken native
/// library surfaces as <see cref="RadioPlaybackStatus.Error"/> instead of a crash.
/// </remarks>
public sealed class RadioPlayerService : IRadioPlayer, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly AppSettings? _settings;
    private readonly SettingsStore? _store;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private RadioStation? _station;
    private RadioPlaybackStatus _status = RadioPlaybackStatus.Stopped;
    private int _volume = 50;
    private bool _muted;
    private bool _enabled;
    private bool _wantsPlayback; // user intent: last transport action was play (vs pause/stop)
    private string? _lastError;
    private bool _disposed;
    private bool _suspended; // audio stopped for developer mode; resume on leaving it
    private int _pendingPlays; // plays queued but not yet through PlayStationCore (status still stale)

    // Serializes every call into the native player (play, pause, stop, volume, mute, teardown), so a
    // play that was already queued can't slip in after a suspend and the player is never freed while
    // another call is using it. LibVLC event handlers never take it (they only take _gate).
    //
    // #144: LibVLC raises its events synchronously on its own threads, and Stop() joins those
    // threads. Our handlers take _gate, so _gate must never be held across a native call; callers
    // snapshot what they need under _gate, release it, and only then call into LibVLC.
    private readonly object _transportGate = new();

    // How long Dispose waits for LibVLC to stop and release before giving up on it: a wedged native
    // teardown must not hang app exit.
    private static readonly TimeSpan DefaultNativeTeardownTimeout = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _nativeTeardownTimeout;

    // Bringing LibVLC up (loading the native library, scanning its plugins) can take many seconds on a
    // cold start and can stall outright (antivirus, a damaged install, a slow disk). It runs on a
    // worker with a bound so the play request fails with a message instead of hanging, and it never
    // runs under _gate: the UI thread reads Snapshot under _gate on every refresh tick, so holding it
    // across the start-up froze the whole window.
    private static readonly TimeSpan DefaultEngineStartTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _engineStartTimeout;
    private readonly Func<RadioEngineHandle> _engineFactory;
    private Task<RadioEngineHandle>? _engineInit; // guarded by _gate; reused if a start outlives its timeout

    // A station that never connects would otherwise sit in "Buffering" forever.
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _connectTimeout;
    private int _playAttempt; // guarded by _gate; lets a stale connect watchdog recognise it was superseded

    // Volume changes arrive in bursts (a slider drag fires one per step). They apply to the player
    // and the in-memory settings immediately, but the disk write is coalesced: one save once the
    // burst goes quiet, flushed on Dispose so a change made just before shutdown isn't lost.
    private static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _saveDelay;
    private readonly object _saveGate = new();
    private readonly Action<Action> _postSave;
    private Timer? _saveTimer;
    private bool _savePending;

    /// <summary>Raised after any playback state, station, volume, or mute change.</summary>
    public event Action? Changed;

    /// <param name="settings">
    /// When supplied, seeds the initial station/volume/mute/enabled state and every subsequent
    /// change is persisted back into it (and saved via <paramref name="store"/>, if present).
    /// </param>
    /// <param name="store">Used to write <paramref name="settings"/> to disk after each change.</param>
    /// <param name="saveDelay">
    /// How long volume changes must go quiet before they're written to disk (default 500 ms).
    /// Every other change is saved immediately.
    /// </param>
    /// <param name="postSave">
    /// Runs the delayed volume save on the thread that owns <paramref name="settings"/>. The settings
    /// object is shared with the rest of the app, which mutates it on the UI thread, so serializing it
    /// from the timer's thread pool thread could race those edits. Defaults to posting to the
    /// <see cref="SynchronizationContext"/> current at construction, or running inline when there is
    /// none (tests, CLI).
    /// </param>
    public RadioPlayerService(
        AppSettings? settings = null,
        SettingsStore? store = null,
        TimeSpan? saveDelay = null,
        Action<Action>? postSave = null)
        : this(settings, store, saveDelay, postSave, DefaultNativeTeardownTimeout)
    {
    }

    /// <summary>Test seam: as the public constructor, with a custom bound on the native teardown in <see cref="Dispose"/>.</summary>
    internal RadioPlayerService(
        AppSettings? settings,
        SettingsStore? store,
        TimeSpan? saveDelay,
        Action<Action>? postSave,
        TimeSpan nativeTeardownTimeout,
        Func<RadioEngineHandle>? engineFactory = null,
        TimeSpan? engineStartTimeout = null,
        TimeSpan? connectTimeout = null)
    {
        _engineFactory = engineFactory ?? CreateNativeEngine;
        _engineStartTimeout = engineStartTimeout ?? DefaultEngineStartTimeout;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _settings = settings;
        _store = store;
        _saveDelay = saveDelay ?? DefaultSaveDelay;
        _nativeTeardownTimeout = nativeTeardownTimeout;
        _postSave = postSave ?? PostTo(SynchronizationContext.Current);

        var radio = settings?.Radio;
        if (radio is not null)
        {
            _enabled = radio.RadioEnabled;
            _volume = Math.Clamp(radio.RadioVolume, 0, 100);
            _muted = radio.RadioMute;
            _station = RadioStationCatalog.Find(radio.RadioLastStation);
            _wantsPlayback = radio.RadioWasPlaying;
        }
    }

    /// <summary>
    /// Whether persisted <paramref name="radio"/> settings ask for playback to resume at launch: the
    /// radio must be enabled, a known station tuned, and it must have been playing (not paused or
    /// stopped) when the app last closed.
    /// </summary>
    public static bool ShouldResumeOnLaunch(RadioSettings? radio)
        => radio is { RadioEnabled: true, RadioWasPlaying: true }
           && RadioStationCatalog.Find(radio.RadioLastStation) is not null;

    /// <summary>A point-in-time snapshot of everything the UI needs to render the player.</summary>
    public RadioPlayerSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new RadioPlayerSnapshot(_enabled, _station, _status, _volume, _muted, _lastError);
        }
    }

    /// <summary>
    /// Resumes whatever was persisted from a previous session: if the radio was still playing when
    /// the app last closed, starts the tuned station again (respecting the saved volume/mute). Safe
    /// to call once at app startup; a no-op if the radio was paused/stopped, never enabled, or no
    /// station was saved. Never throws — a failed resume just leaves the player in
    /// <see cref="RadioPlaybackStatus.Error"/>.
    /// </summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        RadioSettings current;
        lock (_gate)
        {
            current = new RadioSettings
            {
                RadioEnabled = _enabled,
                RadioWasPlaying = _wantsPlayback,
                RadioLastStation = _station?.Id,
            };
        }

        if (!ShouldResumeOnLaunch(current)) return;
        await PlayAsync(current.RadioLastStation!, ct).ConfigureAwait(false);
    }

    /// <summary>Turns the radio feature on/off. Turning it off stops any current playback.</summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _enabled = enabled;
            // Clear the resume intent here so the single save below covers it, and StopAsync
            // (which only saves when the intent actually changes) doesn't write a second time.
            if (!enabled) _wantsPlayback = false;
        }
        Persist();

        if (!enabled) await StopAsync(ct).ConfigureAwait(false);
        else RaiseChanged();
    }

    /// <summary>Loads and plays the given station (by <see cref="RadioStation.Id"/>), replacing whatever is currently playing.</summary>
    public Task PlayAsync(string stationId, CancellationToken ct = default)
    {
        var station = RadioStationCatalog.Find(stationId);
        if (station is null) return Task.CompletedTask;

        lock (_gate)
        {
            _station = station;
            _enabled = true;
            _wantsPlayback = true;
            _suspended = false; // an explicit play takes over from a developer-mode suspend
            _pendingPlays++;
        }
        Persist();

        return QueuePlay(station, ct);
    }

    /// <summary>Resumes the currently-loaded station (or restarts it if stopped). No-op if none is tuned.</summary>
    public Task PlayAsync(CancellationToken ct = default)
    {
        RadioStation? station;
        lock (_gate) station = _station;
        return station is null ? Task.CompletedTask : PlayAsync(station.Id, ct);
    }

    /// <summary>
    /// Toggles playback: pauses if currently playing; stops if buffering or in error (so a stream
    /// that's connecting or has failed can be cancelled, and doesn't stay flagged to resume on the
    /// next launch); otherwise resumes the tuned station (or starts the first catalog station if
    /// none has been tuned yet). This is the single entry point a hardware "Play/Pause" media key
    /// should call.
    /// </summary>
    public Task TogglePlayPauseAsync(CancellationToken ct = default)
    {
        RadioPlaybackStatus status;
        RadioStation? station;
        lock (_gate) { status = _status; station = _station; }

        return ToggleActionFor(status) switch
        {
            RadioToggleAction.Pause => PauseAsync(ct),
            RadioToggleAction.Stop => StopAsync(ct),
            _ => PlayAsync(station?.Id ?? RadioStationCatalog.Stations[0].Id, ct),
        };
    }

    /// <summary>What <see cref="TogglePlayPauseAsync"/> does from the given playback status.</summary>
    public static RadioToggleAction ToggleActionFor(RadioPlaybackStatus status) => status switch
    {
        RadioPlaybackStatus.Playing => RadioToggleAction.Pause,
        RadioPlaybackStatus.Buffering or RadioPlaybackStatus.Error => RadioToggleAction.Stop,
        _ => RadioToggleAction.Play,
    };

    /// <summary>Advances to and plays the next station in the catalog (wrapping). This is what a hardware "Next" media key should call.</summary>
    public Task NextStationAsync(CancellationToken ct = default)
    {
        string? current;
        lock (_gate) current = _station?.Id;
        return PlayAsync(RadioStationCatalog.Next(current).Id, ct);
    }

    /// <summary>Goes back to and plays the previous station in the catalog (wrapping). This is what a hardware "Previous" media key should call.</summary>
    public Task PreviousStationAsync(CancellationToken ct = default)
    {
        string? current;
        lock (_gate) current = _station?.Id;
        return PlayAsync(RadioStationCatalog.Previous(current).Id, ct);
    }

    /// <summary>
    /// Pauses playback, leaving the current station loaded. Also records that the user no longer
    /// wants the radio playing, so the next launch stays silent.
    /// </summary>
    public Task PauseAsync(CancellationToken ct = default)
    {
        ClearPlaybackIntent();
        return Task.Run(() =>
        {
            try
            {
                lock (_transportGate)
                {
                    MediaPlayer? player;
                    lock (_gate) player = LivePlayerLocked();
                    player?.Pause(); // outside _gate (#144)
                }
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }
        }, ct);
    }

    /// <summary>
    /// Stops playback and releases the loaded media. Also records that the user no longer wants the
    /// radio playing, so the next launch stays silent.
    /// </summary>
    public Task StopAsync(CancellationToken ct = default)
    {
        ClearPlaybackIntent();
        return Task.Run(() =>
        {
            try
            {
                lock (_transportGate)
                {
                    MediaPlayer? player;
                    lock (_gate) player = LivePlayerLocked();
                    player?.Stop(); // outside _gate (#144): Stop waits for LibVLC's event threads
                    lock (_gate) _status = RadioPlaybackStatus.Stopped;
                }
                RaiseChanged();
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }
        }, ct);
    }

    /// <summary>Forgets that the radio should be playing (and persists that) so the next launch doesn't resume it.</summary>
    private void ClearPlaybackIntent()
    {
        lock (_gate)
        {
            _suspended = false; // an explicit pause/stop: leaving developer mode must not resume
            if (!_wantsPlayback) return;
            _wantsPlayback = false;
        }
        Persist();
    }

    /// <summary>True while audio is stopped by <see cref="SuspendForDeveloperModeAsync"/> and not yet resumed.</summary>
    public bool IsSuspendedForDeveloperMode
    {
        get { lock (_gate) return _suspended; }
    }

    /// <summary>
    /// Silences the real radio while developer mode is on, where the UI drives a simulation and so
    /// can't reach this player. Unlike <see cref="PauseAsync"/>/<see cref="StopAsync"/> this keeps
    /// the resume-on-launch intent and writes nothing to disk: closing the app while suspended
    /// leaves the saved settings exactly as they were before developer mode. Only a stream that is
    /// playing, connecting, or queued to start is suspended (stopped rather than paused, since a
    /// paused live stream would resume stale; a queued play is dropped); paused, stopped, or failed
    /// playback is left alone. Idempotent.
    /// </summary>
    public Task SuspendForDeveloperModeAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_suspended || _disposed) return Task.CompletedTask;
            // A play still queued hasn't moved _status off Stopped/Paused yet, but will start audio.
            var audible = _status is RadioPlaybackStatus.Playing or RadioPlaybackStatus.Buffering;
            if (!audible && _pendingPlays == 0) return Task.CompletedTask;
            _suspended = true;
        }

        return Task.Run(() =>
        {
            try
            {
                lock (_transportGate)
                {
                    MediaPlayer? player;
                    lock (_gate)
                    {
                        if (!_suspended || _disposed) return; // an explicit play/stop got here first
                        player = _mediaPlayer;
                        _status = RadioPlaybackStatus.Stopped;
                    }
                    // Stop outside _gate: LibVLC can wait on its event thread, whose handlers take _gate (#144).
                    player?.Stop();
                }
                RaiseChanged();
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }
        }, ct);
    }

    /// <summary>
    /// Undoes <see cref="SuspendForDeveloperModeAsync"/>: restarts the tuned station, but only if
    /// that call actually silenced it and nothing since (an explicit play/pause/stop, or turning the
    /// radio off) has changed what the user wants. Otherwise a no-op. Writes nothing to disk.
    /// </summary>
    public Task ResumeAfterDeveloperModeAsync(CancellationToken ct = default)
    {
        RadioStation? station;
        lock (_gate)
        {
            if (!_suspended) return Task.CompletedTask;
            _suspended = false;
            station = _station;
            if (_disposed || !_enabled || !_wantsPlayback || station is null) return Task.CompletedTask;
            _pendingPlays++;
        }

        return QueuePlay(station, ct);
    }

    /// <summary>Test seam: pretend LibVLC reported <paramref name="status"/> (there's no native runtime under test).</summary>
    internal void SetStatusForTest(RadioPlaybackStatus status)
    {
        lock (_gate) _status = status;
    }

    /// <summary>Test seam: the lock queued plays and the developer-mode suspend serialize on, so a test can hold them back.</summary>
    internal object TransportGateForTest => _transportGate;

    /// <summary>Sets output volume (0-100), applying it immediately if the engine is initialized.</summary>
    public Task SetVolumeAsync(int volume, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        lock (_gate) _volume = clamped;
        PersistSoon();

        return Task.Run(() =>
        {
            try
            {
                // Read the latest level rather than `clamped`: these tasks can run out of order
                // during a slider drag, and the player must end on the value the snapshot shows.
                // _transportGate keeps read-then-apply atomic, so the last task to run applies it.
                lock (_transportGate)
                {
                    MediaPlayer? player;
                    int level;
                    lock (_gate) { player = LivePlayerLocked(); level = _volume; }
                    if (player is not null) player.Volume = level; // outside _gate (#144)
                }
                RaiseChanged();
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }
        }, ct);
    }

    /// <summary>Mutes/unmutes output, applying it immediately if the engine is initialized.</summary>
    public Task SetMuteAsync(bool muted, CancellationToken ct = default)
    {
        lock (_gate) _muted = muted;
        Persist();

        return Task.Run(() =>
        {
            try
            {
                lock (_transportGate)
                {
                    MediaPlayer? player;
                    bool mute;
                    lock (_gate) { player = LivePlayerLocked(); mute = _muted; }
                    if (player is not null) player.Mute = mute; // outside _gate (#144)
                }
                RaiseChanged();
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }
        }, ct);
    }

    /// <summary>
    /// Runs <see cref="PlayStationCore"/> on a background thread for a play already counted in
    /// <see cref="_pendingPlays"/>. The token is checked inside the task rather than handed to
    /// <see cref="Task.Run(Action, CancellationToken)"/>, so a cancelled play still releases its count.
    /// </summary>
    private Task QueuePlay(RadioStation station, CancellationToken ct)
        => Task.Run(() => PlayStationCore(station, ct));

    /// <summary>
    /// Runs on a background thread: lazily brings up LibVLC and starts streaming the given station.
    /// Always releases one <see cref="_pendingPlays"/> count taken by the caller.
    /// </summary>
    private void PlayStationCore(RadioStation station, CancellationToken ct = default)
    {
        try
        {
            lock (_transportGate)
            {
                // A play queued before developer mode suspended the radio (or before shutdown) must
                // not start audio now.
                lock (_gate)
                {
                    if (_suspended || _disposed || ct.IsCancellationRequested) return;
                }

                var player = EnsureEngine();
                if (player is null) return; // EnsureEngine already recorded the error.

                int attempt;
                LibVLC libVlc;
                lock (_gate)
                {
                    _status = RadioPlaybackStatus.Buffering;
                    attempt = ++_playAttempt;
                    libVlc = _libVlc!;
                }
                RaiseChanged();

                using var media = new Media(libVlc, new Uri(station.StreamUrl));
                player.Play(media);
                ArmConnectWatchdog(attempt);
            }
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            // Released only once the status reflects this play (Buffering, or Error), so a suspend
            // in between always sees either the pending count or the new status.
            lock (_gate) _pendingPlays--;
        }
    }

    /// <summary>
    /// Lazily brings up the LibVLC engine and media player, wiring their events into our status.
    /// Returns null (having already recorded the error) if the native runtime is not available (no
    /// system libvlc on Linux, a corrupt install), is disposed, or does not start within
    /// <see cref="_engineStartTimeout"/>, so callers can bail without crashing. Called with
    /// <see cref="_transportGate"/> held (one start at a time) but NOT <see cref="_gate"/>.
    /// </summary>
    private MediaPlayer? EnsureEngine()
    {
        Task<RadioEngineHandle> init;
        lock (_gate)
        {
            if (_disposed) return null;
            if (_mediaPlayer is not null) return _mediaPlayer;
            init = _engineInit ??= StartEngineInit();
        }

        RadioEngineHandle handle;
        try
        {
            if (!init.Wait(_engineStartTimeout))
            {
                // Leave the start running: if it does finish, the next play adopts it.
                SetError($"The radio engine did not start within {(int)_engineStartTimeout.TotalSeconds} seconds. " +
                         "Check that VLC's files are intact and not blocked by security software, then try again.");
                return null;
            }

            handle = init.Result;
        }
        catch (Exception ex)
        {
            // Missing/broken native libvlc, unsupported platform, etc. Never let this take the
            // process down; the radio card should just show an error state. A later play tries again.
            lock (_gate) { if (ReferenceEquals(_engineInit, init)) _engineInit = null; }
            SetError($"Radio engine unavailable: {ex.GetBaseException().Message}");
            return null;
        }

        var player = handle.Player;
        player.Playing += (_, _) => { lock (_gate) { _status = RadioPlaybackStatus.Playing; _lastError = null; } RaiseChanged(); };
        player.Paused += (_, _) => { lock (_gate) _status = RadioPlaybackStatus.Paused; RaiseChanged(); };
        // A Stopped event follows a stop we issued after an error; it must not wipe that error.
        player.Stopped += (_, _) => { lock (_gate) { if (_status != RadioPlaybackStatus.Error) _status = RadioPlaybackStatus.Stopped; } RaiseChanged(); };
        player.Buffering += (_, _) => { lock (_gate) if (_status is not (RadioPlaybackStatus.Playing or RadioPlaybackStatus.Error)) _status = RadioPlaybackStatus.Buffering; RaiseChanged(); };
        player.EncounteredError += (_, _) => SetError("The stream could not be played (network error or invalid stream).");

        bool adopted;
        lock (_gate)
        {
            adopted = !_disposed && _mediaPlayer is null;
            if (adopted)
            {
                _libVlc = handle.Lib;
                _mediaPlayer = player;
                player.Volume = _volume;
                player.Mute = _muted;
            }
        }

        if (!adopted)
        {
            handle.Release(); // disposed while it was starting
            return null;
        }

        return player;
    }

    /// <summary>Starts building the engine on a worker thread, and frees it if it finishes only after <see cref="Dispose"/>.</summary>
    private Task<RadioEngineHandle> StartEngineInit()
    {
        var task = Task.Run(_engineFactory);
        task.ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            bool orphaned;
            lock (_gate) orphaned = _disposed && !ReferenceEquals(_mediaPlayer, t.Result.Player);
            if (orphaned) t.Result.Release();
        }, TaskScheduler.Default);
        return task;
    }

    private static RadioEngineHandle CreateNativeEngine()
    {
        LibVLCSharp.Shared.Core.Initialize();
        var lib = new LibVLC(enableDebugLogs: false);
        try
        {
            return new RadioEngineHandle(lib, new MediaPlayer(lib));
        }
        catch
        {
            lib.Dispose();
            throw;
        }
    }

    /// <summary>
    /// After <see cref="_connectTimeout"/>, ends a play that is still only "Buffering": the station is
    /// down, blocked, or not answering, and the card should say so instead of spinning for ever.
    /// Superseded (a newer play, a pause or stop, a suspend) or already playing, it does nothing.
    /// </summary>
    private void ArmConnectWatchdog(int attempt)
    {
        _ = Task.Delay(_connectTimeout).ContinueWith(_ =>
        {
            try
            {
                MediaPlayer? player;
                lock (_transportGate)
                {
                    lock (_gate)
                    {
                        if (_disposed || _suspended || attempt != _playAttempt || _status != RadioPlaybackStatus.Buffering) return;
                        player = LivePlayerLocked();
                    }
                    try { player?.Stop(); } catch { /* best effort */ } // outside _gate (#144)
                }

                lock (_gate) { if (attempt != _playAttempt || _disposed) return; }
                SetError($"The station did not start playing within {(int)_connectTimeout.TotalSeconds} seconds. " +
                         "It may be offline, or your connection may be blocking it.");
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// The player a transport call may use, read under <see cref="_gate"/>: null once disposed, so a
    /// call that was queued (or waiting on <see cref="_transportGate"/>) when a teardown timed out
    /// never reaches LibVLC after <see cref="Dispose"/>.
    /// </summary>
    private MediaPlayer? LivePlayerLocked() => _disposed ? null : _mediaPlayer;

    private void SetError(string message)
    {
        lock (_gate)
        {
            _status = RadioPlaybackStatus.Error;
            _lastError = message;
        }
        RaiseChanged();
    }

    /// <summary>Writes the current station/volume/mute/enabled state back into settings and saves, if wired up.</summary>
    private void Persist()
    {
        if (_settings is null) return;
        CopyToSettings(_settings);

        // A full save covers anything a debounced volume write was still waiting to save.
        if (_store is null) return;
        lock (_saveGate) SaveLocked();
    }

    /// <summary>
    /// Like <see cref="Persist"/>, but the disk write waits until changes have gone quiet for the
    /// save delay. The in-memory settings are updated straight away.
    /// </summary>
    private void PersistSoon()
    {
        if (_settings is null) return;
        CopyToSettings(_settings);
        if (_store is null) return;

        lock (_saveGate)
        {
            if (_disposed) { SaveLocked(); return; }
            _savePending = true;
            ArmSaveTimerLocked();
        }
    }

    /// <summary>Writes any debounced settings change to disk now. A no-op when nothing is pending.</summary>
    public void FlushPendingSave()
    {
        if (_settings is null || _store is null) return;
        lock (_saveGate)
        {
            if (_savePending) SaveLocked();
        }
    }

    /// <summary>
    /// Save while holding <see cref="_saveGate"/>. The pending flag only clears once the write has
    /// succeeded; a failed write (file locked by another process, disk full…) is retried after the
    /// save delay, until Dispose, instead of being dropped.
    /// </summary>
    private void SaveLocked()
    {
        if (_store!.TrySave(_settings!))
        {
            _savePending = false;
            return;
        }

        _savePending = true;
        if (!_disposed) ArmSaveTimerLocked();
    }

    private void ArmSaveTimerLocked()
    {
        // The timer only decides *when*; the save itself is posted to the settings' owning thread.
        _saveTimer ??= new Timer(_ => _postSave(FlushPendingSave), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(_saveDelay, Timeout.InfiniteTimeSpan);
    }

    private static Action<Action> PostTo(SynchronizationContext? context)
        => context is null ? run => run() : run => context.Post(_ => run(), null);

    private void CopyToSettings(AppSettings settings)
    {
        lock (_gate)
        {
            settings.Radio.RadioEnabled = _enabled;
            settings.Radio.RadioLastStation = _station?.Id;
            settings.Radio.RadioVolume = _volume;
            settings.Radio.RadioMute = _muted;
            settings.Radio.RadioWasPlaying = _wantsPlayback;
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Don't lose a volume change made just before shutdown.
        FlushPendingSave();
        lock (_saveGate) _saveTimer?.Dispose();

        // Stopping and releasing LibVLC happens off this thread with a bounded wait: if the native
        // side wedges, app exit carries on and the process teardown reclaims it.
        var teardown = Task.Run(TeardownEngine);
        try { teardown.Wait(_nativeTeardownTimeout); } catch { /* best effort */ }
    }

    /// <summary>
    /// Stops and frees the native engine. Takes <see cref="_transportGate"/> so the engine isn't
    /// freed while another transport call is between reading the player and using it; detaches the
    /// fields under <see cref="_gate"/> but calls LibVLC outside it (#144), since its event handlers
    /// take <see cref="_gate"/>.
    /// </summary>
    private void TeardownEngine()
    {
        MediaPlayer? player;
        LibVLC? libVlc;
        lock (_transportGate)
        {
            lock (_gate)
            {
                player = _mediaPlayer;
                libVlc = _libVlc;
                _mediaPlayer = null;
                _libVlc = null;
            }
            try { player?.Stop(); } catch { /* best effort */ }
            try { player?.Dispose(); } catch { /* best effort */ }
            try { libVlc?.Dispose(); } catch { /* best effort */ }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A started LibVLC engine: the library and the player built on it. Disposed once, whoever gets there
/// first (a start that finishes after the service was disposed has nobody to adopt it).
/// </summary>
internal sealed class RadioEngineHandle(LibVLC lib, MediaPlayer player)
{
    private int _released;

    public LibVLC Lib { get; } = lib;
    public MediaPlayer Player { get; } = player;

    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        try { Player.Dispose(); } catch { /* best effort */ }
        try { Lib.Dispose(); } catch { /* best effort */ }
    }
}
