using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.App.Views;
using EDNexus.Core;
using EDNexus.Core.Dev;
using EDNexus.Core.Overlay;
using EDNexus.Core.Radio;
using EDNexus.Core.Settings;
using EDNexus.Core.Voice;

namespace EDNexus.App.ViewModels;

/// <summary>
/// Dashboard shell. Owns the engine host, the refresh timer, and the collection of self-contained
/// <see cref="CardViewModel"/>s; each card pulls its own slice from the state snapshot every tick.
/// The engine mutates state on a background thread, so rather than binding to it directly we pull a
/// snapshot onto the UI thread a few times a second — simple, and it coalesces event bursts into
/// steady updates.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly Bootstrap _boot;
    private readonly DeveloperMode _dev = new();
    private readonly Random _rng = new();
    private readonly DashboardContext _context;

    // The radio lives as long as the app, not the engine host: it isn't journal-driven, and the host
    // is rebuilt on "reset to live" / leaving developer mode, which must not stop the music.
    private readonly RadioPlayerService _radioService;
    private readonly RadioPlayerSelector _radio;

    private EngineHost _host;
    private DispatcherTimer? _timer;

    public MainWindowViewModel(Bootstrap boot)
    {
        _boot = boot;
        // The delayed volume save runs on the UI thread, where the rest of the app edits the same
        // settings object, so the two never serialize and mutate it at once.
        _radioService = new RadioPlayerService(_boot.Settings, _boot.Store,
            postSave: save => Dispatcher.UIThread.Post(save));
        _radio = new RadioPlayerSelector(_radioService, () => _boot.Dev.Enabled);
        // Stream events arrive off the UI thread, independent of the 250 ms refresh tick.
        _radio.Changed += () => Dispatcher.UIThread.Post(RefreshRadio);
        _host = BuildHost();
        // Reads `_host` at event time, so a host swapped in by ResetToLive() is the one updated.
        _boot.DiscordSettingsChanged += OnDiscordSettingsChanged;
        _context = new DashboardContext(
            () => _host,
            () => _boot.Dev.Enabled,
            _rng,
            () => _boot.Settings.Engineering,
            (id, grade) => _boot.ApplyEngineeringPin(id, grade),
            onFootMode => _boot.ApplyEngineeringOnFootMode(onFootMode),
            (kind, id, grade) => _boot.ApplyOnFootPin(kind, id, grade),
            () => _boot.Settings.Route,
            route => _boot.ApplySavedRoute(route),
            () => _boot.Settings.Mining,
            prices => _boot.LearnCommodityPrices(prices),
            now => _boot.EnsureMiningSessionDate(now),
            (when, credits) => _boot.RecordMiningRefined(when, credits),
            (unit, price) => _boot.RecordMiningSpot(unit, price),
            _radio);
        Cards = new ObservableCollection<CardViewModel>
        {
            new LocationCardViewModel(_context),
            new ShipCardViewModel(_context),
            new MaterialsCardViewModel(_context),
            new EngineeringCardViewModel(_context),
            new EngineersCardViewModel(_context),
            new CargoCardViewModel(_context),
            new RouteCardViewModel(_context),
            new TradeCardViewModel(_context),
            new ColonisationCardViewModel(_context),
            new MarketCardViewModel(_context),
            new MiningCardViewModel(_context),
            new ExobiologyCardViewModel(_context),
            new MissionsCardViewModel(_context),
            new CommunityGoalsCardViewModel(_context),
            new RanksCardViewModel(_context),
            new GalnetCardViewModel(_context),
            new RadioCardViewModel(_context),
        };

        ApplySavedLayout();
        foreach (var card in Cards) card.LayoutChanged += _ => SaveLayout();

        DevMode = _boot.Dev.Enabled;
        JournalStatus = _host.JournalFound
            ? $"● Watching  {_host.JournalDirectory}"
            : "✕ Journal folder not found — set EDNEXUS_JOURNAL_DIR";
        RefreshPrivacyStatus();

        // Listen for background updater notifications so the UI can show a bottom update bar.
        EDNexus.App.Services.AutoUpdateService.UpdateDownloaded += path =>
        {
            // Marshal to the UI thread
            Dispatcher.UIThread.Post(() =>
            {
                UpdatePath = path;
                UpdateAvailable = true;
                System.Diagnostics.Trace.TraceInformation($"UI: UpdateDownloaded event received path={path}");
            });
        };
    }

    /// <summary>The dashboard cards, in display order.</summary>
    public ObservableCollection<CardViewModel> Cards { get; }

    /// <summary>
    /// The live Twitch stream-card publisher of the current host, for the settings dialog's preview
    /// and publish-status line. Read through the field rather than cached, since
    /// <see cref="ResetToLive"/> replaces the host.
    /// </summary>
    public EDNexus.Core.Twitch.TwitchStreamCardService? TwitchCard => _host.TwitchCard;

    /// <summary>
    /// <see cref="EDNexus.Core.Twitch.TwitchStreamCardService.PublishCompleted"/> from whichever
    /// engine is current. Subscribe here rather than on <see cref="TwitchCard"/>: leaving developer
    /// mode rebuilds the engine, and a subscription to the old card would go quiet.
    /// </summary>
    public event Action<EDNexus.Core.Twitch.StreamStatePublishResult>? TwitchPublishCompleted;

    /// <summary><see cref="EDNexus.Core.Twitch.TwitchStreamCardService.ReauthRequired"/> from whichever engine is current.</summary>
    public event Action? TwitchReauthRequired;

    // --- Dashboard layout: order, visibility, width and collapse, persisted per card. ---

    private IEnumerable<CardDefaults> CardDefaults() => Cards.Select(c => new CardDefaults(c.Id, c.DefaultWidth));

    /// <summary>
    /// Reorder and configure the cards from the saved layout. Tolerant by design: a saved entry for
    /// a card that no longer exists is dropped, and a card added since the layout was saved keeps
    /// its defaults and lands at the end rather than vanishing.
    /// </summary>
    private void ApplySavedLayout()
    {
        var layout = DashboardLayout.Merge(CardDefaults(), _boot.Settings.Dashboard.Cards);
        Rearrange(layout);
    }

    /// <summary>Put <see cref="Cards"/> into the given order and push each card's settings onto it.</summary>
    private void Rearrange(IReadOnlyList<CardLayout> layout)
    {
        var byId = Cards.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        var ordered = new List<CardViewModel>();
        foreach (var entry in layout.OrderBy(c => c.Order))
        {
            if (!byId.TryGetValue(entry.Id, out var card)) continue;
            card.ApplyLayout(entry.Visible, entry.Width, entry.Collapsed, entry.Column);
            ordered.Add(card);
        }

        // Rebuild in place so the bound ItemsControl re-renders in the new order.
        Cards.Clear();
        foreach (var card in ordered) Cards.Add(card);
    }

    /// <summary>The arrangement as it currently stands on screen.</summary>
    private IReadOnlyList<CardLayout> CurrentLayout()
        => Cards.Select((c, i) => new CardLayout
        {
            Id = c.Id,
            Order = i,
            Visible = c.IsVisible,
            Width = c.Width,
            Collapsed = c.IsCollapsed,
            Column = c.Column,
        }).ToList();

    /// <summary>Snapshot the current arrangement and persist it.</summary>
    private void SaveLayout() => _boot.ApplyDashboardLayout(CurrentLayout());

    /// <summary>Move a card one place earlier in the flow.</summary>
    [RelayCommand]
    private void MoveCardUp(string? id) => MoveCard(id, -1);

    /// <summary>Move a card one place later in the flow.</summary>
    [RelayCommand]
    private void MoveCardDown(string? id) => MoveCard(id, +1);

    private void MoveCard(string? id, int delta)
    {
        if (string.IsNullOrEmpty(id)) return;

        Rearrange(DashboardLayout.Move(CurrentLayout(), id, delta));
        SaveLayout();
    }

    /// <summary>
    /// Drop a card into a column, above <paramref name="beforeId"/> or at the bottom of that column
    /// when it is null. <paramref name="renderedColumns"/> is where every visible card currently
    /// sits on screen: the cards still on automatic placement are fixed there first, so arranging
    /// one card does not shuffle the ones the commander has already looked at.
    /// </summary>
    public void PlaceCard(string id, int column, string? beforeId, IReadOnlyDictionary<string, int> renderedColumns)
    {
        if (string.IsNullOrEmpty(id)) return;

        var pinned = DashboardLayout.Pin(CurrentLayout(), renderedColumns);
        Rearrange(DashboardLayout.MoveTo(pinned, id, column, beforeId));
        SaveLayout();
    }

    /// <summary>Restore the shipped order, widths and visibility.</summary>
    [RelayCommand]
    private void ResetLayout()
    {
        Rearrange(DashboardLayout.Defaults(CardDefaults()));
        SaveLayout();
    }

    /// <summary>The current arrangement as portable JSON, for sharing between machines.</summary>
    public string ExportLayoutJson() => DashboardLayoutFile.Write(CurrentLayout());

    /// <summary>
    /// Apply an exported arrangement. Returns false when the file isn't an EDNexus layout, leaving
    /// the dashboard untouched — a bad import should be a no-op, not a wrecked dashboard.
    /// </summary>
    public bool ImportLayoutJson(string? json)
    {
        if (DashboardLayoutFile.TryRead(json) is not { } imported) return false;

        // Merge rather than replace, so a layout from a different build still lands sensibly:
        // cards it doesn't mention keep their defaults, and ones this build lacks are dropped.
        Rearrange(DashboardLayout.Merge(CardDefaults(), imported));
        SaveLayout();
        return true;
    }

    /// <summary>Create a fresh engine host and wire crash reporting and voice callouts to its bus.</summary>
    private EngineHost BuildHost()
    {
        // Passing settings wires the EDDN/Inara reporters (still gated on their per-service opt-in).
        // While developer mode is on, reporting is paused so fabricated events never reach EDDN/Inara.
        var host = new EngineHost(
            settings: _boot.Settings,
            reportingSuppressed: () => _boot.Dev.Enabled);
        _boot.Crash.Attach(host.Bus, host.State); // report journal handler errors; redact the CMDR name

        // The stream card talks to a network service and can fail in several distinct ways. Without
        // this the only feedback is a label in the settings dialog, so a commander whose card never
        // appears has nothing to send us and nothing to read.
        if (host.TwitchCard is { } twitchCard)
        {
            twitchCard.PublishCompleted += result =>
            {
                if (result.IsSuccess) Trace.TraceInformation("Twitch: stream card published.");
                else Trace.TraceWarning($"Twitch: stream card publish failed ({result.Status}) — {result.Error}");
                TwitchPublishCompleted?.Invoke(result);
            };
            twitchCard.ReauthRequired += () =>
            {
                Trace.TraceWarning("Twitch: the backend rejected this machine's token; publishing stopped until re-login.");
                TwitchReauthRequired?.Invoke();
            };
        }
        host.VoiceCallouts.CalloutRaised += OnVoiceCalloutRaised;

        // The dev-mode radio simulation listens on this host's bus, where the 🎲 publishes.
        _radio.AttachSimulation(host.Bus);
        return host;
    }

    /// <summary>Speak a callout through the configured voice, unless voice is off or this kind is silenced.</summary>
    private void OnVoiceCalloutRaised(VoiceCallout callout)
    {
        var voice = _boot.Settings.Voice;
        if (!voice.Enabled) return;
        if (voice.DisabledCallouts.Contains(callout.Kind.ToString())) return;
        _boot.Voice.Speak(callout.Text);
    }

    public void Start()
    {
        _host.Start();
        if (_boot.Settings.Overlay.Enabled) _boot.Overlay.Show();
        _ = _radioService.RestoreAsync(); // fire-and-forget: resumes the last station off the UI thread
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public void Dispose()
    {
        _boot.DiscordSettingsChanged -= OnDiscordSettingsChanged;
        _boot.Overlay.Hide();
        try
        {
            _radioService.Dispose(); // also flushes a debounced volume save
        }
        finally
        {
            _host.Dispose();
        }
    }

    /// <summary>Push saved Discord Rich Presence settings onto the live engine (connect/disconnect, privacy).</summary>
    private void OnDiscordSettingsChanged(DiscordSettings settings) => _host.ApplyDiscordSettings(settings);

    [ObservableProperty] private string _journalStatus = "";
    [ObservableProperty] private string _privacyStatus = "";
    [ObservableProperty] private string _commanderName = "—";
    [ObservableProperty] private string _balance = "0 cr";
    [ObservableProperty] private string _lastUpdated = "—";
    [ObservableProperty] private bool _devMode;

    // Update bar: set when a background updater has downloaded a new build.
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updatePath = "";

    // --- Radio: a compact title-bar transport (play/pause, next, previous). Stations and their
    // stream URLs live in RadioStationCatalog; the streaming happens in the app-lifetime
    // RadioPlayerService. Controls act on _radio.Active, the same player the Space Radio card
    // drives (the simulation while developer mode is on). ---

    [ObservableProperty] private string _radioStationName = "Radio off";
    [ObservableProperty] private string _radioPlayPauseGlyph = "▶";
    [ObservableProperty] private string _radioTooltip = "Play the radio";

    /// <summary>True while the transport drives the developer-mode simulation (shows a SIM marker).</summary>
    [ObservableProperty] private bool _radioIsSimulated;

    /// <summary>Tooltip on the SIM marker.</summary>
    public string RadioSimTooltip => RadioDisplay.SimulationTooltip;

    /// <summary>Mirror the active player's snapshot onto the bindable properties above.</summary>
    private void RefreshRadio()
    {
        // Same wording as the Space Radio card: both come from RadioDisplay. The glyph shows what
        // clicking will do (see RadioPlayerService.ToggleActionFor).
        var s = _radio.Active.Snapshot;
        var d = RadioDisplay.From(s);
        RadioIsSimulated = _radio.IsSimulated;
        RadioStationName = s.Station?.Name ?? "No station tuned";
        RadioPlayPauseGlyph = d.PlayPauseGlyph;
        RadioTooltip = d.PlayPauseTooltip;
    }

    /// <summary>
    /// Turn the radio feature on or off from Settings. Turning it off stops playback and clears the
    /// resume-on-launch intent (see <see cref="RadioPlayerService.SetEnabledAsync"/>). Always the
    /// real player, because this is a saved preference, not developer-mode state. The dialog only
    /// calls it when the commander actually changed the checkbox.
    /// </summary>
    public void ApplyRadioEnabled(bool enabled) => _ = _radioService.SetEnabledAsync(enabled);

    [RelayCommand]
    private Task RadioPlayPause() => _radio.Active.TogglePlayPauseAsync();

    [RelayCommand]
    private Task RadioNext() => _radio.Active.NextStationAsync();

    [RelayCommand]
    private Task RadioPrevious() => _radio.Active.PreviousStationAsync();

    [RelayCommand]
    private async Task OpenSettings()
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var dialog = new SettingsWindow(_boot, this);
        if (owner is not null) await dialog.ShowDialog(owner);
        else dialog.Show();
        RefreshPrivacyStatus();
        DevMode = _boot.Dev.Enabled; // reflect a dev-mode toggle made in the settings dialog
    }

    /// <summary>
    /// Switch developer mode (runtime-only, never persisted). Called by the settings dialog on Save.
    /// </summary>
    public void SetDeveloperMode(bool enabled)
    {
        var wasDev = _boot.Dev.Enabled;

        // While developer mode is on the radio UI drives a simulation and can't reach the real
        // player, so silence a real stream on the way in (keeping its resume-on-launch intent and
        // writing nothing) and restart it on the way out, but only if entering is what stopped it.
        if (!wasDev && enabled) _ = _radioService.SuspendForDeveloperModeAsync();

        if (wasDev && !enabled)
        {
            // Leaving developer mode has to discard the fabricated state as well as the banner: those
            // events went through the real bus into the real CommanderState, so without a rebuild the
            // cards keep showing invented systems and cargo that no longer have a dev-mode label on
            // them. The flag flips only once the fabricated engine is gone, so it never gets a moment
            // unsuppressed in which to report to EDDN/Inara or Discord.
            RebuildHost(beforeRebuild: () => _boot.Dev.Enabled = false);
        }
        else
        {
            _boot.Dev.Enabled = enabled;

            // Entering developer mode suppresses Discord presence; clear the real one right away
            // rather than leaving it up until the first fabricated event arrives.
            if (!wasDev && _boot.Dev.Enabled) _host.RefreshDiscordPresence();
        }

        DevMode = _boot.Dev.Enabled;
        if (wasDev && !DevMode) _ = _radioService.ResumeAfterDeveloperModeAsync();
        RefreshRadio(); // the transport switches between the real player and the simulation
    }

    [RelayCommand]
    private void OpenUpdateFolder()
    {
        if (string.IsNullOrEmpty(UpdatePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(UpdatePath) ?? Path.GetTempPath();
            var psi = new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch { }
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (string.IsNullOrEmpty(UpdatePath)) return;

        // Only a download the updater verified against its published checksum is ever offered.
        var verifiedSha = EDNexus.App.Services.AutoUpdateService.LastVerifiedSha256;
        var installerPath = UpdatePath;
        if (string.IsNullOrEmpty(verifiedSha))
        {
            Trace.TraceWarning("Update: no verified checksum for the downloaded installer; not installing");
            return;
        }

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var dlg = new EDNexus.App.Views.ConfirmInstallWindow();
        dlg.SetDetails(installerPath, verifiedSha);
        if (owner is null)
        {
            // No owner (rare in tests). Show non-modal confirmation and abort install — safer than auto-running.
            dlg.Show();
            return;
        }

        var result = await dlg.ShowDialog<bool>(owner);
        if (!result) return;

        // Re-hash right before running it, so a file swapped on disk since the check (or while the dialog
        // was open) is refused rather than launched elevated.
        if (!await Task.Run(() => EDNexus.Core.Updates.UpdateChecker.HashMatches(installerPath, verifiedSha)))
        {
            Trace.TraceWarning($"Update: {installerPath} failed re-verification; not installing");
            UpdateAvailable = false;
            UpdatePath = "";
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = UpdatePath,
                UseShellExecute = true
            };
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            // Launch failed (e.g. UAC declined) — stay open so the user isn't left with nothing running.
            Trace.TraceWarning($"Update: failed to launch installer {UpdatePath}: {ex.Message}");
            return;
        }

        // The installer can't replace EDNexus's files while this process holds them open, so exit
        // once it's launched. Shutdown raises ShutdownRequested, which disposes the engine.
        Trace.TraceInformation($"Update: launched installer {UpdatePath}; shutting down so it can complete");
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void RefreshPrivacyStatus()
        => PrivacyStatus = _boot.Crash.IsActive ? "crash reporting on" : "crash reporting off";

    // --- Developer mode: fabricate random-but-valid state through the real event pipeline. ---

    /// <summary>Reshuffle a single card by publishing its sample events onto the live bus.</summary>
    [RelayCommand]
    private void RandomizeCard(string cardKey)
    {
        _dev.Randomize(_host.Bus, _rng, cardKey);
        Refresh();
    }

    /// <summary>Reshuffle every card at once.</summary>
    [RelayCommand]
    private void RandomizeAll()
    {
        _dev.Randomize(_host.Bus, _rng);
        Refresh();
    }

    /// <summary>Discard fabricated state and re-warm from the real journal by rebuilding the engine.</summary>
    [RelayCommand]
    private void ResetToLive() => RebuildHost(beforeRebuild: null);

    /// <param name="beforeRebuild">
    /// Runs after the old engine is disposed and before the new one is built — the only point at which
    /// developer mode can be switched off without the fabricated engine briefly running unsuppressed.
    /// </param>
    private void RebuildHost(Action? beforeRebuild)
    {
        _host.Dispose();
        beforeRebuild?.Invoke();
        _host = BuildHost();
        _host.Start();
        foreach (var card in Cards) card.Reset();
        Refresh();
    }

    private void Refresh()
    {
        var s = _host.State;
        CommanderName = s.Name ?? "—";
        Balance = s.Balance.ToString("N0") + " cr";
        LastUpdated = s.LastUpdated == default ? "—" : s.LastUpdated.LocalDateTime.ToString("HH:mm:ss");

        foreach (var card in Cards) card.Update(s);
        RefreshRadio();

        if (_boot.Settings.Overlay.Enabled)
        {
            var content = OverlayContentBuilder.Build(
                s, _host.Exobiology.CurrentBody, _host.Colonisation.ActiveSite, _boot.Settings.Route,
                _host.Exobiology.ActiveScan);
            _boot.Overlay.Update(content);
        }
    }

    /// <summary>
    /// Developer-mode helper: fabricates a low-fuel status, a completed exobiology scan, and a
    /// colonisation delivery that fully covers a shopping-list item — the three moments that drive a
    /// voice callout — through the real bus, so overlay/voice can be exercised without flying anywhere.
    /// </summary>
    [RelayCommand]
    private void SimulateOverlayVoice()
    {
        _dev.Randomize(_host.Bus, _rng, "overlay-voice");
        Refresh();
    }
}
