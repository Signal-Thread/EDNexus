using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using EDNexus.Core.Colonisation;
using EDNexus.Core.CommunityGoals;
using EDNexus.Core.Discord;
using EDNexus.Core.Engineering;
using EDNexus.Core.Exobio;
using EDNexus.Core.Journal;
using EDNexus.Core.Market;
using EDNexus.Core.Mining;
using EDNexus.Core.Missions;
using EDNexus.Core.Navigation;
using EDNexus.Core.Ranks;
using EDNexus.Core.News;
using EDNexus.Core.Reporting;
using EDNexus.Core.Routes;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Core.Trade;
using EDNexus.Core.Twitch;
using EDNexus.Core.Voice;
using EliteDangerous.Edsm;
using EliteDangerous.Galnet;
using EliteDangerous.RavenColonial;
using EliteDangerous.Spansh;

namespace EDNexus.Core;

/// <summary>
/// Bundles the engine wiring — event bus, commander state, journal watcher — and manages its
/// background lifetime. Both the UI and the CLI construct one of these instead of assembling the
/// pieces by hand.
/// </summary>
public sealed class EngineHost : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly StateTracker _tracker;
    private readonly JournalWatcher? _watcher;
    private readonly ReporterHost? _reporters;
    private readonly DiscordPresenceController? _discordPresence;
    private readonly TwitchStreamCardService? _twitchCard;
    private readonly StreamStateApiClient? _twitchCardClient;
    private readonly HttpClient _http;
    private Task? _runTask;

    public JournalEventBus Bus { get; } = new();
    public CommanderState State { get; } = new();
    public ColonisationTracker Colonisation { get; }
    public MarketTracker Market { get; }

    /// <summary>Engineering planner: pinned-blueprint material/engineer guidance. Reads static reference data.</summary>
    public EngineeringTracker Engineering { get; }

    /// <summary>Exobiology: body bio signals, three-sample scan progress, and the session's Vista Genomics tally.</summary>
    public ExobiologyTracker Exobiology { get; }

    /// <summary>Missions held, the stacks they form against a common target, and pending hand-ins.</summary>
    public MissionTracker Missions { get; }

    /// <summary>Active Community Goals the commander has seen or joined: tier, contribution and time remaining.</summary>
    public CommunityGoalTracker CommunityGoals { get; }

    /// <summary>Pilot rank standing and progress across the five tracked ladders.</summary>
    public RankTracker Ranks { get; }

    /// <summary>Prospected-asteroid history for the current mining session.</summary>
    public MiningTracker Mining { get; }

    /// <summary>
    /// Publishes the commander's state to their Twitch extension for viewers. Null unless the
    /// commander has logged in to Twitch <em>and</em> switched the stream card on — see
    /// <c>AppSettings.Twitch</c>. The UI subscribes to it for the "last published" status line.
    /// </summary>
    public TwitchStreamCardService? TwitchCard { get; }

    /// <summary>
    /// Turns fuel-low, scan-complete, shopping-list-acquired and known-mining-spot moments into spoken callouts. Never
    /// speaks itself — the UI layer (or CLI) listens to <see cref="VoiceCalloutTracker.CalloutRaised"/>
    /// and hands the text to an <see cref="IVoice"/>.
    /// </summary>
    public VoiceCalloutTracker VoiceCallouts { get; }

    /// <summary>Cross-station "best price nearby" lookups. Backed by Spansh; swappable via <see cref="ITradeSearch"/>.</summary>
    public ITradeSearch Trade { get; }

    /// <summary>
    /// Galactic-mean-price estimates for commodities the game itself won't quote one for (fleet carrier
    /// buy/sell orders report <c>MeanPrice: 0</c>). Backed by Spansh station listings, averaged; see
    /// <see cref="Market.CommodityMeanPriceEstimator"/>.
    /// </summary>
    public CommodityMeanPriceEstimator CommodityMeanPrices { get; }

    /// <summary>Long-distance route plotting (neutron highway). Backed by Spansh; swappable via <see cref="IRoutePlotter"/>.</summary>
    public IRoutePlotter Routes { get; }

    /// <summary>System position / nearby lookups. Backed by EDSM; swappable via <see cref="ISystemLookup"/>.</summary>
    public ISystemLookup Navigation { get; }

    /// <summary>In-universe news. Backed by the Galnet feed; swappable via <see cref="INewsFeed"/>.</summary>
    public INewsFeed News { get; }

    /// <summary>Which Galnet articles this commander has already opened, for the "new since last open" badge.</summary>
    public NewsReadTracker NewsRead { get; }

    /// <summary>
    /// The shared, multi-commander view of a construction project. Backed by Raven Colonial;
    /// swappable via <see cref="ISharedProjectLookup"/>.
    /// </summary>
    public ISharedProjectLookup SharedProjects { get; }

    public string? JournalDirectory { get; }
    public bool JournalFound => JournalDirectory is not null;

    /// <param name="journalDir">Journal folder, or null to auto-detect.</param>
    /// <param name="settings">
    /// When supplied, wires the EDDN/Inara data reporters (still gated on their per-service opt-in).
    /// The CLI passes null, so its replay-only runs never transmit.
    /// </param>
    /// <param name="reportingSuppressed">
    /// Optional live predicate; while it returns true the reporters go silent. The app wires this to
    /// developer mode so fabricated events never reach EDDN or Inara.
    /// </param>
    /// <param name="twitchCleanup">
    /// Where a stream-card clear the EBS did not acknowledge (the game exited, the app is closing) is
    /// queued for retry. Null on hosts that have no such queue.
    /// </param>
    /// <remarks>
    /// The radio player (<see cref="EDNexus.Core.Radio.RadioPlayerService"/>) is deliberately not part of the
    /// host: it isn't journal-driven, and the host is rebuilt on "reset to live" / leaving developer
    /// mode, which must not interrupt the music. The app owns it for its whole lifetime instead.
    /// </remarks>
    public EngineHost(
        string? journalDir = null,
        AppSettings? settings = null,
        Func<bool>? reportingSuppressed = null,
        EbsCleanupQueue? twitchCleanup = null)
    {
        JournalDirectory = journalDir ?? JournalPaths.Resolve();
        _tracker = new StateTracker(Bus, State);
        Colonisation = new ColonisationTracker(Bus, State);
        Market = new MarketTracker(Bus, State);
        Engineering = new EngineeringTracker(Bus);
        Exobiology = new ExobiologyTracker(Bus, State);
        // Qualified: the property name matches the EDNexus.Core.Missions namespace, which otherwise
        // wins the bare-name lookup from inside EDNexus.Core.
        this.Missions = new MissionTracker(Bus);
        // Qualified for the same reason: CommunityGoals matches the EDNexus.Core.CommunityGoals
        // namespace.
        this.CommunityGoals = new CommunityGoalTracker(Bus);
        Ranks = new RankTracker(Bus);
        Mining = new MiningTracker(Bus);

        // Wired after Exobiology/Colonisation so their trackers have already folded the same event
        // into their own state by the time this one's handler for it runs (see VoiceCalloutTracker's
        // remarks on ScanOrganic subscription order).
        VoiceCallouts = new VoiceCalloutTracker(Bus, State, Exobiology, Colonisation);
        if (settings is not null)
        {
            // Reads the spot list by reference: the UI thread replaces it wholesale when recording.
            VoiceCallouts.KnownMiningSpotsIn = (address, name) => settings.Mining.AnnounceKnownSpots
                ? MiningSpotBook.WorthMiningIn(settings.Mining.KnownSpots, address, name, settings.Mining.MinValueThreshold)
                : Array.Empty<KnownMiningSpot>();
        }

        // Shared client for outbound trade lookups. The EDDN/Inara reporters own their own client
        // inside ReporterHost, so this one is dedicated to the read-side (Spansh) queries.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EDNexus", ResolveVersion()));
        var cacheRoot = Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath())!, "cache");
        var version = ResolveVersion();
        var spansh = new SpanshClient(new SpanshClientOptions { SoftwareName = "EDNexus", SoftwareVersion = version }, _http);

        // Market prices go stale quickly (6 h); plotted routes and system positions are effectively
        // static, so they can be cached far longer.
        Trade = new SpanshTradeSearch(spansh, new DiskResponseCache(Path.Combine(cacheRoot, "trade"), TimeSpan.FromHours(6)));

        // A commodity's galactic mean is a fixed constant Frontier never redefines outside a balance
        // patch, so this cache is deliberately long-lived — it exists to average in a few more real
        // stations over time, not to track drift the way the trade-finder's live quotes need to.
        CommodityMeanPrices = new CommodityMeanPriceEstimator(
            new SpanshTradeSearch(spansh, new DiskResponseCache(Path.Combine(cacheRoot, "commodity-mean"), TimeSpan.FromDays(3))));
        Routes = new SpanshRoutePlotter(spansh, new DiskResponseCache(Path.Combine(cacheRoot, "routes"), TimeSpan.FromDays(30)));
        Navigation = new EdsmSystemLookup(
            new EdsmClient(new EdsmClientOptions { SoftwareName = "EDNexus", SoftwareVersion = version }, _http),
            new DiskResponseCache(Path.Combine(cacheRoot, "systems"), TimeSpan.FromDays(30)));

        // Galnet publishes a few times a week, so an hour-long TTL is already far more current than
        // the source — and it keeps a dashboard that reopens all day off Frontier's site.
        News = new GalnetNewsFeed(
            new GalnetClient(new GalnetClientOptions { SoftwareName = "EDNexus", SoftwareVersion = version }, _http),
            new DiskResponseCache(Path.Combine(cacheRoot, "galnet"), TimeSpan.FromHours(1)));
        NewsRead = new NewsReadTracker();

        // Read-only: squadmates deliver while you fly, so this one is never cached.
        SharedProjects = new RavenColonialProjectLookup(new RavenColonialClient(
            new RavenColonialClientOptions { SoftwareName = "EDNexus", SoftwareVersion = version }, _http));

        if (settings is not null)
        {
            // Every EDDN/Inara upload attempt is recorded here for troubleshooting and validation.
            var log = new FileReportingLog(Path.Combine(
                Path.GetDirectoryName(SettingsStore.DefaultPath())!, "logs", "reporting.log"));
            _reporters = new ReporterHost(Bus, settings, ResolveVersion(), IsDevelopmentBuild, reportingSuppressed, log);
        }

        // Discord Rich Presence: a local IPC integration to the commander's own Discord client, not a
        // third-party upload. Like the reporters above it's still opt-out via AppSettings, and — same
        // as EDDN/Inara — the CLI's replay-only runs (settings: null) never activate it. The
        // controller exists whenever settings do, so the Settings window can switch it on/off live.
        if (settings is not null)
        {
            var discord = settings.Discord;
            _discordPresence = new DiscordPresenceController(State, () =>
            {
                try { return new DiscordRpcClientAdapter(discord.ApplicationId); }
                catch { return NoOpDiscordRpcClient.Instance; }   // unsupported platform, etc.
            }, reportingSuppressed);
            _discordPresence.Apply(discord);
        }

        // Twitch stream card: publishes the sections the broadcaster opted into to their extension,
        // via the EBS. Built whenever the app supplies settings, and idle until the commander both
        // logs in and switches the card on — the token callback below returns null until then, so
        // logging in mid-session starts it without a restart. Like the reporters and Discord above it
        // goes silent while developer mode is feeding the bus fabricated events, so sample data never
        // reaches a live audience.
        if (settings is not null)
        {
            var twitch = settings.Twitch;
            // Its own client: the shared _http above carries a 20s timeout tuned for Spansh/EDSM
            // lookups, where a stream card that cannot publish in a few seconds is already stale.
            _twitchCardClient = new StreamStateApiClient();
            TwitchCard = _twitchCard = new TwitchStreamCardService(
                State,
                new StreamCardSources(Ranks, Exobiology, Mining, this.Missions),
                _twitchCardClient,
                // Live callbacks throughout, so a change made in Settings → Twitch takes effect on
                // the next publish rather than the next launch.
                updateStateEndpoint: () => new TwitchOAuthOptions { EbsBaseUrl = twitch.EbsBaseUrl }.UpdateStateEndpoint,
                token: () => twitch.StreamCardEnabled ? twitch.Token : null,
                visibility: () => twitch.Card.ToVisibility(),
                isSuppressed: reportingSuppressed,
                cleanup: twitchCleanup);

            // The card is a live picture: it comes down when the game exits and goes back up when a
            // session starts. Fabricated developer-mode events say nothing about the real game.
            Bus.Subscribe("Shutdown", _ => { if (reportingSuppressed?.Invoke() != true) _twitchCard.GameExited(); });
            Bus.Subscribe("Fileheader", _ => { if (reportingSuppressed?.Invoke() != true) _twitchCard.GameStarted(); });
            Bus.Subscribe("LoadGame", _ => { if (reportingSuppressed?.Invoke() != true) _twitchCard.GameStarted(); });
        }

        if (JournalDirectory is not null)
            _watcher = new JournalWatcher(JournalDirectory, Bus);
    }

    /// <summary>
    /// Takes the Twitch stream card off the air as the app closes, waiting at most
    /// <paramref name="timeout"/>. Separate from <see cref="Dispose"/>, which also runs when the
    /// engine is merely rebuilt (leaving developer mode, "reset to live") and must not blank the card.
    /// </summary>
    /// <returns>True when the card is off the air, or there was nothing to clear.</returns>
    public bool TakeTwitchCardOffAirForExit(TimeSpan timeout) => _twitchCard?.TakeOffAirForExit(timeout) ?? true;

    /// <summary>
    /// Re-apply the commander's Discord Rich Presence settings to the live integration: connects or
    /// disconnects to match <see cref="DiscordSettings.Enabled"/> and pushes privacy changes straight
    /// away. A no-op on hosts built without settings (the CLI).
    /// </summary>
    public void ApplyDiscordSettings(DiscordSettings settings) => _discordPresence?.Apply(settings);

    /// <summary>
    /// Re-check the reporting-suppressed predicate for Discord presence, so switching developer mode on
    /// clears the commander's real presence immediately instead of on the next state change.
    /// </summary>
    public void RefreshDiscordPresence() => _discordPresence?.Refresh();

    /// <summary>Warm state from the latest journal, then watch live on a background task.</summary>
    public void Start()
    {
        if (_watcher is null) return;
        _watcher.Replay();
        BeginWatching(_watcher);
    }

    /// <summary>
    /// Like <see cref="Start"/>, but runs the (potentially long) journal replay on a worker thread so
    /// a UI-thread caller isn't blocked while a large journal is parsed.
    /// </summary>
    public async Task StartAsync()
    {
        if (_watcher is null) return;
        await Task.Run(_watcher.Replay).ConfigureAwait(false);
        BeginWatching(_watcher);
    }

    private void BeginWatching(JournalWatcher watcher)
    {
        // Now that the commander picture is warm, put it in front of viewers. The card service is
        // built in the constructor, before any of this has happened, so it deliberately publishes
        // nothing until asked.
        _twitchCard?.RequestPublish();

        watcher.Error += OnWatcherError;
        // The watcher catches its own per-tick failures, so this fires only if the loop itself dies.
        // Observing the task keeps that from being swallowed silently while the UI says "Watching".
        _runTask = Task.Run(() => watcher.RunAsync(_cts.Token));
        _runTask.ContinueWith(
            t => OnWatcherError(t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Raised when the journal watcher hits an unexpected error (the loop keeps running unless it died).</summary>
    public event Action<Exception>? WatcherError;

    private void OnWatcherError(Exception ex)
    {
        System.Diagnostics.Trace.TraceError("Journal watcher error: " + ex);
        WatcherError?.Invoke(ex);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* cancellation */ }
        // Flush any queued reports before tearing down the shared HttpClient.
        try { _reporters?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* best effort */ }
        _discordPresence?.Dispose();
        _twitchCard?.Dispose();
        _twitchCardClient?.Dispose();
        _cts.Dispose();
        _http.Dispose();
    }

    private static string ResolveVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(EngineHost).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = info ?? asm.GetName().Version?.ToString() ?? "0.0.0";
        var plus = version.IndexOf('+');   // strip any "+<gitsha>" build-metadata suffix
        return plus >= 0 ? version[..plus] : version;
    }

    private static bool IsDevelopmentBuild =>
#if DEBUG
        true;
#else
        false;
#endif
}
