using System.Globalization;
using EDNexus.App.Telemetry;
using EDNexus.Core.Mining;
using EDNexus.Core.Settings;
using EDNexus.Core.Twitch;
using IOverlay = EDNexus.Core.Overlay.IOverlay;
using IVoice = EDNexus.Core.Voice.IVoice;

namespace EDNexus.App;

/// <summary>Process-wide services created in <c>Program.Main</c> and read by the Avalonia app.</summary>
public sealed class Bootstrap
{
    public SettingsStore Store { get; }
    public AppSettings Settings { get; }
    public CrashReporting Crash { get; }

    /// <summary>Runtime developer-tools state (not persisted; off every launch).</summary>
    public DeveloperOptions Dev { get; } = new();

    /// <summary>The in-game HUD overlay — the Windows implementation, or a no-op elsewhere.</summary>
    public IOverlay Overlay { get; } = Services.Overlay.OverlayFactory.Create();

    /// <summary>Spoken callouts — Windows SAPI, or a no-op elsewhere.</summary>
    public IVoice Voice { get; } = Services.Voice.VoiceFactory.Create();

    /// <summary>
    /// The desktop side of the Twitch login flow. Standalone — it talks only to the EBS, and only
    /// when the commander presses Log in. Rebuilt whenever the configured EBS changes, since the
    /// endpoints it calls are derived from that base URL.
    /// </summary>
    public TwitchAuthService Twitch { get; private set; }

    /// <summary>
    /// Retries card clears and sign-outs the EBS has not acknowledged. Owned here rather than by the
    /// engine host so that neither a host rebuild nor an app restart drops one.
    /// </summary>
    public EbsCleanupQueue TwitchCleanup { get; }

    public Bootstrap(SettingsStore store, AppSettings settings, CrashReporting crash)
    {
        Store = store;
        Settings = settings;
        Crash = crash;
        TwitchCleanup = new EbsCleanupQueue(settings, store);
        TwitchCleanup.Start();
        Twitch = BuildTwitchAuth();

        // Apply the saved voice choice up front so the very first callout already uses it.
        Voice.SetVoice(Settings.Voice.VoiceName);
        Voice.SetVolume(Settings.Voice.Volume);
    }

    /// <summary>Persist the current consent choice and start/stop reporting to match.</summary>
    public void ApplyCrashReportingChoice(bool enabled)
    {
        Settings.CrashReportingEnabled = enabled;
        Store.Save(Settings);
        if (enabled) Crash.TryStart(Settings);
        else Crash.Stop();
    }

    /// <summary>
    /// Persist the EDDN/Inara opt-in choices. The reporters read these flags live, so no restart is
    /// needed for the change to take effect.
    /// </summary>
    public void ApplyReportingChoice(bool eddnEnabled, bool inaraEnabled, string inaraApiKey)
    {
        Settings.Reporting.Eddn.Enabled = eddnEnabled;
        Settings.Reporting.Inara.Enabled = inaraEnabled;
        Settings.Reporting.Inara.ApiKey = inaraApiKey.Trim();
        Store.Save(Settings);
    }

    /// <summary>Persist the user's auto-update preference.</summary>
    public void ApplyAutoDownloadChoice(bool enabled)
    {
        Settings.AutoDownloadUpdates = enabled;
        Store.Save(Settings);
        System.Diagnostics.Trace.TraceInformation($"Settings: AutoDownloadUpdates set to {enabled}");
    }

    /// <summary>Persist the pinned engineering blueprint (null id clears the pin).</summary>
    public void ApplyEngineeringPin(string? blueprintId, int grade)
    {
        Settings.Engineering.PinnedBlueprintId = blueprintId;
        Settings.Engineering.PinnedGrade = grade;
        Store.Save(Settings);
    }

    /// <summary>Persist the Ship / On-foot toggle for the Engineering card.</summary>
    public void ApplyEngineeringOnFootMode(bool onFootMode)
    {
        Settings.Engineering.OnFootMode = onFootMode;
        Store.Save(Settings);
    }

    /// <summary>Persist the dashboard arrangement (card order, visibility, width, collapse state).</summary>
    public void ApplyDashboardLayout(IEnumerable<CardLayout> layout)
    {
        Settings.Dashboard.Cards = layout.ToList();
        Store.Save(Settings);
    }

    /// <summary>Persist the pinned Odyssey suit/weapon upgrade (null id clears the pin).</summary>
    public void ApplyOnFootPin(string? kind, string? id, int grade)
    {
        Settings.Engineering.PinnedOnFootKind = kind;
        Settings.Engineering.PinnedOnFootId = id;
        Settings.Engineering.PinnedOnFootGrade = grade;
        Store.Save(Settings);
    }

    /// <summary>Persist the route plotter's last plotted route (or an empty one, to clear it).</summary>
    public void ApplySavedRoute(RouteSettings route)
    {
        Settings.Route = route;
        Store.Save(Settings);
    }

    /// <summary>Persist the mining card's "worth mining" credit threshold.</summary>
    public void ApplyMiningThreshold(int credits)
    {
        Settings.Mining.MinValueThreshold = Math.Max(0, credits);
        Store.Save(Settings);
    }

    /// <summary>
    /// Fold newly observed galactic-average prices into the learned price book. A commodity's average
    /// price is a fixed constant, so once learned it is written once and never touched again — this
    /// only ever adds unseen commodities or corrects one this build's table had wrong.
    /// </summary>
    public void LearnCommodityPrices(IEnumerable<(string Symbol, int MeanPrice)> prices)
    {
        // Developer mode feeds fabricated markets (invented MeanPrice values) through the real bus, and
        // is documented as never persisted — so nothing observed while it is on may reach the settings file.
        if (Dev.Enabled) return;

        var changed = false;
        foreach (var (symbol, mean) in prices)
        {
            if (mean <= 0 || string.IsNullOrEmpty(symbol)) continue;
            if (Settings.Mining.KnownPrices.TryGetValue(symbol, out var existing) && existing == mean) continue;
            Settings.Mining.KnownPrices[symbol] = mean;
            changed = true;
        }
        if (changed) Store.Save(Settings);
    }

    /// <summary>
    /// Roll the mining day over to <paramref name="now"/>'s local date if it has changed, freezing
    /// whatever totals stood as "last session" first. Idempotent — a no-op once already on today's
    /// date — so it is safe to call on every dashboard tick as well as every refined unit.
    /// </summary>
    public void EnsureMiningSessionDate(DateTimeOffset now)
    {
        var today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var mining = Settings.Mining;
        if (mining.SessionDate == today) return;

        if (mining.SessionDate is not null)
        {
            mining.LastSessionDate = mining.SessionDate;
            mining.LastSessionValue = mining.SessionValue;
            mining.LastSessionUnits = mining.SessionUnits;
        }
        mining.SessionDate = today;
        mining.SessionValue = 0;
        mining.SessionUnits = 0;
        Store.Save(Settings);
    }

    /// <summary>
    /// Record one refined unit against today's running mining total (rolling the day over first if
    /// needed). <paramref name="credits"/> is 0 when the commodity's price isn't known yet — the unit
    /// still counts toward tonnage refined, just not toward the credit total.
    /// </summary>
    public void RecordMiningRefined(DateTimeOffset when, long credits)
    {
        if (Dev.Enabled) return;   // fabricated refining must not count toward the real daily total
        EnsureMiningSessionDate(when);
        Settings.Mining.SessionValue += Math.Max(0, credits);
        Settings.Mining.SessionUnits += 1;
        Store.Save(Settings);
    }

    /// <summary>
    /// Record a unit refined from an SRV into the known planetary mining spots. The list is swapped for
    /// a new one rather than edited, since the engine reads it from the journal thread on arrival.
    /// </summary>
    public void RecordMiningSpot(RefinedUnit unit, int averagePrice)
    {
        if (Dev.Enabled) return;   // a fabricated spot would be announced as real once developer mode is off
        Settings.Mining.KnownSpots = MiningSpotBook.Record(Settings.Mining.KnownSpots, unit, averagePrice);
        Store.Save(Settings);
    }

    /// <summary>Persist whether docking at a construction site looks up a shared project on Raven Colonial.</summary>
    public void ApplySharedProjectLookup(bool enabled)
    {
        Settings.Colonisation.SharedProjectLookup = enabled;
        Store.Save(Settings);
    }

    /// <summary>Persist the Mining option that announces known spots on arriving in a system.</summary>
    public void ApplyMiningSpotAnnouncements(bool enabled)
    {
        Settings.Mining.AnnounceKnownSpots = enabled;
        Store.Save(Settings);
    }

    /// <summary>Persist the overlay's on/off state and show/hide the live window to match.</summary>
    public void ApplyOverlayChoice(bool enabled)
    {
        Settings.Overlay.Enabled = enabled;
        Store.Save(Settings);
        if (enabled) Overlay.Show();
        else Overlay.Hide();
    }

    /// <summary>
    /// Raised after the Discord Rich Presence settings are saved, so whoever owns the live engine can
    /// re-apply them (connect/disconnect, privacy) without a restart.
    /// </summary>
    public event Action<DiscordSettings>? DiscordSettingsChanged;

    /// <summary>Persist the Discord Rich Presence choices and apply them to the live integration.</summary>
    public void ApplyDiscordChoice(bool enabled, bool showSystem, bool showCommander)
    {
        var discord = Settings.Discord;
        discord.Enabled = enabled;
        discord.ShowSystem = showSystem;
        discord.ShowCommander = showCommander;
        Store.Save(Settings);
        DiscordSettingsChanged?.Invoke(discord);
    }

    /// <summary>
    /// Persist the voice-callout choices and apply the voice/volume live, so a change here doesn't
    /// need a restart to take effect.
    /// </summary>
    public void ApplyVoiceChoice(bool enabled, string? voiceName, int volume, IEnumerable<string> disabledCallouts)
    {
        Settings.Voice.Enabled = enabled;
        Settings.Voice.VoiceName = string.IsNullOrWhiteSpace(voiceName) ? null : voiceName;
        Settings.Voice.Volume = Math.Clamp(volume, 0, 100);
        Settings.Voice.DisabledCallouts = disabledCallouts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Store.Save(Settings);

        Voice.SetVoice(Settings.Voice.VoiceName);
        Voice.SetVolume(Settings.Voice.Volume);
    }

    /// <summary>
    /// Persist the Twitch stream-card choices. The engine's publisher reads all of these live, so a
    /// change takes effect on its next publish rather than the next launch.
    /// </summary>
    /// <param name="enabled">Whether to publish to viewers at all.</param>
    /// <param name="sections">Which sections the broadcaster agrees to show.</param>
    /// <param name="ebsBaseUrl">
    /// The EBS to publish to and log in against. Blank restores the shipped default rather than
    /// leaving the app with no endpoint at all.
    /// </param>
    /// <returns>
    /// The clear for a card that was on the air and no longer should be (switched off, or moved to a
    /// different EBS), otherwise null. It is already queued for retry; the caller should also send it
    /// straight away through
    /// <see cref="EDNexus.Core.Twitch.TwitchStreamCardService.TakeOffAirAsync"/>, which orders it after
    /// any publish in flight, and report the result to <see cref="EbsCleanupQueue.Complete"/>.
    /// </returns>
    public PendingEbsCleanup? ApplyTwitchChoice(bool enabled, TwitchCardSections sections, string? ebsBaseUrl)
    {
        var trimmed = (ebsBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var previousBaseUrl = Settings.Twitch.EbsBaseUrl;
        // The dialog refuses an insecure address before it gets here; this is the backstop for any
        // other caller. Keeping the current EBS is safer than storing one the token cannot be sent to.
        if (TwitchOAuthOptions.ValidateEbsBaseUrl(trimmed) is not null) trimmed = previousBaseUrl;
        var wasOnAir = Settings.Twitch.StreamCardEnabled && !string.IsNullOrWhiteSpace(Settings.Twitch.Token);
        var previousToken = Settings.Twitch.Token;

        Settings.Twitch.StreamCardEnabled = enabled;
        Settings.Twitch.Card = sections;
        Settings.Twitch.EbsBaseUrl = trimmed.Length > 0 ? trimmed : new TwitchSettings().EbsBaseUrl;
        Store.Save(Settings);

        var ebsChanged = !string.Equals(previousBaseUrl, Settings.Twitch.EbsBaseUrl, StringComparison.OrdinalIgnoreCase);
        // Queued, and so persisted, before anything is sent: if the clear fails or the app closes
        // first, the card would otherwise stay public with nothing left that knows to take it down.
        var takeOffAir = wasOnAir && (!enabled || ebsChanged)
            ? TwitchCleanup.Enqueue(
                EbsCleanupKind.ClearCard,
                new TwitchOAuthOptions { EbsBaseUrl = previousBaseUrl }.UpdateStateEndpoint,
                previousToken!)
            : null;

        if (ebsChanged)
        {
            // A token minted by one EBS means nothing to another, so pointing at a different instance
            // ends the session rather than leaving the UI claiming to be signed in while every publish
            // comes back 401. Cleared locally only — the old EBS may not even be reachable.
            Settings.Twitch.Token = null;
            Settings.Twitch.ChannelId = null;
            Settings.Twitch.Username = null;
            Store.Save(Settings);

            // The auth service derives its endpoints from the base URL at construction, so a changed
            // EBS needs a new one — otherwise the next sign-in would still go to the old instance.
            Twitch = BuildTwitchAuth();
        }

        return takeOffAir;
    }

    /// <summary>
    /// Switch the stream card off after signing out, leaving every other Twitch choice as last saved.
    /// Unlike <see cref="ApplyTwitchChoice"/> it takes nothing from the dialog, so edits the
    /// commander has not saved (section toggles, a half-typed EBS URL) are not saved behind their
    /// back. Nothing is queued to take the card down: the sign-out's revoke already clears it.
    /// </summary>
    public void DisableTwitchCard()
    {
        if (!Settings.Twitch.StreamCardEnabled) return;
        Settings.Twitch.StreamCardEnabled = false;
        Store.Save(Settings);
    }

    private TwitchAuthService BuildTwitchAuth() =>
        new(Settings, Store, new TwitchOAuthOptions { EbsBaseUrl = Settings.Twitch.EbsBaseUrl }, cleanup: TwitchCleanup);
}
