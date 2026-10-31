using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.Core.Settings;
using EDNexus.Core.ShoppingList;
using EDNexus.Core.State;

namespace EDNexus.App.ViewModels;

/// <summary>
/// Unified shopping list: the commander's hand-entered lines plus (optionally) the active colonisation
/// depot and the pinned engineering goal, merged and projected against the live inventory. The
/// projection and the manual-list edits live in <see cref="ShoppingListService"/>; this class only
/// gathers entries, shows rows, and saves settings.
/// </summary>
public sealed partial class ShoppingListCardViewModel : CardViewModel
{
    private string _signature = "";
    private bool _syncing;

    public ShoppingListCardViewModel(DashboardContext context) : base(context, "shopping", "SHOPPING LIST", 920) { }

    /// <summary>Projected rows, biggest shortfall first.</summary>
    public ObservableCollection<ShoppingRowViewModel> Rows { get; } = new();

    /// <summary>Kinds offered by the add-item picker.</summary>
    public ShoppingItemKind[] Kinds { get; } = Enum.GetValues<ShoppingItemKind>();

    [ObservableProperty] private bool _linkColonisation;
    [ObservableProperty] private bool _linkEngineering;
    [ObservableProperty] private bool _hideCompleted;

    [ObservableProperty] private ShoppingItemKind _newKind = ShoppingItemKind.Commodity;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddItemCommand))]
    private string _newName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddItemCommand))]
    private int _newQuantity = 1;

    /// <summary>True when the list has no lines at all, for the empty-state text.</summary>
    public bool IsEmpty => Rows.Count == 0 && !IsAllDone;

    /// <summary>True when every line is satisfied and "hide completed" is hiding them all.</summary>
    [ObservableProperty] private bool _isAllDone;

    /// <summary>True when there are rows to show (drives the table's visibility).</summary>
    public bool HasRows => Rows.Count > 0;

    partial void OnIsAllDoneChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnLinkColonisationChanged(bool value) => OnToggleChanged();
    partial void OnLinkEngineeringChanged(bool value) => OnToggleChanged();
    partial void OnHideCompletedChanged(bool value) => OnToggleChanged();

    private void OnToggleChanged()
    {
        if (_syncing) return;
        var current = Context.GetShoppingSettings();
        Context.SaveShoppingSettings(new ShoppingListSettings
        {
            Manual = new List<ShoppingEntry>(current.Manual),
            LinkColonisation = LinkColonisation,
            LinkEngineering = LinkEngineering,
            HideCompleted = HideCompleted,
        });
        _signature = "";   // re-project on the next tick
    }

    public override void Update(CommanderState s)
    {
        var settings = Context.GetShoppingSettings();

        _syncing = true;
        try
        {
            LinkColonisation = settings.LinkColonisation;
            LinkEngineering = settings.LinkEngineering;
            HideCompleted = settings.HideCompleted;
        }
        finally
        {
            _syncing = false;
        }

        var entries = new List<ShoppingEntry>(settings.Manual);
        if (settings.LinkColonisation)
            entries.AddRange(ShoppingListService.FromColonisation(Context.Host.Colonisation.ActiveSite));
        if (settings.LinkEngineering)
            entries.AddRange(ShoppingListService.FromEngineering(Context.GetEngineeringPin(), Context.Host.Engineering, s));

        var rows = ShoppingListService.Build(entries, s, settings.HideCompleted);
        IsAllDone = rows.Count == 0 && settings.HideCompleted && ShoppingListService.Build(entries, s).Count > 0;

        var signature = string.Join("|", rows.Select(r =>
            $"{r.Kind}:{r.Key}:{r.Name}:{r.Needed}:{r.Held}:{string.Join(',', r.Sources)}:{r.Hint}"));
        if (signature == _signature) return;
        _signature = signature;

        Rows.Clear();
        foreach (var r in rows) Rows.Add(new ShoppingRowViewModel(r));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
    }

    public override void Reset()
    {
        _signature = "";
        Rows.Clear();
        IsAllDone = false;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
    }

    private bool CanAddItem() => !string.IsNullOrWhiteSpace(NewName) && NewQuantity > 0;

    [RelayCommand(CanExecute = nameof(CanAddItem))]
    private void AddItem()
    {
        var current = Context.GetShoppingSettings();
        var updated = ShoppingListService.AddManual(current, NewKind, NewName, NewQuantity);
        if (updated.Manual.SequenceEqual(current.Manual)) return;   // nothing changed: no write, keep the input
        Context.SaveShoppingSettings(updated);
        NewName = "";
        NewQuantity = 1;
        _signature = "";
    }

    [RelayCommand]
    private void RemoveManual(ShoppingRowViewModel? row)
    {
        if (row is null || !row.IsManual) return;
        Context.SaveShoppingSettings(ShoppingListService.RemoveManual(Context.GetShoppingSettings(), row.Kind, row.Key));
        _signature = "";
    }

    [RelayCommand]
    private void Find(ShoppingRowViewModel? row)
    {
        // The display name, never the stemmed key, is what the market search understands.
        if (row is { CanFind: true }) Context.FindToBuy?.Invoke(row.Name);
    }
}

/// <summary>One displayed shopping-list row: a <see cref="ShoppingRow"/> with presentation strings.</summary>
public sealed class ShoppingRowViewModel
{
    public ShoppingRowViewModel(ShoppingRow row)
    {
        Kind = row.Kind;
        Key = row.Key;
        Name = row.Name;
        Needed = row.Needed;
        Held = row.Held;
        StillNeeded = row.StillNeeded;
        Satisfied = row.Satisfied;
        CanFind = row.CanFind;
        Hint = row.Hint;
        Progress = row.Fraction;
        IsManual = row.Sources.Contains(ShoppingItemSource.Manual);
        SourceText = string.Join(" · ", row.Sources.Select(SourceLabel));
        KindText = row.Kind.ToString();
        HeldText = $"{row.Held:N0} / {row.Needed:N0}";
        StillNeededText = row.Satisfied ? "✓" : row.StillNeeded.ToString("N0");
    }

    public ShoppingItemKind Kind { get; }
    /// <summary>Normalised row key (stemmed for commodities); use <see cref="Name"/> for searches.</summary>
    public string Key { get; }
    public string Name { get; }
    public int Needed { get; }
    public int Held { get; }
    public int StillNeeded { get; }
    public bool Satisfied { get; }
    public bool CanFind { get; }
    public string Hint { get; }
    public bool HasHint => Hint.Length > 0;

    /// <summary>Fraction held of needed, 0..1.</summary>
    public double Progress { get; }

    /// <summary>True when the row has a manual source, so it can be removed.</summary>
    public bool IsManual { get; }

    /// <summary>Where the line comes from, e.g. "Depot · Manual".</summary>
    public string SourceText { get; }
    public string KindText { get; }
    public string HeldText { get; }
    public string StillNeededText { get; }

    private static string SourceLabel(ShoppingItemSource s) => s switch
    {
        ShoppingItemSource.Colonisation => "Depot",
        ShoppingItemSource.Engineering => "Engineering",
        _ => "Manual",
    };
}
