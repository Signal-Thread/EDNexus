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
    private EngineHost _host;
    private DispatcherTimer? _timer;

    public MainWindowViewModel(Bootstrap boot)
    {
        _boot = boot;
        _host = BuildHost();
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
            (unit, price) => _boot.RecordMiningSpot(unit, price));
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
            reportingSuppressed: () => _boot.Dev.Enabled,
            settingsStore: _boot.Store);
        _boot.Crash.Attach(host.Bus, host.State); // report journal handler errors; redact the CMDR name
        host.VoiceCallouts.CalloutRaised += OnVoiceCalloutRaised;

        // The radio plays in the background independent of the 250ms state-refresh tick, so it gets
        // its own event → UI-thread hop instead of waiting to be picked up by Refresh(). Captures
        // `host` (rather than reading the `_host` field) so a stale handler from a host that
        // ResetToLive() has since replaced can't clobber the current one's state.
        host.Radio.Changed += () => Dispatcher.UIThread.Post(() => RefreshRadio(host.Radio.Snapshot));
        RefreshRadio(host.Radio.Snapshot);
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
        _ = _host.Radio.RestoreAsync(); // fire-and-forget: resumes the last station off the UI thread
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public void Dispose()
    {
        _boot.Overlay.Hide();
        _host.Dispose();
    }

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
    // stream URLs live in RadioStationCatalog; the actual streaming happens in EngineHost.Radio. ---

    [ObservableProperty] private string _radioStationName = "Radio off";
    [ObservableProperty] private string _radioPlayPauseGlyph = "▶";
    [ObservableProperty] private string _radioTooltip = "Play the radio";

    /// <summary>Mirror a <see cref="RadioPlayerSnapshot"/> onto the bindable properties above.</summary>
    private void RefreshRadio(RadioPlayerSnapshot s)
    {
        RadioStationName = s.Station?.Name ?? "No station tuned";
        RadioPlayPauseGlyph = s.Status == RadioPlaybackStatus.Playing ? "⏸" : "▶";
        RadioTooltip = s.Status switch
        {
            RadioPlaybackStatus.Error => s.LastError ?? "Radio error",
            RadioPlaybackStatus.Buffering => $"Buffering {s.Station?.Name}…",
            RadioPlaybackStatus.Playing => $"Playing {s.Station?.Name}",
            RadioPlaybackStatus.Paused => $"Paused — {s.Station?.Name}",
            _ => s.Station is null ? "Play the radio" : $"Play {s.Station.Name}",
        };
    }

    [RelayCommand]
    private Task RadioPlayPause() => _host.Radio.TogglePlayPauseAsync();

    [RelayCommand]
    private Task RadioNext() => _host.Radio.NextStationAsync();

    [RelayCommand]
    private Task RadioPrevious() => _host.Radio.PreviousStationAsync();

    [RelayCommand]
    private async Task OpenSettings()
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var wasDev = _boot.Dev.Enabled;
        var dialog = new SettingsWindow(_boot, this);
        if (owner is not null) await dialog.ShowDialog(owner);
        else dialog.Show();
        RefreshPrivacyStatus();
        DevMode = _boot.Dev.Enabled; // reflect a dev-mode toggle made in the settings dialog

        // Leaving developer mode has to discard the fabricated state as well as the banner: those
        // events went through the real bus into the real CommanderState, so without a rebuild the
        // cards keep showing invented systems and cargo that no longer have a dev-mode label on them.
        if (wasDev && !_boot.Dev.Enabled) ResetToLive();
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
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var dlg = new EDNexus.App.Views.ConfirmInstallWindow();
        dlg.SetFilePath(UpdatePath);
        if (owner is null)
        {
            // No owner (rare in tests). Show non-modal confirmation and abort install — safer than auto-running.
            dlg.Show();
            return;
        }

        var result = await dlg.ShowDialog<bool>(owner);
        if (!result) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = UpdatePath,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch { }
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
    private void ResetToLive()
    {
        _host.Dispose();
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
