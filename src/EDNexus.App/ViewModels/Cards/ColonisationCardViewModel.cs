using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.Core.Colonisation;
using EDNexus.Core.State;

namespace EDNexus.App.ViewModels;

/// <summary>
/// Colonisation projects: pick one to see its progress and outstanding shopping list (and, when it is
/// registered on a shared tracker, what the whole squadron still owes), or pick "All projects" for one
/// combined list of everything still needed to finish every open build.
/// </summary>
public sealed partial class ColonisationCardViewModel : CardViewModel
{
    private string _signature = "";
    private string _pickerSignature = "";
    private string _combinedSignature = "";
    private bool _syncing;

    // The depot the shared totals were fetched for, so a lookup runs once per site rather than per tick.
    private long _sharedMarketId;

    public ColonisationCardViewModel(DashboardContext context) : base(context, "colonisation", "COLONISATION", 920) { }

    [ObservableProperty] private bool _hasColonisation;
    [ObservableProperty] private string _colonisationTitle = "—";
    [ObservableProperty] private string _colonisationStatus = "—";
    [ObservableProperty] private string _colonisationSummary = "";
    [ObservableProperty] private double _colonisationProgress;

    // --- Shared project (Raven Colonial) ---

    /// <summary>True once a shared project has been matched to this depot.</summary>
    [ObservableProperty] private bool _hasShared;

    [ObservableProperty] private bool _sharedBusy;

    /// <summary>Name the build carries on the shared tracker, plus who started it.</summary>
    [ObservableProperty] private string _sharedTitle = "";

    /// <summary>What the squadron still owes, which is what the local depot snapshot cannot say.</summary>
    [ObservableProperty] private string _sharedSummary = "";

    /// <summary>Who else is working the build, and where the figures came from.</summary>
    [ObservableProperty] private string _sharedContributors = "";

    public ObservableCollection<ShoppingLine> ShoppingList { get; } = new();

    // --- Project picker and combined view ---

    /// <summary>The picker's entries: "All projects" first, then each known project, newest first.</summary>
    public ObservableCollection<ProjectChoice> Choices { get; } = new();

    [ObservableProperty] private ProjectChoice? _selectedChoice;

    /// <summary>When off (the default), finished and failed projects are hidden from the picker.</summary>
    [ObservableProperty] private bool _showCompleted;

    /// <summary>True while the combined all-projects list is showing instead of a single project.</summary>
    [ObservableProperty] private bool _showCombined;

    [ObservableProperty] private string _combinedSummary = "";

    /// <summary>True when the selected project can be dropped from the remembered list.</summary>
    [ObservableProperty] private bool _canForget;

    public ObservableCollection<CombinedLine> CombinedList { get; } = new();

    /// <summary>Inverse of <see cref="ShowCombined"/>, for the single-project block's visibility.</summary>
    public bool ShowSingle => !ShowCombined;

    partial void OnShowCombinedChanged(bool value) => OnPropertyChanged(nameof(ShowSingle));

    partial void OnShowCompletedChanged(bool value) => _pickerSignature = "";

    partial void OnSelectedChoiceChanged(ProjectChoice? value)
    {
        if (_syncing || value is null) return;
        var tracker = Context.Host.Colonisation;
        if (value.MarketId is long id) tracker.Select(id);
        else tracker.SetShowAll(true);
        _signature = "";
        _combinedSignature = "";
    }

    /// <summary>Inverse of <see cref="HasColonisation"/>, for the empty-state hint's visibility.</summary>
    public bool NoColonisation => !HasColonisation;

    partial void OnHasColonisationChanged(bool value) => OnPropertyChanged(nameof(NoColonisation));

    public override void Update(CommanderState s)
    {
        var tracker = Context.Host.Colonisation;
        var sites = tracker.Sites;
        var site = tracker.SelectedSite;
        if (site is null)
        {
            if (HasColonisation)
            {
                HasColonisation = false;
                ShowCombined = false;
                Choices.Clear();
                ShoppingList.Clear();
                CombinedList.Clear();
                _signature = _pickerSignature = _combinedSignature = "";
                _sharedMarketId = 0;   // docking at the same depot again must look it up again
                ClearShared();
            }
            return;
        }

        HasColonisation = true;
        SyncPicker(tracker, sites, site);

        if (tracker.ShowAll)
        {
            ShowCombined = true;
            UpdateCombined(sites);
            return;
        }
        ShowCombined = false;

        // Docking at a different depot means different shared totals — look them up once, not per tick.
        // While a lookup for the previous depot is still in flight the new one has to wait: the id is
        // only claimed once its own lookup actually starts, so a later tick picks it up instead of the
        // new depot's shared load being dropped for good.
        if (site.MarketId != _sharedMarketId && !SharedBusy)
        {
            _sharedMarketId = site.MarketId;
            ClearShared();
            LoadSharedAsync(site.StarSystem, site.MarketId).Forget("Colonisation: shared project lookup");
        }

        ColonisationTitle = site.StarSystem is { Length: > 0 } sys && sys != site.DisplayName
            ? $"{site.DisplayName} · {sys}" : site.DisplayName;
        ColonisationProgress = Math.Clamp(site.Progress, 0, 1);
        ColonisationStatus = site.Complete ? "Complete"
            : site.Failed ? "Failed"
            : $"{site.Progress * 100:0.#}%";
        ColonisationSummary =
            $"{site.CompletedCount}/{site.Resources.Count} commodities · {site.TotalRemaining:N0} t remaining";

        var list = site.BuildShoppingList(s.Cargo, Context.Host.CarrierCargo.Stock);
        var signature = site.MarketId + "|" + site.Progress.ToString("0.####") + "|"
            + string.Join("|", list.Select(i => $"{i.Name}:{i.Remaining}:{i.InHold}:{i.OnCarrier}"));
        CanForget = true;
        if (signature == _signature) return;
        _signature = signature;

        ShoppingList.Clear();
        foreach (var i in list)
        {
            var parts = new List<string>(2);
            if (i.InHold > 0) parts.Add($"{i.Carrying:N0} in hold");
            if (i.OnCarrier > 0) parts.Add($"{i.OnCarrier:N0} on carrier");
            var hold = parts.Count == 0 ? ""
                : (i.CoveredByStock ? "✓ " : "") + string.Join(" · ", parts);
            ShoppingList.Add(new ShoppingLine(
                i.Name, i.Remaining.ToString("N0"), i.StillNeeded.ToString("N0"), hold,
                i.InHold > 0 || i.OnCarrier > 0, i.Fraction));
        }
    }

    public override void Reset()
    {
        _signature = _pickerSignature = _combinedSignature = "";
        _sharedMarketId = 0;
        ShoppingList.Clear();
        CombinedList.Clear();
        Choices.Clear();
        ClearShared();
    }

    /// <summary>Drop the selected project from the remembered list (it returns if its depot is seen again).</summary>
    [RelayCommand]
    private void ForgetSelected()
    {
        var tracker = Context.Host.Colonisation;
        if (!tracker.ShowAll && tracker.SelectedSite is { } site) tracker.Forget(site.MarketId);
        _signature = _pickerSignature = "";
    }

    /// <summary>Rebuild the picker only when the set of projects or the selection changed.</summary>
    private void SyncPicker(ColonisationTracker tracker, IReadOnlyList<ColonisationSite> sites, ColonisationSite selected)
    {
        var visible = sites.Where(p => ShowCompleted || p.IsOpen || p.MarketId == selected.MarketId).ToList();
        var signature = (tracker.ShowAll ? "all" : selected.MarketId.ToString())
            + "|" + ShowCompleted + "|" + string.Join("|", visible.Select(p => $"{p.MarketId}:{p.DisplayName}:{p.Progress:0.###}:{p.IsOpen}"));
        if (signature == _pickerSignature) return;
        _pickerSignature = signature;

        _syncing = true;
        try
        {
            Choices.Clear();
            Choices.Add(new ProjectChoice(null, $"All projects ({sites.Count(p => p.IsOpen)} open)"));
            foreach (var p in visible)
            {
                var state = p.Complete ? " — complete" : p.Failed ? " — failed" : !p.IsOpen ? " — supplied"
                    : $" — {p.Progress * 100:0}%";
                Choices.Add(new ProjectChoice(p.MarketId, p.DisplayName + state));
            }
            SelectedChoice = tracker.ShowAll
                ? Choices[0]
                : Choices.FirstOrDefault(c => c.MarketId == selected.MarketId);
        }
        finally { _syncing = false; }
    }

    private void UpdateCombined(IReadOnlyList<ColonisationSite> sites)
    {
        ClearShared();
        CanForget = false;
        var open = sites.Count(p => p.IsOpen);
        var combined = ColonisationAggregator.Combine(sites);
        var remaining = combined.Sum(c => (long)c.Remaining);
        CombinedSummary = open == 0
            ? "No open projects — everything known is complete."
            : $"{open} open project{(open == 1 ? "" : "s")} · {combined.Count} commodities · {remaining:N0} t remaining";

        var signature = string.Join("|", combined.Select(c =>
            $"{c.Symbol}:{c.Remaining}:" + string.Join(",", c.Projects.Select(p => $"{p.MarketId}={p.Remaining}"))));
        if (signature == _combinedSignature) return;
        _combinedSignature = signature;

        CombinedList.Clear();
        foreach (var c in combined)
        {
            // One project owing it needs no breakdown line; several get "Name 120 · Name 40".
            var breakdown = c.Projects.Count <= 1 ? ""
                : string.Join(" · ", c.Projects.Select(p => $"{p.ProjectName} {p.Remaining:N0}"));
            CombinedList.Add(new CombinedLine(c.Name, c.Remaining.ToString("N0"), breakdown, c.Fraction));
        }
    }

    /// <summary>Re-read the shared totals — squadmates deliver while the commander is flying.</summary>
    [RelayCommand]
    private Task RefreshShared()
    {
        var site = Context.Host.Colonisation.SelectedSite;
        if (site is not null) _sharedMarketId = site.MarketId;   // a manual refresh claims the depot too
        return site is null ? Task.CompletedTask : LoadSharedAsync(site.StarSystem, site.MarketId);
    }

    private void ClearShared()
    {
        HasShared = false;
        SharedTitle = "";
        SharedSummary = "";
        SharedContributors = "";
    }

    /// <summary>
    /// Pull the squadron-wide totals for this depot. Most builds are solo and never registered, so
    /// finding nothing is the ordinary outcome and simply leaves the shared block hidden.
    /// </summary>
    private async Task LoadSharedAsync(string? systemName, long marketId)
    {
        // Sends the depot's system name and market id to a third party, so it is opt-out in Settings.
        if (!Context.SharedProjectLookupEnabled) { ClearShared(); return; }
        if (SharedBusy || string.IsNullOrWhiteSpace(systemName) || marketId <= 0) return;

        SharedBusy = true;
        try
        {
            var lookup = Context.Host.SharedProjects;
            var shared = await lookup.GetForDepotAsync(systemName, marketId, CancellationToken.None);
            // Undocked, or docked somewhere else, while the lookup was in flight: these totals belong
            // to a depot the card is no longer showing.
            if (shared is null || Context.Host.Colonisation.SelectedSite?.MarketId != marketId) return;

            var architect = string.IsNullOrWhiteSpace(shared.Architect) ? "" : $" · started by {shared.Architect}";
            SharedTitle = $"{shared.BuildName}{architect}";

            var delivered = Math.Max(0, shared.MaxNeed - shared.SumRemaining);
            SharedSummary = shared.Complete
                ? "Shared tracker reports this build complete."
                : $"Squadron-wide: {shared.SumRemaining:N0} t still needed · {delivered:N0} of {shared.MaxNeed:N0} t delivered";

            SharedContributors = shared.Contributors.Count switch
            {
                0 => lookup.SourceName,
                1 => $"{shared.Contributors[0]} contributing · {lookup.SourceName}",
                var n => $"{n} commanders contributing · {lookup.SourceName}",
            };

            HasShared = true;
        }
        catch (Exception)
        {
            // The local depot snapshot is authoritative for this commander's own deliveries, so a
            // shared lookup that fails simply leaves the extra block off the card.
            ClearShared();
        }
        finally
        {
            SharedBusy = false;
        }
    }
}

/// <summary>A picker entry: one project, or the combined list when <see cref="MarketId"/> is null.</summary>
public sealed record ProjectChoice(long? MarketId, string Label)
{
    public override string ToString() => Label;
}

/// <param name="Breakdown">Per-project shares ("Born's Pride 120 · New Horizon 40"), empty when one project owes it.</param>
public sealed record CombinedLine(string Name, string Remaining, string Breakdown, double Fraction)
{
    public bool HasBreakdown => Breakdown.Length > 0;
}

/// <param name="HoldNote">"✓ 648 in hold" / "648 in hold" / "" — highlights what's already aboard.</param>
/// <param name="Fraction">Delivery progress for this commodity (0..1), for the per-row bar.</param>
public sealed record ShoppingLine(
    string Name, string Remaining, string ToBuy, string HoldNote, bool Carrying, double Fraction);
