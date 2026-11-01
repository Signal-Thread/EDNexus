using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.Core.Ship;
using EDNexus.Core.State;

namespace EDNexus.App.ViewModels;

/// <summary>The active ship and its main-tank fuel level.</summary>
public sealed partial class ShipCardViewModel : CardViewModel
{
    public ShipCardViewModel(DashboardContext context) : base(context, "ship", "SHIP", 452) { }

    [ObservableProperty] private string _ship = "—";
    [ObservableProperty] private string _fuel = "—";
    [ObservableProperty] private double _fuelFraction;
    [ObservableProperty] private string? _coriolisStatus;

    // Planning figures. Each is "—" until the Loadout that supplies it has been seen.
    [ObservableProperty] private string _jumpRangeNow = "—";
    [ObservableProperty] private string _jumpRangeDetail = "";
    [ObservableProperty] private string _jumpsPerTank = "—";
    [ObservableProperty] private string _mass = "—";
    [ObservableProperty] private string _cargo = "—";
    [ObservableProperty] private string _powerRetracted = "—";
    [ObservableProperty] private string _powerDeployed = "—";
    [ObservableProperty] private string _powerNote = "";
    [ObservableProperty] private double _powerFraction;
    [ObservableProperty] private bool _hasPowerNote;
    [ObservableProperty] private string _powerHeading = "Power";

    public override void Update(CommanderState s)
    {
        Ship = FormatShip(s);
        Fuel = s.FuelCapacity > 0 ? $"{s.FuelMain:0.0} / {s.FuelCapacity:0.0} t" : $"{s.FuelMain:0.0} t";
        FuelFraction = s.FuelCapacity > 0 ? Math.Clamp(s.FuelMain / s.FuelCapacity, 0, 1) : 0;
        UpdateJump(s);
        UpdatePower(s);
    }

    private void UpdateJump(CommanderState s)
    {
        if (s.Fsd is not { BaseMass: > 0 } fsd)
        {
            JumpRangeNow = Mass = Cargo = JumpsPerTank = "—";
            JumpRangeDetail = "";
            return;
        }

        var now = fsd.JumpRangeWith(s.FuelMain, s.CargoTons);
        JumpRangeNow = $"{now:0.0} ly";
        // The journal's own figure is the best case (tank full, hold empty) and includes any Guardian
        // booster; the figures we derive don't, hence the note when one is fitted.
        var best = fsd.MaxJumpRange > 0 ? fsd.MaxJumpRange : fsd.JumpRangeWith(fsd.TankSize, 0);
        var full = fsd.JumpRangeWith(fsd.TankSize, fsd.CargoCapacity);
        JumpRangeDetail = $"best {best:0.0} ly (empty hold) · {full:0.0} ly with a full hold"
            + (fsd.RangeBoost > 0 ? " · excludes Guardian booster" : "");
        JumpsPerTank = $"{fsd.FullRangeJumps(s.FuelMain):0.#} full-range jumps ({fsd.MaxFuelPerJump:0.0} t each)";
        Mass = $"{fsd.MassWith(s.FuelMain, s.CargoTons):#,0.0} t";
        Cargo = fsd.CargoCapacity > 0 ? $"{s.CargoTons:0} / {fsd.CargoCapacity:0} t" : $"{s.CargoTons:0} t";
    }

    private void UpdatePower(CommanderState s)
    {
        PowerHeading = s.HardpointsDeployed ? "Power · hardpoints deployed" : "Power · hardpoints retracted";
        if (s.Power is not { Capacity: > 0 } power)
        {
            PowerRetracted = PowerDeployed = "—";
            PowerNote = "";
            PowerFraction = 0;
            HasPowerNote = false;
            return;
        }

        PowerRetracted = FormatPower(power.RetractedDraw, power.Capacity);
        PowerDeployed = FormatPower(power.DeployedDraw, power.Capacity);
        var draw = power.Draw(s.HardpointsDeployed);
        PowerFraction = Math.Clamp(draw / power.Capacity, 0, 1);
        var overloaded = power.DeployedDraw > power.Capacity;
        PowerNote = overloaded
            ? "Deploying hardpoints overloads the power plant."
            : power.UnknownModules > 0 ? $"{power.UnknownModules} module(s) not in the power table; draw may be higher." : "";
        HasPowerNote = PowerNote.Length > 0;
    }

    private static string FormatPower(double draw, double capacity)
        => $"{draw:0.0} / {capacity:0.0} MW ({draw / capacity:P0})";

    /// <summary>Open the current build in Coriolis, so it can be inspected or shared as a link.</summary>
    [RelayCommand]
    private void OpenCoriolisBuild()
    {
        var url = CoriolisShareLink.Build(Context.Host.State.LastLoadoutJson);
        if (url is null)
        {
            CoriolisStatus = "No ship loadout seen yet.";
            return;
        }

        OpenUrl(url);
        CoriolisStatus = "Opened in Coriolis.";
    }

    /// <summary>Copy the current build's Coriolis link, for pasting elsewhere.</summary>
    [RelayCommand]
    private async Task CopyCoriolisLink()
    {
        var url = CoriolisShareLink.Build(Context.Host.State.LastLoadoutJson);
        if (url is null)
        {
            CoriolisStatus = "No ship loadout seen yet.";
            return;
        }

        await CopyToClipboardAsync(url);
        CoriolisStatus = "Coriolis link copied.";
    }

    private static string FormatShip(CommanderState s)
    {
        if (string.IsNullOrEmpty(s.Ship)) return "—";
        var label = string.IsNullOrEmpty(s.ShipName) ? s.Ship : $"{s.Ship} · {s.ShipName}";
        return string.IsNullOrEmpty(s.ShipIdent) ? label : $"{label}  [{s.ShipIdent}]";
    }
}
