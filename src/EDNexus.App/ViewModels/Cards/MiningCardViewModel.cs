using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.App.Services;
using EDNexus.Core.Mining;
using EDNexus.Core.State;

namespace EDNexus.App.ViewModels;

/// <summary>
/// Mining session helper: every prospected asteroid this session, with each material — and any
/// deep-core motherlode — called out when its galactic-average price clears the credit threshold set
/// in Settings → Mining. Learns those average prices passively from every station market visited
/// (Frontier does not expose the figure anywhere else), so the highlight gets more complete the more
/// the commander plays rather than depending on a static, drift-prone price table.
/// </summary>
/// <remarks>
/// The journal's <c>ProspectedAsteroid</c> event does not distinguish a laser-minable surface deposit
/// from one exposed by a sub-surface displacement missile — both simply appear in its material list —
/// so "Deposit" below covers either; only a deep-core motherlode is separately identifiable.
/// </remarks>
public sealed partial class MiningCardViewModel : CardViewModel
{
    private string _signature = "";
    private long _lastLearnedMarketId;
    private DateTimeOffset _lastLearnedMarketUpdate;
    private DateTimeOffset? _lastSeenProspectTimestamp;
    private int _recordedRefinedCount;

    public MiningCardViewModel(DashboardContext context) : base(context, "mining", "MINING", 452) { }

    [ObservableProperty] private string _latestSummary = "Prospect a rock to see what it's carrying.";
    [ObservableProperty] private bool _latestWorthMining;
    [ObservableProperty] private string _sessionSummary = "";
    [ObservableProperty] private string _todayValueText = "";
    [ObservableProperty] private string _lastSessionText = "";

    public ObservableCollection<ProspectRow> Prospects { get; } = new();

    public override void Update(CommanderState s)
    {
        LearnFromCurrentMarket();
        RecordNewlyRefinedUnits();
        Context.EnsureMiningSessionDate(DateTimeOffset.Now);
        RefreshSessionValueText();

        var mining = Context.Host.Mining;
        var latestTimestamp = mining.Latest?.Timestamp;
        var isNewProspect = latestTimestamp is not null && latestTimestamp != _lastSeenProspectTimestamp;
        _lastSeenProspectTimestamp = latestTimestamp;

        var threshold = Context.GetMiningSettings().MinValueThreshold;
        var signature = $"{mining.History.Count}|{latestTimestamp:o}|{threshold}";
        if (signature != _signature)
        {
            _signature = signature;
            RebuildFromHistory();
        }

        // Only a genuinely new find rings the chime — a threshold change made in Settings can also
        // flip the latest row's verdict, but that is a re-evaluation of an old find, not a new one.
        if (isNewProspect && Prospects.Count > 0 && Prospects[0].AnyWorthMining)
            MiningAlertSound.Play();
    }

    public override void Reset()
    {
        _signature = "";
        _lastLearnedMarketId = 0;
        _lastSeenProspectTimestamp = null;
        _recordedRefinedCount = 0;
        Prospects.Clear();
        LatestSummary = "Prospect a rock to see what it's carrying.";
        LatestWorthMining = false;
        SessionSummary = "";
    }

    /// <summary>Wipe this session's prospected history — the field data itself, not the learned prices, threshold or daily value.</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        Context.Host.Mining.Clear();
        Prospects.Clear();
        LatestSummary = "Prospect a rock to see what it's carrying.";
        LatestWorthMining = false;
        SessionSummary = "";
        _signature = "";
        _lastSeenProspectTimestamp = null;
        _recordedRefinedCount = 0;
    }

    /// <summary>Feed every unit refined since the last tick into the persisted daily total.</summary>
    private void RecordNewlyRefinedUnits()
    {
        var refined = Context.Host.Mining.Refined;
        if (refined.Count <= _recordedRefinedCount)
        {
            // The tracker was cleared (or rebuilt) out from under us — resync rather than replaying
            // units that are no longer there, or skipping ones a fresh tracker starts counting from 0.
            if (refined.Count < _recordedRefinedCount) _recordedRefinedCount = 0;
            return;
        }

        // Developer mode fabricates refining through the real pipeline. The count still advances so a
        // sample unit isn't replayed, but none of it may reach the persisted daily total or spot book.
        if (Context.DevEnabled)
        {
            _recordedRefinedCount = refined.Count;
            return;
        }

        var known = Context.GetMiningSettings().KnownPrices;
        var threshold = Context.GetMiningSettings().MinValueThreshold;
        for (var i = _recordedRefinedCount; i < refined.Count; i++)
        {
            var unit = refined[i];
            var credits = known.TryGetValue(unit.Symbol, out var price) ? price : 0;
            Context.RecordMiningRefined(unit.Timestamp, credits);

            // A unit refined from an SRV that clears the "worth mining" threshold marks a planetary
            // spot worth coming back to.
            if (unit.Position is not null && threshold > 0 && credits >= threshold)
                Context.RecordMiningSpot(unit, (int)credits);
        }
        _recordedRefinedCount = refined.Count;
    }

    private void RefreshSessionValueText()
    {
        var m = Context.GetMiningSettings();
        TodayValueText = m.SessionUnits > 0 ? $"Today: {m.SessionValue:N0} cr · {m.SessionUnits:N0} t refined" : "";
        LastSessionText = m.LastSessionDate is { Length: > 0 }
            ? $"Last session ({m.LastSessionDate}): {m.LastSessionValue:N0} cr · {m.LastSessionUnits:N0} t refined"
            : "";
    }

    private void RebuildFromHistory()
    {
        var known = Context.GetMiningSettings().KnownPrices;
        var threshold = Context.GetMiningSettings().MinValueThreshold;
        var history = Context.Host.Mining.History;

        Prospects.Clear();
        // Newest first, so the rock the commander is looking at right now is always on top.
        for (var i = history.Count - 1; i >= 0; i--)
            Prospects.Add(ToRow(history[i], known, threshold));

        if (Prospects.Count > 0)
        {
            var latest = Prospects[0];
            LatestWorthMining = latest.AnyWorthMining;
            LatestSummary = latest.AnyWorthMining
                ? "Worth mining: " + string.Join(", ", latest.WorthMiningNames)
                : threshold <= 0
                    ? "Set a credit threshold in Settings → Mining to start highlighting worthwhile finds."
                    : "Nothing in this rock clears your threshold.";
        }

        var hits = Prospects.Count(p => p.AnyWorthMining);
        SessionSummary = history.Count == 0 ? ""
            : $"{history.Count} prospected · {hits} worth mining · {known.Count} prices known";
    }

    private static ProspectRow ToRow(ProspectResult r, IReadOnlyDictionary<string, int> known, int threshold)
    {
        var materials = r.Materials.Select(m => ToMaterialRow(m.Name, m.Symbol, m.Proportion, known, threshold)).ToList();

        ProspectMaterialRow? motherlode = null;
        if (r.HasMotherlode)
            motherlode = ToMaterialRow(r.MotherlodeName ?? r.MotherlodeSymbol!, r.MotherlodeSymbol!, null, known, threshold);

        return new ProspectRow(
            Time: r.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
            ContentLabel: r.Content switch
            {
                "High" => "High material content",
                "Medium" => "Medium material content",
                "Low" => "Low material content",
                _ => "",
            },
            Remaining: r.Remaining < 99.5 ? $"{r.Remaining:N0}% remaining" : "",
            Motherlode: motherlode,
            Materials: materials);
    }

    private static ProspectMaterialRow ToMaterialRow(
        string name, string symbol, double? proportion, IReadOnlyDictionary<string, int> known, int threshold)
    {
        var hasPrice = known.TryGetValue(symbol, out var price) && price > 0;
        var worthMining = hasPrice && threshold > 0 && price >= threshold;
        var proportionText = proportion is { } p ? $"{p:N0}%" : "";
        var priceText = hasPrice ? $"{price:N0} cr" : "price unknown";
        return new ProspectMaterialRow(name, proportionText, priceText, worthMining);
    }

    /// <summary>Absorb every commodity's galactic-average price out of the docked market, if any.</summary>
    private void LearnFromCurrentMarket()
    {
        // The dev sample Market event carries invented MeanPrice values; learning them would write
        // them into the real price book, which developer mode promises never to touch.
        if (Context.DevEnabled) return;

        var market = Context.Host.Market.Current;
        if (market is null || market.MarketId == _lastLearnedMarketId && market.Updated == _lastLearnedMarketUpdate)
            return;

        _lastLearnedMarketId = market.MarketId;
        _lastLearnedMarketUpdate = market.Updated;
        Context.LearnCommodityPrices(market.Commodities.Where(c => c.MeanPrice > 0).Select(c => (c.Symbol, c.MeanPrice)));
    }
}

/// <summary>One prospected asteroid, as the mining card renders it.</summary>
public sealed class ProspectRow
{
    public string Time { get; }
    public string ContentLabel { get; }
    public bool ShowContent => ContentLabel.Length > 0;
    public string Remaining { get; }
    public bool ShowRemaining => Remaining.Length > 0;
    public ProspectMaterialRow? Motherlode { get; }
    public bool HasMotherlode => Motherlode is not null;
    public IReadOnlyList<ProspectMaterialRow> Materials { get; }
    public bool AnyWorthMining => (Motherlode?.WorthMining ?? false) || Materials.Any(m => m.WorthMining);

    /// <summary>Names worth calling out, deep-core motherlode first — feeds the card's headline summary.</summary>
    public IEnumerable<string> WorthMiningNames =>
        (Motherlode is { WorthMining: true } m ? new[] { $"{m.Name} (deep core)" } : Array.Empty<string>())
        .Concat(Materials.Where(x => x.WorthMining).Select(x => x.Name));

    public ProspectRow(string Time, string ContentLabel, string Remaining, ProspectMaterialRow? Motherlode, IReadOnlyList<ProspectMaterialRow> Materials)
    {
        this.Time = Time;
        this.ContentLabel = ContentLabel;
        this.Remaining = Remaining;
        this.Motherlode = Motherlode;
        this.Materials = Materials;
    }
}

/// <summary>One material within a prospected rock (or its motherlode), with its price verdict.</summary>
public sealed class ProspectMaterialRow
{
    public string Name { get; }
    public string ProportionText { get; }
    public bool ShowProportion => ProportionText.Length > 0;
    public string PriceText { get; }
    public bool WorthMining { get; }

    public ProspectMaterialRow(string name, string proportionText, string priceText, bool worthMining)
    {
        Name = name;
        ProportionText = proportionText;
        PriceText = priceText;
        WorthMining = worthMining;
    }
}
