using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EDNexus.Core.Settings;
using EDNexus.Core.Twitch;
using System.Diagnostics;
using System.IO;

namespace EDNexus.App.Views;

public partial class SettingsWindow : Window
{
    private readonly Bootstrap? _boot;

    // The live dashboard, so the layout controls drive the real cards rather than a copy. Null when
    // the window is opened without one (the designer, and the pre-layout call sites).
    private readonly ViewModels.MainWindowViewModel? _dashboard;

    // What the radio toggle showed when the dialog opened, so Save only touches the player when the
    // commander actually changed it.
    private bool _radioEnabledAtOpen;

    // The crash-reporting answer the dialog opened with. Null means the commander has not been asked
    // yet (or dismissed the prompt), which a Save must not turn into a "no".
    private bool? _crashConsentAtOpen;

    // Set by "Test voice", which applies the voice and volume live; a dialog closed without saving puts
    // the saved ones back.
    private bool _voiceTested;

    // Guards against a second click while Save is still awaiting the EBS.
    private bool _saving;

    // Leaves room for the title bar and a little breathing space around the edges, so the capped
    // dialog still reads as a window rather than filling the display corner to corner.
    private const double ScreenMargin = 80;

    // Parameterless ctor for the XAML previewer / designer.
    public SettingsWindow()
    {
        InitializeComponent();
        FitToScreen();
    }

    public SettingsWindow(Bootstrap boot, ViewModels.MainWindowViewModel? dashboard = null) : this()
    {
        _boot = boot;
        _dashboard = dashboard;
        DashboardSection.IsVisible = dashboard is not null;
        if (dashboard is not null) CardList.ItemsSource = dashboard.Cards;
        _crashConsentAtOpen = boot.Settings.CrashReportingEnabled;
        CrashToggle.IsChecked = boot.Settings.CrashReportingEnabled == true;
        EddnToggle.IsChecked = boot.Settings.Reporting.Eddn.Enabled;
        InaraToggle.IsChecked = boot.Settings.Reporting.Inara.Enabled;
        InaraApiKey.Text = boot.Settings.Reporting.Inara.ApiKey;
        AutoDownloadToggle.IsChecked = boot.Settings.AutoDownloadUpdates;
        MiningThresholdBox.Text = boot.Settings.Mining.MinValueThreshold > 0
            ? boot.Settings.Mining.MinValueThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "";

        RavenLookupToggle.IsChecked = boot.Settings.Colonisation.SharedProjectLookup;
        ShareDeliveriesToggle.IsChecked = boot.Settings.Colonisation.ShareDeliveries;
        ShareDeliveriesToggle.IsEnabled = RavenLookupToggle.IsChecked == true;
        MiningSpotAnnounceToggle.IsChecked = boot.Settings.Mining.AnnounceKnownSpots;
        if (boot.Settings.Mining.KnownSpots.Count > 0)
            MiningSpotsSummary.Text += $" {boot.Settings.Mining.KnownSpots.Count:N0} spots recorded so far.";

        OverlayToggle.IsChecked = boot.Settings.Overlay.Enabled;

        VoiceToggle.IsChecked = boot.Settings.Voice.Enabled;
        VoiceNameCombo.ItemsSource = boot.Voice.AvailableVoices;
        VoiceNameCombo.SelectedItem = boot.Settings.Voice.VoiceName;
        VoiceVolumeSlider.Value = boot.Settings.Voice.Volume;
        FuelLowToggle.IsChecked = !boot.Settings.Voice.DisabledCallouts.Contains(nameof(EDNexus.Core.Voice.VoiceCalloutKind.FuelLow));
        ScanCompleteToggle.IsChecked = !boot.Settings.Voice.DisabledCallouts.Contains(nameof(EDNexus.Core.Voice.VoiceCalloutKind.ScanComplete));
        ShoppingListToggle.IsChecked = !boot.Settings.Voice.DisabledCallouts.Contains(nameof(EDNexus.Core.Voice.VoiceCalloutKind.ShoppingListItemAcquired));

        _radioEnabledAtOpen = boot.Settings.Radio.RadioEnabled;
        RadioToggle.IsChecked = _radioEnabledAtOpen;

        LoadTwitch(boot.Settings.Twitch);

        DiscordToggle.IsChecked = boot.Settings.Discord.Enabled;
        DiscordShowSystemToggle.IsChecked = boot.Settings.Discord.ShowSystem;
        DiscordShowCommanderToggle.IsChecked = boot.Settings.Discord.ShowCommander;
        UpdateDiscordPrivacyEnabled();

        // The whole section disappears when the dev tools are compiled out / disabled.
        DevSection.IsVisible = boot.Dev.Available;
        DevModeToggle.IsChecked = boot.Dev.Enabled;

        UpdateStatus();
        UpdateVersionAndUpdateLine();
    }

    /// <summary>
    /// The window auto-sizes to its content, which grows every time a section is added and had
    /// started running off the bottom of shorter (or scaled) displays. Cap both dimensions to the
    /// screen's working area — the body <see cref="ScrollViewer"/> takes over from there, and the
    /// masonry panel just packs into fewer columns when the width has to come down.
    /// </summary>
    private void FitToScreen()
    {
        try
        {
            // Before Show() there is no owner and no placement yet, so fall back to the primary
            // screen; OnOpened re-runs this once we know which display we actually landed on.
            var screen = (Owner is WindowBase owner ? Screens.ScreenFromWindow(owner) : null)
                         ?? Screens.ScreenFromWindow(this)
                         ?? Screens.Primary;
            if (screen is null) return;

            // WorkingArea is in physical pixels; window sizes are in device-independent units.
            var usable = screen.WorkingArea.Size.ToSize(screen.Scaling);

            MaxHeight = Math.Max(MinHeight, usable.Height - ScreenMargin);
            MaxWidth = Math.Max(MinWidth, usable.Width - ScreenMargin);

            // The default width buys a second column of settings; on a small display take what
            // fits instead, rather than opening wider than the desktop.
            Width = Math.Min(Width, MaxWidth);
        }
        catch (ObjectDisposedException)
        {
            // The platform window went away mid-query; the uncapped default is harmless here.
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToScreen();
    }

    private void UpdateStatus()
    {
        if (_boot is null) return;
        var active = _boot.Crash.IsActive;
        var chosen = _boot.Settings.CrashReportingEnabled;
        StatusLine.Text = chosen switch
        {
            true when active => "Status: reporting active.",
            true => "Status: enabled, but no DSN is configured in this build — nothing will be sent.",
            _ => "Status: reporting is off.",
        };
    }

    private void UpdateVersionAndUpdateLine()
    {
        try
        {
            var ver = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(Program).Assembly.Location).ProductVersion;
            VersionLine.Text = ver ?? "(unknown)";
        }
        catch
        {
            VersionLine.Text = "(unknown)";
        }

        try
        {
            // If the updater has already downloaded an update, show the path; otherwise indicate not available.
            var updatePath = EDNexus.App.Services.AutoUpdateService.LastDownloadedPath;
            if (!string.IsNullOrEmpty(updatePath))
            {
                UpdateLine.Text = "Downloaded and verified: " + Path.GetFileName(updatePath);
            }
            else
            {
                UpdateLine.Text = EDNexus.App.Services.AutoUpdateService.LastMessage ?? "No update downloaded.";
            }
        }
        catch
        {
            UpdateLine.Text = "(unknown)";
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_boot is null)
        {
            Close();
            return;
        }

        if (_saving) return;

        // Everything that can be wrong is checked before anything is applied, so a bad value cannot
        // leave the dialog half-saved.
        if (!CreditsInput.TryParse(MiningThresholdBox.Text, out var threshold))
        {
            ShowSaveError("Minimum mining value must be a whole number of credits (for example 50000), or blank to turn the highlight off.", MiningThresholdBox);
            return;
        }

        if (TwitchOAuthOptions.ValidateEbsBaseUrl(TwitchEbsBox.Text) is { } ebsError)
        {
            ShowSaveError(ebsError, TwitchEbsBox);
            return;
        }

        _saving = true;
        SaveButton.IsEnabled = false;
        HideSaveError();
        try
        {
            await ApplyAsync(threshold);
            Close();
        }
        catch (Exception ex)
        {
            // An async void handler that throws takes the UI thread down with it.
            System.Diagnostics.Trace.TraceError($"Settings: save failed: {ex}");
            ShowSaveError($"Could not save every setting: {ex.Message}", null);
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void ShowSaveError(string message, Control? offender)
    {
        SaveErrorLine.Text = message;
        SaveErrorLine.IsVisible = true;
        offender?.BringIntoView();
        offender?.Focus();
    }

    private void HideSaveError() => SaveErrorLine.IsVisible = false;

    private async Task ApplyAsync(int miningThreshold)
    {
        if (_boot is not null)
        {
            // Left as it was while the commander has never answered and has not opted in now: an
            // unanswered prompt is not a refusal.
            if (_crashConsentAtOpen is not null || CrashToggle.IsChecked == true)
                _boot.ApplyCrashReportingChoice(CrashToggle.IsChecked == true);
            _boot.ApplyReportingChoice(
                EddnToggle.IsChecked == true,
                InaraToggle.IsChecked == true,
                InaraApiKey.Text ?? string.Empty);
            _boot.ApplyAutoDownloadChoice(AutoDownloadToggle.IsChecked == true);
            _boot.ApplySharedProjectLookup(RavenLookupToggle.IsChecked == true);
            // An opt-in to send data is never kept alive behind a disabled checkbox.
            _boot.ApplyShareDeliveries(RavenLookupToggle.IsChecked == true && ShareDeliveriesToggle.IsChecked == true);
            _boot.ApplyMiningThreshold(miningThreshold);
            _boot.ApplyMiningSpotAnnouncements(MiningSpotAnnounceToggle.IsChecked == true);
            _boot.ApplyOverlayChoice(OverlayToggle.IsChecked == true);
            _boot.ApplyVoiceChoice(
                VoiceToggle.IsChecked == true,
                VoiceNameCombo.SelectedItem as string,
                (int)VoiceVolumeSlider.Value,
                DisabledCalloutNames());
            // The radio player owns its own persistence; the dashboard routes this to the real player.
            var radioEnabled = RadioToggle.IsChecked == true;
            if (radioEnabled != _radioEnabledAtOpen) _dashboard?.ApplyRadioEnabled(radioEnabled);
            var takeOffAir = _boot.ApplyTwitchChoice(
                TwitchCardToggle.IsChecked == true,
                TwitchSectionsFromToggles(),
                TwitchEbsBox.Text);
            // Stopping publishes is not enough: the EBS would keep serving the last card to every
            // viewer who opens the stream. The clear is already queued for retry; sending it here
            // as well orders it after any publish in flight, and a success saves the retry.
            // Awaited because leaving developer mode below rebuilds the host, disposing this
            // service and its HTTP client mid-request.
            if (takeOffAir is { } off && _dashboard?.TwitchCard is { } card)
            {
                // Best-effort: the clear is already queued for retry, so a failure here must not skip
                // the remaining settings or keep the dialog open.
                try { _boot.TwitchCleanup.Complete(off, await card.TakeOffAirAsync(off.Endpoint, off.Token)); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Twitch: could not clear the card directly; it stays queued: {ex.Message}"); }
            }
            // Switching the card on (or changing which sections show) changes what viewers should
            // see without touching the commander picture, so the publisher has nothing to react to —
            // and with the game closed no journal event is coming to nudge it. Ask directly.
            _dashboard?.TwitchCard?.RequestPublish();
            _boot.ApplyDiscordChoice(
                DiscordToggle.IsChecked == true,
                DiscordShowSystemToggle.IsChecked == true,
                DiscordShowCommanderToggle.IsChecked == true);
            // Runtime-only; not persisted. The dashboard owns the switch so leaving dev mode can tear
            // down the fabricated engine before reporting is un-suppressed.
            if (_dashboard is not null) _dashboard.SetDeveloperMode(DevModeToggle.IsChecked == true);
            else _boot.Dev.Enabled = DevModeToggle.IsChecked == true;
            UpdateStatus();
            UpdateVersionAndUpdateLine();
            System.Diagnostics.Trace.TraceInformation("Settings: saved by user");
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Which callout kinds the checkboxes have turned off, as <c>VoiceCalloutKind</c> names.</summary>
    private IEnumerable<string> DisabledCalloutNames()
    {
        if (FuelLowToggle.IsChecked != true) yield return nameof(EDNexus.Core.Voice.VoiceCalloutKind.FuelLow);
        if (ScanCompleteToggle.IsChecked != true) yield return nameof(EDNexus.Core.Voice.VoiceCalloutKind.ScanComplete);
        if (ShoppingListToggle.IsChecked != true) yield return nameof(EDNexus.Core.Voice.VoiceCalloutKind.ShoppingListItemAcquired);
    }

    /// <summary>Speak a short sample line through the currently selected voice/volume, without saving.</summary>
    private void OnTestVoice(object? sender, RoutedEventArgs e)
    {
        if (_boot is null) return;
        _voiceTested = true;
        _boot.Voice.SetVoice(VoiceNameCombo.SelectedItem as string);
        _boot.Voice.SetVolume((int)VoiceVolumeSlider.Value);
        _boot.Voice.Speak("EDNexus voice callouts are working.");
    }

    /// <summary>Fabricate the three callout-triggering events through the real bus, via developer mode.</summary>
    private void OnSimulateOverlayVoice(object? sender, RoutedEventArgs e)
        => _dashboard?.SimulateOverlayVoiceCommand.Execute(null);

    private void OnDiscordToggleChanged(object? sender, RoutedEventArgs e) => UpdateDiscordPrivacyEnabled();

    /// <summary>The privacy toggles only mean something while presence itself is on; their values are kept either way.</summary>
    private void UpdateDiscordPrivacyEnabled()
    {
        var enabled = DiscordToggle.IsChecked == true;
        DiscordShowSystemToggle.IsEnabled = enabled;
        DiscordShowCommanderToggle.IsEnabled = enabled;
    }

    // Sharing deliveries talks to the same service as the lookup, so it only makes sense with the lookup on.
    private void OnRavenLookupChanged(object? sender, RoutedEventArgs e)
        => ShareDeliveriesToggle.IsEnabled = RavenLookupToggle.IsChecked == true;

    private void OnRevealApiKeyChanged(object? sender, RoutedEventArgs e)
        => InaraApiKey.RevealPassword = RevealApiKey.IsChecked == true;

    private void OnOpenLogs(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EDNexus", "logs");
            Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            // Best-effort; failure to open explorer must not crash the settings dialog.
        }
    }

    private async void OnCheckNow(object? sender, RoutedEventArgs e)
    {
        try
        {
            CheckNowButton.IsEnabled = false;
            UpdateLine.Text = "Checking for updates...";
            System.Diagnostics.Trace.TraceInformation("Settings: user initiated update check");
            var res = await EDNexus.App.Services.AutoUpdateService.CheckForUpdatesAsync();
            System.Diagnostics.Trace.TraceInformation($"Settings: update check result Found={res.Found}, Message={res.Message}, Verified={res.Verified}");
            // The message is self-describing: "Update v1 downloaded and verified", "Already up to date",
            // "Update v1 could not be verified ... It was not installed.", "Update v1 is available: update through Flatpak".
            UpdateLine.Text = res.Message;
        }
        catch (Exception ex)
        {
            UpdateLine.Text = $"Check failed: {ex.Message}";
            System.Diagnostics.Trace.TraceWarning($"Settings: update check failed: {ex}");
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    // --- Twitch stream card ---

    /// <summary>
    /// Cancels an in-flight sign-in when the dialog closes, so the loopback listener and the browser
    /// wait do not outlive the window that started them.
    /// </summary>
    private CancellationTokenSource? _twitchLogin;

    // True while a sign-in is waiting on the browser, so the button is not re-enabled under it.
    private bool _twitchLoginRunning;

    private const string TwitchEbsPendingHint =
        "Save to switch to the new backend address, then sign in. Sign-in always goes to the saved address.";

    private void LoadTwitch(TwitchSettings twitch)
    {
        TwitchCardToggle.IsChecked = twitch.StreamCardEnabled;

        var card = twitch.Card;
        TwitchCommanderToggle.IsChecked = card.Commander;
        TwitchCreditsToggle.IsChecked = card.Credits;
        TwitchShipToggle.IsChecked = card.Ship;
        TwitchLocationToggle.IsChecked = card.Location;
        TwitchCarrierToggle.IsChecked = card.Carrier;
        TwitchExobioToggle.IsChecked = card.Exobiology;
        TwitchMiningToggle.IsChecked = card.Mining;
        TwitchMissionsToggle.IsChecked = card.Missions;
        TwitchCargoToggle.IsChecked = card.Cargo;

        // Blank rather than the literal default, so the placeholder does the explaining and saving an
        // untouched box doesn't pin the commander to today's hosted URL.
        TwitchEbsBox.Text = string.Equals(twitch.EbsBaseUrl, new TwitchSettings().EbsBaseUrl, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : twitch.EbsBaseUrl;

        UpdateTwitchAccountLine();
        UpdateTwitchPreview();

        TwitchEbsBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) UpdateTwitchLoginEnabled();
        };

        if (_dashboard is not null)
        {
            // Publishing happens on a background pump; hop to the UI thread to report it. Through
            // the dashboard, so the status line keeps reporting after an engine rebuild.
            _dashboard.TwitchPublishCompleted += OnTwitchPublishCompleted;
            _dashboard.TwitchReauthRequired += OnTwitchReauthRequired;
        }
    }

    /// <summary>Reflects the live section toggles into the record the mapper reads.</summary>
    private TwitchCardSections TwitchSectionsFromToggles() => new()
    {
        Commander = TwitchCommanderToggle.IsChecked == true,
        Credits = TwitchCreditsToggle.IsChecked == true,
        Ship = TwitchShipToggle.IsChecked == true,
        Location = TwitchLocationToggle.IsChecked == true,
        Carrier = TwitchCarrierToggle.IsChecked == true,
        Exobiology = TwitchExobioToggle.IsChecked == true,
        Mining = TwitchMiningToggle.IsChecked == true,
        Missions = TwitchMissionsToggle.IsChecked == true,
        Cargo = TwitchCargoToggle.IsChecked == true,
    };

    /// <summary>The EBS box as it will be saved: blank means the hosted default, as in <c>ApplyTwitchChoice</c>.</summary>
    private string TwitchEbsAsTyped()
    {
        var trimmed = (TwitchEbsBox.Text ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.Length > 0 ? trimmed : new TwitchSettings().EbsBaseUrl;
    }

    /// <summary>True while the box holds an address other than the saved one.</summary>
    private bool TwitchEbsEditPending =>
        _boot is not null
        && !string.Equals(TwitchEbsAsTyped(), _boot.Settings.Twitch.EbsBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sign-in uses the saved backend, not whatever is half-typed in the box (Save is what switches
    /// it, and signs the commander out). Rather than sign in somewhere other than the box says, hold
    /// the button until the new address has been saved.
    /// </summary>
    private void UpdateTwitchLoginEnabled()
    {
        var pending = TwitchEbsEditPending;
        TwitchLoginButton.IsEnabled = !_twitchLoginRunning && !pending;

        if (pending && !_twitchLoginRunning)
            ShowTwitchAuthStatus(TwitchEbsPendingHint);
        else if (TwitchAuthLine.Text == TwitchEbsPendingHint)
            TwitchAuthLine.IsVisible = false;
    }

    private void UpdateTwitchAccountLine()
    {
        if (_boot is null) return;

        var session = _boot.Twitch.State;
        TwitchAccountLine.Text = session.LoggedIn
            ? $"Signed in as {session.Username ?? "(unknown)"}."
            : "Not signed in.";
        TwitchLoginButton.Content = session.LoggedIn ? "Sign in again" : "Sign in with Twitch";
        TwitchLogoutButton.IsVisible = session.LoggedIn;

        // The card cannot publish without a session, so say so rather than letting the toggle look
        // like it is doing something.
        TwitchCardToggle.IsEnabled = session.LoggedIn;
        if (!session.LoggedIn && TwitchCardToggle.IsChecked == true) TwitchCardToggle.IsChecked = false;
    }

    /// <summary>
    /// Renders the snapshot that would go out right now, straight from the publisher's own mapper, so
    /// the preview cannot drift from what viewers actually get.
    /// </summary>
    private void UpdateTwitchPreview()
    {
        if (_dashboard?.TwitchCard is not { } card)
        {
            TwitchPreview.Text = "No engine is running, so there is nothing to preview yet.";
            return;
        }

        // Previewing the *saved* visibility would be misleading while the commander is still ticking
        // boxes, so map against what the toggles say right now.
        TwitchPreview.Text = DescribeSnapshot(card.Preview(TwitchSectionsFromToggles().ToVisibility()));
    }

    private static string DescribeSnapshot(StreamCardSnapshot snapshot)
    {
        var lines = new List<string> { snapshot.Headline };
        if (!string.IsNullOrWhiteSpace(snapshot.Subline)) lines.Add(snapshot.Subline!);

        var shown = new List<string>();
        if (snapshot.Commander is not null) shown.Add(snapshot.Commander.Credits is null ? "commander" : "commander + credits");
        if (snapshot.Ship is not null) shown.Add("ship");
        if (snapshot.Location is not null) shown.Add("location");
        if (snapshot.Carrier is not null) shown.Add("fleet carrier");
        if (snapshot.Exobiology is not null) shown.Add("exobiology");
        if (snapshot.Mining is not null) shown.Add("mining");
        if (snapshot.Missions is not null) shown.Add("missions");
        if (snapshot.Cargo is not null) shown.Add($"cargo ({snapshot.Cargo.Count})");

        lines.Add("");
        lines.Add(shown.Count > 0
            ? "Sections: " + string.Join(", ", shown)
            : "Sections: none — viewers would see only the line above.");
        return string.Join(Environment.NewLine, lines);
    }

    private void OnTwitchRefreshPreview(object? sender, RoutedEventArgs e) => UpdateTwitchPreview();

    /// <summary>A section toggle only changes the preview; it is persisted on Save like everything else.</summary>
    private void OnTwitchSectionToggled(object? sender, RoutedEventArgs e) => UpdateTwitchPreview();

    private void OnTwitchCardToggled(object? sender, RoutedEventArgs e) => UpdateTwitchPreview();

    private async void OnTwitchLogin(object? sender, RoutedEventArgs e)
    {
        if (_boot is null) return;

        if (TwitchEbsEditPending)
        {
            UpdateTwitchLoginEnabled();
            return;
        }

        _twitchLogin?.Cancel();
        _twitchLogin = new CancellationTokenSource();

        _twitchLoginRunning = true;
        TwitchLoginButton.IsEnabled = false;
        ShowTwitchAuthStatus("Waiting for you to approve EDNexus in your browser…");
        try
        {
            var result = await _boot.Twitch.LoginAsync(_twitchLogin.Token);
            ShowTwitchAuthStatus(result.Status switch
            {
                TwitchAuthStatus.Success => $"Signed in as {result.Username}.",
                TwitchAuthStatus.Denied => "Sign-in was declined on Twitch.",
                TwitchAuthStatus.Timeout => "Sign-in timed out — the browser page was never completed.",
                TwitchAuthStatus.Cancelled => "Sign-in cancelled.",
                _ => $"Sign-in failed: {result.Error}",
            });

            // A successful sign-in is the one moment where turning the card on is what the commander
            // came here to do — but it is still their call, so only the toggle is unlocked.
            UpdateTwitchAccountLine();
            UpdateTwitchPreview();
            // If the card was already switched on, the new token is what was missing: publish now
            // rather than waiting for the next journal event.
            if (result.IsSuccess) _dashboard?.TwitchCard?.RequestPublish();
        }
        catch (Exception ex)
        {
            ShowTwitchAuthStatus($"Sign-in failed: {ex.Message}");
        }
        finally
        {
            _twitchLoginRunning = false;
            UpdateTwitchLoginEnabled();
        }
    }

    private async void OnTwitchLogout(object? sender, RoutedEventArgs e)
    {
        if (_boot is null) return;

        TwitchLogoutButton.IsEnabled = false;
        try
        {
            await _boot.Twitch.LogoutAsync();
            // Signing out must also stop publishing, or the card would keep going on the next login.
            // Only that: the rest of the dialog is still unsaved and stays the commander's to save
            // or discard.
            TwitchCardToggle.IsChecked = false;
            _boot.DisableTwitchCard();
            ShowTwitchAuthStatus("Signed out.");
            UpdateTwitchAccountLine();
            UpdateTwitchPreview();
        }
        finally
        {
            TwitchLogoutButton.IsEnabled = true;
        }
    }

    private void ShowTwitchAuthStatus(string message)
    {
        TwitchAuthLine.Text = message;
        TwitchAuthLine.IsVisible = true;
    }

    private void OnTwitchPublishCompleted(StreamStatePublishResult result) =>
        Dispatcher.UIThread.Post(() =>
        {
            TwitchPublishStatus.Text = result.Status switch
            {
                StreamStatePublishStatus.Published => $"Last published at {DateTime.Now:HH:mm:ss}.",
                StreamStatePublishStatus.Unauthorized => "The backend rejected this machine's credential — sign in again.",
                StreamStatePublishStatus.TooLarge => "The last card was too large for Twitch — try hiding a section.",
                StreamStatePublishStatus.RateLimited => "Publishing is being rate-limited; updates are slowing down.",
                StreamStatePublishStatus.Cleared => "Card taken off the air.",
                StreamStatePublishStatus.ClearedNotDelivered =>
                    "Card taken off the air; viewers already watching will be told on the next retry.",
                _ => $"Could not publish: {result.Error}",
            };
        });

    private void OnTwitchReauthRequired() =>
        Dispatcher.UIThread.Post(() =>
        {
            ShowTwitchAuthStatus("Your Twitch authorization is no longer valid — sign in again to resume.");
            UpdateTwitchAccountLine();
        });

    protected override void OnClosed(EventArgs e)
    {
        // "Test voice" applied the unsaved choice live. After a Save the saved settings are that choice;
        // after Close they are what the commander had, which is what should be speaking.
        if (_voiceTested && _boot is not null)
        {
            _boot.Voice.SetVoice(_boot.Settings.Voice.VoiceName);
            _boot.Voice.SetVolume(_boot.Settings.Voice.Volume);
        }

        _twitchLogin?.Cancel();
        _twitchLogin?.Dispose();
        if (_dashboard is not null)
        {
            _dashboard.TwitchPublishCompleted -= OnTwitchPublishCompleted;
            _dashboard.TwitchReauthRequired -= OnTwitchReauthRequired;
        }
        base.OnClosed(e);
    }

    // --- Dashboard layout ---

    private void OnMoveCardUp(object? sender, RoutedEventArgs e) => MoveCard(sender, up: true);

    private void OnMoveCardDown(object? sender, RoutedEventArgs e) => MoveCard(sender, up: false);

    /// <summary>
    /// The row's card id rides on the button's Tag, because the reorder commands live on the
    /// dashboard rather than the card being moved.
    /// </summary>
    private void MoveCard(object? sender, bool up)
    {
        if (_dashboard is null || sender is not Control { Tag: string id }) return;

        if (up) _dashboard.MoveCardUpCommand.Execute(id);
        else _dashboard.MoveCardDownCommand.Execute(id);

        RebindCardList();
    }

    private void OnResetLayout(object? sender, RoutedEventArgs e)
    {
        if (_dashboard is null) return;
        _dashboard.ResetLayoutCommand.Execute(null);
        RebindCardList();
        ShowLayoutStatus("Layout reset to defaults.");
    }

    /// <summary>
    /// Write the arrangement to a file the commander chooses. Only the layout travels — the Inara
    /// key and install id stay in local app data, which is why settings themselves are not synced.
    /// </summary>
    private async void OnExportLayout(object? sender, RoutedEventArgs e)
    {
        if (_dashboard is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export dashboard layout",
            SuggestedFileName = DashboardLayoutFile.DefaultFileName,
            DefaultExtension = "json",
            FileTypeChoices = new[] { LayoutFileType },
        });
        if (file is null) return;   // cancelled

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_dashboard.ExportLayoutJson());
            ShowLayoutStatus($"Layout exported to {file.Name}.");
        }
        catch (Exception ex)
        {
            ShowLayoutStatus($"Export failed: {ex.Message}");
        }
    }

    private async void OnImportLayout(object? sender, RoutedEventArgs e)
    {
        if (_dashboard is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import dashboard layout",
            AllowMultiple = false,
            FileTypeFilter = new[] { LayoutFileType },
        });
        if (files.Count == 0) return;   // cancelled

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();

            if (_dashboard.ImportLayoutJson(json))
            {
                RebindCardList();
                ShowLayoutStatus($"Layout imported from {files[0].Name}.");
            }
            else
            {
                ShowLayoutStatus("That file is not an EDNexus dashboard layout — nothing changed.");
            }
        }
        catch (Exception ex)
        {
            ShowLayoutStatus($"Import failed: {ex.Message}");
        }
    }

    private static FilePickerFileType LayoutFileType => new("EDNexus dashboard layout")
    {
        Patterns = new[] { "*.json" },
    };

    /// <summary>The dashboard rebuilds its collection to reorder, so rebind to show the new order.</summary>
    private void RebindCardList()
    {
        if (_dashboard is null) return;
        CardList.ItemsSource = null;
        CardList.ItemsSource = _dashboard.Cards;
    }

    private void ShowLayoutStatus(string message)
    {
        LayoutStatus.Text = message;
        LayoutStatus.IsVisible = true;
    }
}
