namespace EDNexus.Core.Settings;

/// <summary>Persisted user settings. Kept deliberately small and UI-free.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Crash/error reporting consent. <c>null</c> = not yet asked (show the first-run prompt);
    /// <c>false</c> = declined (the default effect — nothing is sent); <c>true</c> = opted in.
    /// </summary>
    public bool? CrashReportingEnabled { get; set; }

    /// <summary>
    /// When true, downloaded updates are automatically fetched at startup (platform-specific asset
    /// from the GitHub Releases feed). Default is false to avoid surprise network activity.
    /// </summary>
    public bool AutoDownloadUpdates { get; set; } = false;

    /// <summary>
    /// Random, locally-generated correlation id. It is the only stable key attached to reports and
    /// maps to nothing outside this machine — it is not derived from the commander or the OS user.
    /// </summary>
    public string InstallId { get; set; } = "";

    /// <summary>Opt-in configuration for the EDDN and Inara data reporters. Both default to off.</summary>
    public ReportingSettings Reporting { get; set; } = new();

    /// <summary>The commander's pinned engineering goal, if any.</summary>
    public EngineeringSettings Engineering { get; set; } = new();

    /// <summary>
    /// The commander's dashboard arrangement — card order, visibility, width and collapse state.
    /// Empty until they first customise it, so a fresh install uses the shipped layout.
    /// </summary>
    public DashboardSettings Dashboard { get; set; } = new();

    /// <summary>The route plotter's last plotted route, so it survives a restart. Empty until one is plotted.</summary>
    public RouteSettings Route { get; set; } = new();

    /// <summary>The mining card's price threshold and learned galactic-average prices.</summary>
    public MiningSettings Mining { get; set; } = new();

    /// <summary>Colonisation card options (currently the shared-project lookup opt-out).</summary>
    public ColonisationSettings Colonisation { get; set; } = new();

    /// <summary>The in-game HUD overlay's on/off state.</summary>
    public OverlaySettings Overlay { get; set; } = new();

    /// <summary>Spoken-callout on/off state, chosen voice, volume and which callouts fire.</summary>
    public VoiceSettings Voice { get; set; } = new();

    /// <summary>The radio player's last selected station, volume, and mute state.</summary>
    public RadioSettings Radio { get; set; } = new();

    /// <summary>Discord Rich Presence: broadcasts live commander status to the commander's own Discord client.</summary>
    public DiscordSettings Discord { get; set; } = new();

    /// <summary>
    /// Twitch login session for streaming the commander's state to their extension/overlay via the
    /// EBS. Empty until the commander completes login via <c>EDNexus.Core.Twitch.TwitchAuthService</c>.
    /// </summary>
    public TwitchSettings Twitch { get; set; } = new();
}

/// <summary>
/// The persisted result of an EBS-mediated Twitch login: a long-lived opaque token issued by the EBS
/// (never a raw Twitch token — the desktop app never sees those) and the identified broadcaster.
/// Treat this the same as the Inara API key: sensitive, machine-local only.
/// </summary>
public sealed class TwitchSettings
{
    /// <summary>The EBS-issued long-lived bearer token, or null if never logged in / logged out.</summary>
    public string? Token { get; set; }

    /// <summary>The authenticated broadcaster's Twitch user/channel id, resolved server-side by the EBS.</summary>
    public string? ChannelId { get; set; }

    /// <summary>The authenticated Twitch display name, for showing "Logged in as ...".</summary>
    public string? Username { get; set; }

    /// <summary>
    /// When true — and once <see cref="Token"/> is present — the commander's state is published to
    /// their extension for viewers to see. Off by default: logging in is not the same as agreeing to
    /// put your session on screen.
    /// </summary>
    public bool StreamCardEnabled { get; set; }

    /// <summary>
    /// Base URL of the EBS this app logs into and publishes to. Defaults to the hosted instance;
    /// override it to point at a local EBS while developing the extension.
    /// </summary>
    public string EbsBaseUrl { get; set; } = "https://ednexus.signal-and-thread.com";

    /// <summary>Which sections of the commander's picture the broadcaster is willing to show viewers.</summary>
    public TwitchCardSections Card { get; set; } = new();

    /// <summary>
    /// Card clears and sign-outs the EBS has not acknowledged yet, retried by
    /// <c>EDNexus.Core.Twitch.EbsCleanupQueue</c> until it does. Each holds an EBS token, so treat
    /// this like <see cref="Token"/>.
    /// </summary>
    public List<EDNexus.Core.Twitch.PendingEbsCleanup> PendingCleanups
    {
        get => _pendingCleanups;
        set => _pendingCleanups = value ?? []; // a hand-edited "null" must not break the queue
    }

    private List<EDNexus.Core.Twitch.PendingEbsCleanup> _pendingCleanups = [];
}

/// <summary>
/// Per-section opt-in for the Twitch stream card. Anything left off is never included in the
/// published payload at all, so it does not leave the commander's machine — see
/// <c>EDNexus.Core.Twitch.StreamCardVisibility</c>, which this maps onto.
/// </summary>
public sealed class TwitchCardSections
{
    /// <summary>Commander name and rank standing.</summary>
    public bool Commander { get; set; } = true;

    /// <summary>
    /// The credit balance. Off by default — it is the field commanders most often prefer not to put on
    /// stream, and a card is still useful without it.
    /// </summary>
    public bool Credits { get; set; }

    /// <summary>Hull, fuel and hold.</summary>
    public bool Ship { get; set; } = true;

    /// <summary>System, body and docked station.</summary>
    public bool Location { get; set; } = true;

    /// <summary>The commander's own fleet carrier and any booked jump.</summary>
    public bool Carrier { get; set; } = true;

    /// <summary>Sampling progress and unsold Vista Genomics data.</summary>
    public bool Exobiology { get; set; } = true;

    /// <summary>The last prospected rock and this session's yield.</summary>
    public bool Mining { get; set; } = true;

    /// <summary>Held missions and massacre stacks.</summary>
    public bool Missions { get; set; } = true;

    /// <summary>The manifest of the hold.</summary>
    public bool Cargo { get; set; } = true;

    /// <summary>Projects these flags onto the mapper's own visibility record.</summary>
    public Twitch.StreamCardVisibility ToVisibility() => new(
        Commander: Commander,
        Credits: Credits,
        Ship: Ship,
        Location: Location,
        Carrier: Carrier,
        Exobiology: Exobiology,
        Mining: Mining,
        Missions: Missions,
        Cargo: Cargo);
}

/// <summary>
/// Settings for the in-game HUD overlay (a transparent, click-through, always-on-top window drawn
/// over the game). Windows-only today; a no-op elsewhere regardless of this flag.
/// </summary>
public sealed class OverlaySettings
{
    /// <summary>When true, the overlay window is shown over the game. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true (the default), the overlay is shown only while Elite Dangerous (or EDNexus itself) is the
    /// app in front, instead of sitting on top of every other window, including other overlays such as
    /// EDCopilot's. Turn off to keep it always on top.
    /// </summary>
    public bool OnlyWhenGameFocused { get; set; } = true;
}

/// <summary>
/// Settings for spoken callouts (fuel low, scan complete, shopping-list item acquired). Backed by
/// the platform's TTS engine (SAPI on Windows); a no-op elsewhere regardless of this flag.
/// </summary>
public sealed class VoiceSettings
{
    /// <summary>When true, callouts are spoken through the platform's TTS engine. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Chosen voice name (as reported by <c>IVoice.AvailableVoices</c>), or null for the engine's default.</summary>
    public string? VoiceName { get; set; }

    /// <summary>Playback volume, 0-100.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>
    /// Names of <c>VoiceCalloutKind</c> members that are turned off. Empty (the default) means every
    /// callout kind is enabled; a name present here is silenced.
    /// </summary>
    public HashSet<string> DisabledCallouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Persisted state for the background radio player: whether it's turned on, which station was
/// last tuned, and the volume/mute levels to restore on the next launch.
/// </summary>
public sealed class RadioSettings
{
    /// <summary>Whether the radio feature is turned on. Default off so a fresh install stays silent.</summary>
    public bool RadioEnabled { get; set; } = false;

    /// <summary>Id (see <see cref="Radio.RadioStation"/>) of the last station tuned, or null if none yet.</summary>
    public string? RadioLastStation { get; set; }

    /// <summary>Output volume, 0-100.</summary>
    public int RadioVolume { get; set; } = 50;

    /// <summary>Whether output is muted.</summary>
    public bool RadioMute { get; set; } = false;

    /// <summary>
    /// Whether the radio was playing (the user last pressed play, not pause/stop) when the app
    /// closed. Only when this is set does the next launch resume <see cref="RadioLastStation"/>.
    /// Default off, so a fresh install, or settings saved before this existed, start silent.
    /// </summary>
    public bool RadioWasPlaying { get; set; } = false;
}

/// <summary>
/// Discord Rich Presence settings. Unlike the EDDN/Inara reporters this defaults on: presence is a
/// local IPC connection to the commander's own already-running Discord client, not an upload to a
/// third party, so there is no separate data-sharing consent to gate it behind.
/// </summary>
public sealed class DiscordSettings
{
    /// <summary>When true, mirror system/ship/activity onto Discord Rich Presence.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When false, the presence never names the current star system, body, station, or carrier — it
    /// reads "In flight" / "Docked" instead, so a commander can't be stream-sniped or give away a fresh
    /// discovery before logging it. The "View on Inara" button is dropped too, since EDNexus's Inara
    /// sync publishes location there.
    /// </summary>
    public bool ShowSystem { get; set; } = true;

    /// <summary>
    /// When false, the presence carries nothing that identifies the commander: the "View on Inara"
    /// button (which embeds the commander name) and the ship's custom ident are omitted.
    /// </summary>
    public bool ShowCommander { get; set; } = true;

    /// <summary>
    /// Discord application (Client) ID presence is registered under. Override only to point at a
    /// different Discord application (e.g. for local testing); the default is EDNexus's own.
    /// </summary>
    public string ApplicationId { get; set; } = global::EDNexus.Core.Discord.DiscordPresenceService.DefaultApplicationId;
}

/// <summary>
/// Settings for the mining card's "worth mining" highlight.
/// </summary>
/// <remarks>
/// Frontier doesn't expose a commodity's galactic average price anywhere outside of a station's
/// market screen, and there's no live API for it either — so rather than ship a table of numbers that
/// will drift out of date, EDNexus learns it the same way a commander would: every <c>MeanPrice</c>
/// seen in a docked market is remembered here, keyed by commodity, and never needs re-learning because
/// that figure is a fixed per-commodity constant, not something that fluctuates station to station.
/// </remarks>
public sealed class MiningSettings
{
    /// <summary>
    /// Galactic-average credit threshold: a material at or above this value is called out as worth
    /// mining. 0 (the default) means unset — nothing is highlighted until the commander picks a value.
    /// </summary>
    public int MinValueThreshold { get; set; }

    /// <summary>Learned galactic-average price per commodity (canonical symbol → credits).</summary>
    public Dictionary<string, int> KnownPrices { get; set; } = new();

    /// <summary>
    /// Local calendar date ("yyyy-MM-dd") the running totals below belong to. Once a commander mines
    /// past local midnight, the next recorded unit rolls this over: the totals so far are frozen into
    /// <see cref="LastSessionDate"/> and below, and a fresh day starts at zero.
    /// </summary>
    public string? SessionDate { get; set; }

    /// <summary>Credits refined so far today (units whose price isn't known yet don't add to this).</summary>
    public long SessionValue { get; set; }

    /// <summary>Tonnes refined so far today, regardless of whether their price is known.</summary>
    public int SessionUnits { get; set; }

    /// <summary>The most recently completed day's frozen totals — "last session" in the mining card.</summary>
    public string? LastSessionDate { get; set; }
    public long LastSessionValue { get; set; }
    public int LastSessionUnits { get; set; }

    /// <summary>
    /// When true, arriving in a system with <see cref="KnownSpots"/> worth mining (at or above
    /// <see cref="MinValueThreshold"/>) raises a spoken callout. Default on; still needs voice callouts enabled.
    /// </summary>
    public bool AnnounceKnownSpots { get; set; } = true;

    /// <summary>
    /// Planetary (SRV) mining spots where a commodity at or above <see cref="MinValueThreshold"/> was
    /// refined. Replaced wholesale, never mutated in place, whenever a spot is recorded, so the journal
    /// thread can safely read the current list while the UI thread records a new one.
    /// </summary>
    public List<KnownMiningSpot> KnownSpots { get; set; } = new();
}

/// <summary>
/// One recorded planetary mining spot: a commodity refined from an SRV at a surface position. Units
/// refined close to an existing spot for the same commodity fold into it (see <c>MiningSpotBook</c>).
/// </summary>
public sealed class KnownMiningSpot
{
    public long? SystemAddress { get; set; }
    public string StarSystem { get; set; } = "";
    public string Body { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Canonical commodity symbol (see <c>CommodityName</c>).</summary>
    public string Symbol { get; set; } = "";

    /// <summary>Display name of the commodity.</summary>
    public string Name { get; set; } = "";

    /// <summary>The commodity's galactic-average price when last recorded (a fixed per-commodity constant).</summary>
    public int AveragePrice { get; set; }

    /// <summary>Tonnes refined at this spot so far.</summary>
    public int Tonnes { get; set; }

    public DateTimeOffset FirstMined { get; set; }
    public DateTimeOffset LastMined { get; set; }
}

/// <summary>
/// The route plotter card's last plotted route: inputs enough to redisplay it (and re-plot it)
/// without a network round-trip, plus where the stepper was left. A null/empty <see cref="From"/>
/// means no route is saved — the card starts blank, as before this existed.
/// </summary>
public sealed class RouteSettings
{
    public string? From { get; set; }
    public string? To { get; set; }

    /// <summary>Name of a <c>RouteMode</c> member (e.g. "NeutronHighway"), stored as text so it's a no-op to add modes later.</summary>
    public string Mode { get; set; } = "NeutronHighway";

    /// <summary>The neutron plot's jump-range text field, kept verbatim so a restore doesn't lose the commander's exact input.</summary>
    public string JumpRangeText { get; set; } = "50";

    /// <summary>Which hop the stepper was pointing at.</summary>
    public int StepIndex { get; set; }

    public List<SavedRouteHop> Hops { get; set; } = new();
}

/// <summary>
/// A persisted copy of a plotted waypoint — deliberately its own shape (not the engine's <c>RouteHop</c>)
/// so a change to the live route model never breaks deserializing an old saved route.
/// </summary>
public sealed class SavedRouteHop
{
    public string System { get; set; } = "";
    public int Jumps { get; set; }
    public bool IsNeutron { get; set; }
    public double DistanceJumpedLy { get; set; }
    public double DistanceRemainingLy { get; set; }
    public double? FuelUsed { get; set; }
    public double? FuelInTank { get; set; }
    public bool IsScoopable { get; set; }
    public bool MustRestock { get; set; }
    public double? RestockAmount { get; set; }
    public bool HasIcyRing { get; set; }
}

/// <summary>The single pinned blueprint the Engineering card focuses on. Null id means nothing pinned.</summary>
public sealed class EngineeringSettings
{
    /// <summary>Blueprint id from the engineering catalog (e.g. "fsd_increased_range"), or null.</summary>
    public string? PinnedBlueprintId { get; set; }

    /// <summary>Target grade for the pinned blueprint, 1–5.</summary>
    public int PinnedGrade { get; set; } = 5;

    /// <summary>When true, the Engineering card shows the on-foot (Odyssey) panel instead of the ship panel.</summary>
    public bool OnFootMode { get; set; }

    /// <summary>"suit" or "weapon" — which Odyssey catalog <see cref="PinnedOnFootId"/> refers to.</summary>
    public string? PinnedOnFootKind { get; set; }

    /// <summary>Suit or weapon id from the Odyssey catalog, or null if nothing pinned.</summary>
    public string? PinnedOnFootId { get; set; }

    /// <summary>Target grade for the pinned suit/weapon, 1–5.</summary>
    public int PinnedOnFootGrade { get; set; } = 5;
}

/// <summary>Per-service opt-in for outbound data reporting. Nothing is sent unless enabled.</summary>
public sealed class ReportingSettings
{
    public EddnSettings Eddn { get; set; } = new();
    public InaraSettings Inara { get; set; } = new();

    /// <summary>
    /// When true, the reporting log additionally records the (redacted) JSON payload of every EDDN
    /// and Inara upload — verbose, for validating exactly what was sent. The per-attempt summary
    /// (schema/status/result) is always logged regardless. Default off to keep the log compact.
    /// </summary>
    public bool LogPayloads { get; set; }
}

/// <summary>EDDN reporter settings. Uploads are anonymized by the relay.</summary>
public sealed class EddnSettings
{
    /// <summary>When true, contribute anonymized market/scan/travel data to EDDN.</summary>
    public bool Enabled { get; set; }
}

/// <summary>Inara reporter settings. Requires the commander's personal Inara API key.</summary>
public sealed class InaraSettings
{
    /// <summary>When true, sync commander travel/identity to Inara using <see cref="ApiKey"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>The commander's personal Inara API key (from their Inara account).</summary>
    public string ApiKey { get; set; } = "";
}
