using System.Text.Json;
using EDNexus.Core.Journal;
using EDNexus.Core.State;

namespace EDNexus.Core.Colonisation;

/// <summary>
/// Feature service that turns <c>ColonisationConstructionDepot</c> and
/// <c>ColonisationContribution</c> events into a live per-site tracker of required / provided /
/// remaining commodities. It owns its own derived state — it never mutates
/// <see cref="CommanderState"/>, only reads it to stamp the site's location.
/// </summary>
public sealed class ColonisationTracker
{
    private readonly CommanderState? _state;
    private readonly object _gate = new();
    private readonly Dictionary<long, ColonisationSite> _sites = new();
    private long? _activeMarketId;
    private long? _selectedMarketId;
    private bool _showAll;

    /// <summary>Raised after any depot/contribution event changes the tracked picture.</summary>
    public event Action? Changed;

    public ColonisationTracker(JournalEventBus bus, CommanderState? state = null)
    {
        _state = state;
        bus.Subscribe("ColonisationConstructionDepot", OnDepot);
        bus.Subscribe("ColonisationContribution", OnContribution);
    }

    /// <summary>All known construction sites, most recently updated first.</summary>
    public IReadOnlyList<ColonisationSite> Sites
    {
        get { lock (_gate) return _sites.Values.OrderByDescending(s => s.Updated).ToList(); }
    }

    /// <summary>The site whose depot/contribution was seen most recently, if any.</summary>
    public ColonisationSite? ActiveSite
    {
        get
        {
            lock (_gate)
                return _activeMarketId is long id && _sites.TryGetValue(id, out var s) ? s : null;
        }
    }

    /// <summary>
    /// The project the card shows on its own: the one the commander picked, else the one whose
    /// depot/contribution was seen most recently. Seeing a depot picks it, as docking always has.
    /// </summary>
    public ColonisationSite? SelectedSite
    {
        get
        {
            lock (_gate)
                return _selectedMarketId is long id && _sites.TryGetValue(id, out var s) ? s : ActiveSiteLocked();
        }
    }

    /// <summary>True when the commander wants the combined all-projects list rather than one project.</summary>
    public bool ShowAll
    {
        get { lock (_gate) return _showAll; }
    }

    /// <summary>
    /// What the single-project view and the linked shopping list should cover: every open project
    /// when <see cref="ShowAll"/>, otherwise just the <see cref="SelectedSite"/>.
    /// </summary>
    public IReadOnlyList<ColonisationSite> FocusedSites
    {
        get
        {
            lock (_gate)
            {
                if (_showAll) return _sites.Values.Where(s => s.IsOpen).OrderByDescending(s => s.Updated).ToList();
                var one = _selectedMarketId is long id && _sites.TryGetValue(id, out var s) ? s : ActiveSiteLocked();
                return one is null ? Array.Empty<ColonisationSite>() : new[] { one };
            }
        }
    }

    /// <summary>The summed outstanding commodities of every open project.</summary>
    public IReadOnlyList<CombinedCommodity> Combined => ColonisationAggregator.Combine(Sites);

    /// <summary>Shows one known project on its own. An unknown market id is ignored.</summary>
    public void Select(long marketId)
    {
        lock (_gate)
        {
            if (!_sites.ContainsKey(marketId)) return;
            if (_selectedMarketId == marketId && !_showAll) return;
            _selectedMarketId = marketId;
            _showAll = false;
        }
        Changed?.Invoke();
    }

    /// <summary>Switches between the combined all-projects list and the single-project view.</summary>
    public void SetShowAll(bool showAll)
    {
        lock (_gate)
        {
            if (_showAll == showAll) return;
            _showAll = showAll;
        }
        Changed?.Invoke();
    }

    /// <summary>Stops tracking a project (for example a finished one). It returns if its depot is seen again.</summary>
    public bool Forget(long marketId)
    {
        lock (_gate)
        {
            if (!_sites.Remove(marketId)) return false;
            if (_activeMarketId == marketId) _activeMarketId = null;
            if (_selectedMarketId == marketId) _selectedMarketId = null;
            // Fall back to the most recently updated survivor so the card keeps showing projects.
            if (_activeMarketId is null && _sites.Count > 0)
                _activeMarketId = _sites.Values.OrderByDescending(s => s.Updated).First().MarketId;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Drops every project and the selection, e.g. when a different commander logs in.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _sites.Clear();
            _activeMarketId = null;
            _selectedMarketId = null;
            _showAll = false;
        }
        Changed?.Invoke();
    }

    /// <summary>The state worth remembering across sessions.</summary>
    public ColonisationProjectData Snapshot()
    {
        lock (_gate)
            return new ColonisationProjectData
            {
                SelectedMarketId = _selectedMarketId,
                ShowAll = _showAll,
                Projects = _sites.Values.OrderBy(s => s.MarketId).Select(s => (ColonisationProjectRecord?)ColonisationProjectRecord.From(s)).ToList(),
            };
    }

    /// <summary>
    /// Folds remembered projects in. A project already seen this session is replaced only by a
    /// strictly newer saved copy, so journal events that arrived first are never rolled back. The
    /// saved selection is adopted only if nothing has been selected yet.
    /// </summary>
    public void Restore(ColonisationProjectData data)
    {
        var sites = data.ToSites();
        lock (_gate)
        {
            foreach (var site in sites)
                if (!_sites.TryGetValue(site.MarketId, out var existing) || site.Updated > existing.Updated)
                    _sites[site.MarketId] = site;

            if (_activeMarketId is null && _sites.Count > 0)
                _activeMarketId = _sites.Values.OrderByDescending(s => s.Updated).First().MarketId;
            if (_selectedMarketId is null && data.SelectedMarketId is long sel && _sites.ContainsKey(sel))
                _selectedMarketId = sel;
            _showAll = data.ShowAll || _showAll;
        }
        Changed?.Invoke();
    }

    private ColonisationSite? ActiveSiteLocked() =>
        _activeMarketId is long id && _sites.TryGetValue(id, out var s) ? s : null;

    private void OnDepot(JournalEntry e)
    {
        if (e.GetInt64("MarketID") is not long marketId) return;
        if (!e.Raw.TryGetProperty("ResourcesRequired", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;

        var resources = new List<ColonisationResource>();
        foreach (var item in arr.EnumerateArray())
        {
            var symbolRaw = ReadString(item, "Name");
            var localised = ReadString(item, "Name_Localised");
            var display = localised ?? symbolRaw ?? "Unknown";
            var symbol = CommodityName.Canonicalize(symbolRaw ?? localised);

            resources.Add(new ColonisationResource(
                Name: display,
                Symbol: symbol,
                Required: ReadInt(item, "RequiredAmount"),
                Provided: ReadInt(item, "ProvidedAmount"),
                Payment: ReadInt(item, "Payment")));
        }

        var site = new ColonisationSite
        {
            MarketId = marketId,
            StationName = _state?.StationName,
            StarSystem = _state?.StarSystem,
            Progress = e.GetDouble("ConstructionProgress") ?? 0,
            Complete = e.GetBool("ConstructionComplete") ?? false,
            Failed = e.GetBool("ConstructionFailed") ?? false,
            Updated = e.Timestamp,
            Resources = resources,
        };

        lock (_gate)
        {
            _sites[marketId] = site;
            _activeMarketId = marketId;
            _selectedMarketId = marketId;
        }
        Changed?.Invoke();
    }

    private void OnContribution(JournalEntry e)
    {
        if (e.GetInt64("MarketID") is not long marketId) return;
        if (!e.Raw.TryGetProperty("Contributions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;

        // Fold the deltas into a symbol → tons map keyed the same way as the depot resources.
        var deltas = new Dictionary<string, int>();
        foreach (var item in arr.EnumerateArray())
        {
            var symbol = CommodityName.Canonicalize(ReadString(item, "Name") ?? ReadString(item, "Name_Localised"));
            if (symbol.Length == 0) continue;
            var amount = ReadInt(item, "Amount");
            deltas[symbol] = deltas.TryGetValue(symbol, out var v) ? v + amount : amount;
        }
        if (deltas.Count == 0) return;

        lock (_gate)
        {
            // A depot snapshot is authoritative and usually follows a contribution within moments;
            // until then we apply the delta so the shopping list updates live. Unknown sites are
            // skipped rather than invented — the next depot snapshot will establish them.
            if (!_sites.TryGetValue(marketId, out var site)) return;

            var updated = site.Resources.Select(r =>
                deltas.TryGetValue(r.Symbol, out var delta)
                    ? r with { Provided = Math.Min(r.Required, r.Provided + delta) }
                    : r).ToList();

            _sites[marketId] = new ColonisationSite
            {
                MarketId = site.MarketId,
                StationName = site.StationName,
                StarSystem = site.StarSystem,
                Progress = site.Progress,
                Complete = site.Complete,
                Failed = site.Failed,
                Updated = e.Timestamp,
                Resources = updated,
            };
            _activeMarketId = marketId;
            _selectedMarketId = marketId;
        }
        Changed?.Invoke();
    }

    private static string? ReadString(JsonElement item, string prop)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int ReadInt(JsonElement item, string prop)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : 0;
}
