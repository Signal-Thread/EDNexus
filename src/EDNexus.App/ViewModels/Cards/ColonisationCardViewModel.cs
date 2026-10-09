using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.Core.State;

namespace EDNexus.App.ViewModels;

/// <summary>
/// Active colonisation construction site: progress, the outstanding shopping list, and — when the
/// build is registered on a shared tracker — what the whole squadron still owes it.
/// </summary>
public sealed partial class ColonisationCardViewModel : CardViewModel
{
    private string _signature = "";

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

    /// <summary>Inverse of <see cref="HasColonisation"/>, for the empty-state hint's visibility.</summary>
    public bool NoColonisation => !HasColonisation;

    partial void OnHasColonisationChanged(bool value) => OnPropertyChanged(nameof(NoColonisation));

    public override void Update(CommanderState s)
    {
        var site = Context.Host.Colonisation.ActiveSite;
        if (site is null)
        {
            if (HasColonisation)
            {
                HasColonisation = false;
                ShoppingList.Clear();
                _signature = "";
                _sharedMarketId = 0;   // docking at the same depot again must look it up again
                ClearShared();
            }
            return;
        }

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

        HasColonisation = true;
        ColonisationTitle = site.StationName ?? site.StarSystem ?? "Construction site";
        ColonisationProgress = Math.Clamp(site.Progress, 0, 1);
        ColonisationStatus = site.Complete ? "Complete"
            : site.Failed ? "Failed"
            : $"{site.Progress * 100:0.#}%";
        ColonisationSummary =
            $"{site.CompletedCount}/{site.Resources.Count} commodities · {site.TotalRemaining:N0} t remaining";

        var list = site.BuildShoppingList(s.Cargo);
        var signature = site.MarketId + "|" + site.Progress.ToString("0.####") + "|"
            + string.Join("|", list.Select(i => $"{i.Name}:{i.Remaining}:{i.InHold}"));
        if (signature == _signature) return;
        _signature = signature;

        ShoppingList.Clear();
        foreach (var i in list)
        {
            var hold = i.InHold <= 0 ? ""
                : i.CoveredByHold ? $"✓ {i.Carrying:N0} in hold"
                : $"{i.Carrying:N0} in hold";
            ShoppingList.Add(new ShoppingLine(
                i.Name, i.Remaining.ToString("N0"), i.StillNeeded.ToString("N0"), hold, i.InHold > 0, i.Fraction));
        }
    }

    public override void Reset()
    {
        _signature = "";
        _sharedMarketId = 0;
        ShoppingList.Clear();
        ClearShared();
    }

    /// <summary>Re-read the shared totals — squadmates deliver while the commander is flying.</summary>
    [RelayCommand]
    private Task RefreshShared()
    {
        var site = Context.Host.Colonisation.ActiveSite;
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
            if (shared is null || Context.Host.Colonisation.ActiveSite?.MarketId != marketId) return;

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

/// <param name="HoldNote">"✓ 648 in hold" / "648 in hold" / "" — highlights what's already aboard.</param>
/// <param name="Fraction">Delivery progress for this commodity (0..1), for the per-row bar.</param>
public sealed record ShoppingLine(
    string Name, string Remaining, string ToBuy, string HoldNote, bool Carrying, double Fraction);
