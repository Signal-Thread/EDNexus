using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.Core;
using EDNexus.Core.Dev;
using EDNexus.Core.Mining;
using EDNexus.Core.Settings;
using EDNexus.Core.State;

namespace EDNexus.App.ViewModels;

/// <summary>
/// Ambient services a card may need beyond the per-tick state snapshot: the current engine host, a
/// developer-mode predicate, and a shared RNG for the offline sample services. The host is fetched
/// through a delegate rather than cached because "reset to live" rebuilds it — a card that held a
/// stale reference would keep reading the disposed engine.
/// </summary>
public sealed class DashboardContext
{
    private readonly Func<EngineHost> _host;
    private readonly Func<bool> _devEnabled;
    private readonly Func<EngineeringSettings> _getEngineeringPin;
    private readonly Action<string?, int> _saveEngineeringPin;
    private readonly Action<bool> _saveEngineeringOnFootMode;
    private readonly Action<string?, string?, int> _saveOnFootPin;
    private readonly Func<RouteSettings> _getSavedRoute;
    private readonly Action<RouteSettings> _saveRoute;
    private readonly Func<MiningSettings> _getMiningSettings;
    private readonly Action<IEnumerable<(string Symbol, int MeanPrice)>> _learnCommodityPrices;
    private readonly Action<DateTimeOffset> _ensureMiningSessionDate;
    private readonly Action<DateTimeOffset, long> _recordMiningRefined;
    private readonly Action<RefinedUnit, int> _recordMiningSpot;
    private readonly Func<bool> _sharedProjectLookupEnabled;

    public DashboardContext(
        Func<EngineHost> host,
        Func<bool> devEnabled,
        Random rng,
        Func<EngineeringSettings> getEngineeringPin,
        Action<string?, int> saveEngineeringPin,
        Action<bool> saveEngineeringOnFootMode,
        Action<string?, string?, int> saveOnFootPin,
        Func<RouteSettings> getSavedRoute,
        Action<RouteSettings> saveRoute,
        Func<MiningSettings> getMiningSettings,
        Action<IEnumerable<(string Symbol, int MeanPrice)>> learnCommodityPrices,
        Action<DateTimeOffset> ensureMiningSessionDate,
        Action<DateTimeOffset, long> recordMiningRefined,
        Action<RefinedUnit, int> recordMiningSpot,
        RadioPlayerSelector radio,
        Func<bool>? sharedProjectLookupEnabled = null)
    {
        _host = host;
        Radio = radio;
        _devEnabled = devEnabled;
        Rng = rng;
        _getEngineeringPin = getEngineeringPin;
        _saveEngineeringPin = saveEngineeringPin;
        _saveEngineeringOnFootMode = saveEngineeringOnFootMode;
        _saveOnFootPin = saveOnFootPin;
        _getSavedRoute = getSavedRoute;
        _saveRoute = saveRoute;
        _getMiningSettings = getMiningSettings;
        _learnCommodityPrices = learnCommodityPrices;
        _ensureMiningSessionDate = ensureMiningSessionDate;
        _recordMiningRefined = recordMiningRefined;
        _recordMiningSpot = recordMiningSpot;
        _sharedProjectLookupEnabled = sharedProjectLookupEnabled ?? (() => true);
    }

    /// <summary>The live engine host — always the current one, even after a reset-to-live rebuild.</summary>
    public EngineHost Host => _host();

    /// <summary>True while developer mode is active (cards then run against offline generators).</summary>
    public bool DevEnabled => _devEnabled();

    public Random Rng { get; }

    /// <summary>
    /// The radio: <see cref="RadioPlayerSelector.Active"/> is what the card and title bar drive (the
    /// simulation while developer mode is on). App-lifetime, so it survives host rebuilds.
    /// </summary>
    public RadioPlayerSelector Radio { get; }

    /// <summary>Read the persisted engineering pin (blueprint id + grade, and the on-foot pin/mode).</summary>
    public EngineeringSettings GetEngineeringPin() => _getEngineeringPin();

    /// <summary>Persist the engineering pin; a null blueprint id clears it.</summary>
    public void SaveEngineeringPin(string? blueprintId, int grade) => _saveEngineeringPin(blueprintId, grade);

    /// <summary>Persist the Engineering card's Ship / On-foot toggle.</summary>
    public void SaveEngineeringOnFootMode(bool onFootMode) => _saveEngineeringOnFootMode(onFootMode);

    /// <summary>Persist the pinned on-foot suit/weapon; a null id clears it.</summary>
    public void SaveOnFootPin(string? kind, string? id, int grade) => _saveOnFootPin(kind, id, grade);

    /// <summary>Read the route plotter's last plotted route, so it can restore across a restart.</summary>
    public RouteSettings GetSavedRoute() => _getSavedRoute();

    /// <summary>Persist the route plotter's route (an empty/default instance clears it).</summary>
    public void SaveRoute(RouteSettings route) => _saveRoute(route);

    /// <summary>Read the mining card's price threshold and learned galactic-average prices.</summary>
    public MiningSettings GetMiningSettings() => _getMiningSettings();

    /// <summary>Fold newly observed galactic-average prices into the learned price book.</summary>
    public void LearnCommodityPrices(IEnumerable<(string Symbol, int MeanPrice)> prices) => _learnCommodityPrices(prices);

    /// <summary>Roll the mining day over if the local date has changed since it was last checked.</summary>
    public void EnsureMiningSessionDate(DateTimeOffset now) => _ensureMiningSessionDate(now);

    /// <summary>Record one refined unit against today's running mining total.</summary>
    public void RecordMiningRefined(DateTimeOffset when, long credits) => _recordMiningRefined(when, credits);

    /// <summary>Record a unit refined from an SRV as a planetary mining spot worth returning to.</summary>
    public void RecordMiningSpot(RefinedUnit unit, int averagePrice) => _recordMiningSpot(unit, averagePrice);

    /// <summary>
    /// Whether the colonisation card may look the active depot up on the shared project tracker
    /// (Raven Colonial). Doing so sends the system name and market id to that third-party service.
    /// </summary>
    public bool SharedProjectLookupEnabled => _sharedProjectLookupEnabled();
}

/// <summary>
/// A single dashboard card. Each card owns its own slice of the commander state and refreshes itself
/// from a state snapshot on every tick, so the shell view model no longer has to know what any card
/// contains. Identity (<see cref="Id"/>), placement (<see cref="Width"/>), and the show/collapse flags
/// are the hooks the shell — and, later, a customisable layout — drive.
/// </summary>
public abstract partial class CardViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    protected DashboardContext Context { get; }

    protected CardViewModel(DashboardContext context, string id, string title, double width)
    {
        Context = context;
        Id = id;
        _baseTitle = title;
        _width = width;
        DefaultWidth = width;
    }

    /// <summary>The width this card ships with, so "reset layout" can restore it.</summary>
    public double DefaultWidth { get; }

    /// <summary>
    /// Raised when the commander changes something the layout persists. The shell subscribes and
    /// saves; the card itself has no idea a settings store exists.
    /// </summary>
    public event Action<CardViewModel>? LayoutChanged;

    /// <summary>Apply a saved arrangement without raising <see cref="LayoutChanged"/>.</summary>
    public void ApplyLayout(bool visible, double width, bool collapsed, int column = CardLayout.AutoColumn)
    {
        _applyingLayout = true;
        try
        {
            IsVisible = visible;
            Width = width > 0 ? width : DefaultWidth;
            IsCollapsed = collapsed;
            Column = column;
        }
        finally
        {
            _applyingLayout = false;
        }
    }

    private bool _applyingLayout;

    private void RaiseLayoutChanged()
    {
        if (!_applyingLayout) LayoutChanged?.Invoke(this);
    }

    /// <summary>Stable key, aligned with the dev-mode sample source keys (e.g. "location", "market").</summary>
    public string Id { get; }

    private readonly string _baseTitle;

    /// <summary>
    /// Header text shown on the card. Carries a "paused" marker while the card is
    /// <see cref="IsFaulted"/>, so the failure is visible without every card template needing a
    /// banner of its own.
    /// </summary>
    public string Title => IsFaulted ? $"{_baseTitle}  ⚠ paused" : _baseTitle;

    /// <summary>How many consecutive failing updates it takes to pause a card.</summary>
    public const int MaxConsecutiveFailures = 5;

    private int _consecutiveFailures;

    /// <summary>
    /// True once <see cref="MaxConsecutiveFailures"/> updates in a row have thrown. A paused card is
    /// no longer refreshed — it keeps its last good contents instead of taking the dashboard down on
    /// every tick — until <see cref="ClearFault"/> (a reset-to-live) gives it another chance.
    /// </summary>
    public bool IsFaulted { get; private set; }

    /// <summary>What went wrong the last time the card's update threw, or null.</summary>
    public string? FaultMessage { get; private set; }

    /// <summary>
    /// Update the card from <paramref name="state"/>, containing any failure. The dashboard ticks
    /// every 250 ms, so one card throwing on an odd state would otherwise take the whole app down.
    /// </summary>
    /// <param name="onFailure">Called for every failing update with the failure count so far and whether the card is now paused.</param>
    /// <returns>True when the update ran cleanly (or the card is paused and was skipped).</returns>
    public bool TryUpdate(CommanderState state, Action<CardViewModel, Exception, int, bool>? onFailure = null)
    {
        if (IsFaulted) return true;

        try
        {
            Update(state);
            _consecutiveFailures = 0;
            return true;
        }
        catch (Exception ex) when (!UiExceptionPolicy.IsFatal(ex))
        {
            _consecutiveFailures++;
            var pause = _consecutiveFailures >= MaxConsecutiveFailures;
            if (pause)
            {
                IsFaulted = true;
                FaultMessage = ex.Message;
                OnPropertyChanged(nameof(IsFaulted));
                OnPropertyChanged(nameof(FaultMessage));
                OnPropertyChanged(nameof(Title));
            }

            onFailure?.Invoke(this, ex, _consecutiveFailures, pause);
            return false;
        }
    }

    /// <summary>Let a paused card update again (and forget its failure count).</summary>
    public void ClearFault()
    {
        _consecutiveFailures = 0;
        if (!IsFaulted) return;

        IsFaulted = false;
        FaultMessage = null;
        OnPropertyChanged(nameof(IsFaulted));
        OnPropertyChanged(nameof(FaultMessage));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Whether this card supports the dev-mode 🎲 reshuffle (only cards with a sample source do).</summary>
    public virtual bool CanRandomize => true;

    [ObservableProperty] private double _width;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isCollapsed;

    /// <summary>
    /// Dashboard column this card was dropped into, or <see cref="CardLayout.AutoColumn"/> while it
    /// is still packed automatically. Set through <see cref="ApplyLayout"/> by the shell, never by
    /// the card itself.
    /// </summary>
    [ObservableProperty] private int _column = CardLayout.AutoColumn;

    /// <summary>Glyph for the collapse toggle, so the header needs no bool-to-text converter.</summary>
    public string CollapseGlyph => IsCollapsed ? "▸" : "▾";

    /// <summary>True when the card is at its wide size.</summary>
    public bool IsWide => Width >= WideWidth;

    /// <summary>Glyph for the width toggle: shrink when wide, expand when narrow.</summary>
    public string WidthGlyph => IsWide ? "▭" : "▬";

    /// <summary>
    /// How many dashboard columns the card claims. The dashboard packs cards into as many columns
    /// as the window fits rather than at a fixed pixel width, but <see cref="Width"/> stays the
    /// persisted form of the choice so saved and exported layouts keep working unchanged.
    /// </summary>
    public int ColumnSpan => IsWide ? 2 : 1;

    // Nominal footprint of a card at each size; only the ratio between them still shows on screen.
    private const double NarrowWidth = 452;
    private const double WideWidth = 920;

    partial void OnIsCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(CollapseGlyph));
        RaiseLayoutChanged();
    }

    partial void OnIsVisibleChanged(bool value) => RaiseLayoutChanged();

    partial void OnWidthChanged(double value)
    {
        OnPropertyChanged(nameof(IsWide));
        OnPropertyChanged(nameof(WidthGlyph));
        OnPropertyChanged(nameof(ColumnSpan));
        RaiseLayoutChanged();
    }

    [RelayCommand]
    private void ToggleCollapse() => IsCollapsed = !IsCollapsed;

    /// <summary>Swap between the half-row and full-row size.</summary>
    [RelayCommand]
    private void ToggleWidth() => Width = IsWide ? NarrowWidth : WideWidth;

    /// <summary>Hide the card. It comes back from Settings → Dashboard.</summary>
    [RelayCommand]
    private void Hide() => IsVisible = false;

    /// <summary>Pull fresh values from the latest state snapshot. Called on the UI thread each tick.</summary>
    public abstract void Update(CommanderState state);

    /// <summary>Drop any cached diff/collection state so the next update rebuilds from scratch.</summary>
    public virtual void Reset() { }

    /// <summary>Copy text to the OS clipboard via the active desktop window.</summary>
    protected static async Task CopyToClipboardAsync(string text)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>Open a URL in the OS's default browser.</summary>
    protected static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }
}
