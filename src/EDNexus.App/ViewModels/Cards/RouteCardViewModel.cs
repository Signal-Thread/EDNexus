using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.Core.Dev;
using EDNexus.Core.Routes;
using EDNexus.Core.Settings;
using EDNexus.Core.Ship;
using EDNexus.Core.State;

namespace EDNexus.App.ViewModels;

/// <summary>
/// Route plotter: a plot between two systems with a stepper over the waypoints. Three modes — the
/// neutron highway (boosting off neutron stars), a plain no-boost route modelled on the ship's FSD, and
/// a fleet-carrier run (500 ly hops with tritium tracking). Backed by the live Spansh plotter, or an
/// offline sample generator while developer mode is on.
/// </summary>
public sealed partial class RouteCardViewModel : CardViewModel
{
    private SampleRoutePlotter? _sampleRoutes;
    private ShipFsdProfile? _fsd;
    private double _cargoTons;
    private double _carrierUsedCapacity;

    /// <summary>The plotted route behind the displayed hops — the source of truth <see cref="PersistRoute"/> saves from.</summary>
    private RoutePlan? _lastPlan;

    /// <summary>True while <see cref="RestoreSavedRoute"/> is applying a saved route, so it doesn't re-save itself.</summary>
    private bool _restoring;

    public RouteCardViewModel(DashboardContext context) : base(context, "route", "ROUTE PLOTTER", 452)
    {
        RefreshMode();
        RestoreSavedRoute();
    }

    /// <summary>No dev-mode sample source feeds this card, so it has no 🎲 reshuffle.</summary>
    public override bool CanRandomize => false;

    private IRoutePlotter Plotter => Context.DevEnabled ? _sampleRoutes ??= new SampleRoutePlotter(Context.Rng) : Context.Host.Routes;

    [ObservableProperty] private string _routeFrom = "";
    [ObservableProperty] private string _routeTo = "";
    [ObservableProperty] private string _routeJumpRange = "50";
    [ObservableProperty] private string _routeStatus = "";
    [ObservableProperty] private string _routeSummary = "";
    [ObservableProperty] private bool _routeBusy;
    [ObservableProperty] private bool _routeHasPlan;
    [ObservableProperty] private string _routeNextSystem = "—";
    [ObservableProperty] private int _routeStepIndex;

    // Mode selection — three radio buttons in one group. Neutron highway is the default.
    [ObservableProperty] private bool _modeNeutron = true;
    [ObservableProperty] private bool _modeNoBoost;
    [ObservableProperty] private bool _modeCarrier;

    /// <summary>The jump-range field only matters to the neutron plot; no-boost reads the ship, carrier is fixed 500 ly.</summary>
    [ObservableProperty] private bool _showJumpRange = true;

    /// <summary>Live fleet-carrier tritium reserve, shown while carrier mode is selected.</summary>
    [ObservableProperty] private bool _showCarrierFuel;
    [ObservableProperty] private string _carrierFuelText = "";

    /// <summary>A pending carrier jump (from CarrierJumpRequest) that hasn't arrived yet — explains a lagging location.</summary>
    [ObservableProperty] private bool _showCarrierPending;
    [ObservableProperty] private string _carrierPendingText = "";

    /// <summary>A short hint under the mode selector explaining the active mode / any prerequisite.</summary>
    [ObservableProperty] private string _modeHint = "";

    public ObservableCollection<RouteHopLine> RouteHops { get; } = new();

    private RouteMode CurrentMode => ModeCarrier ? RouteMode.FleetCarrier : ModeNoBoost ? RouteMode.NoBoost : RouteMode.NeutronHighway;

    partial void OnModeNeutronChanged(bool value) => RefreshMode();
    partial void OnModeNoBoostChanged(bool value) => RefreshMode();
    partial void OnModeCarrierChanged(bool value) => RefreshMode();

    private void RefreshMode()
    {
        ShowJumpRange = CurrentMode == RouteMode.NeutronHighway;
        ShowCarrierFuel = CurrentMode == RouteMode.FleetCarrier;
        ModeHint = CurrentMode switch
        {
            RouteMode.FleetCarrier => "Fleet carrier: fixed 500 ly hops, tritium-fuelled — no neutron boosting.",
            RouteMode.NoBoost => _fsd is { } f
                ? $"No boost: plain jumps at your ship's ~{ShipRange(f):N1} ly range."
                : Context.DevEnabled
                    ? "No boost: plain jumps at your ship's range."
                    : "No boost needs your ship's FSD — jump once in-game so EDNexus reads the Loadout.",
            _ => "Neutron highway: boosts off neutron stars for the fewest jumps.",
        };
    }

    /// <summary>Pre-fill the origin from live state, and track the ship FSD / carrier fuel the plot needs.</summary>
    public override void Update(CommanderState s)
    {
        if (RouteFrom.Length == 0 && s.StarSystem is { Length: > 0 } sys) RouteFrom = sys;

        _fsd = s.Fsd;
        _cargoTons = s.CargoTons;
        _carrierUsedCapacity = s.CarrierUsedCapacity;
        if (s.CarrierFuel > 0)
        {
            var range = s.CarrierJumpRange > 0 ? $" · {s.CarrierJumpRange:N0} ly range" : "";
            CarrierFuelText = $"Carrier tritium: {s.CarrierFuel:N0} t{range}";
        }
        else
        {
            CarrierFuelText = "Carrier tritium: unknown (no CarrierStats seen yet).";
        }

        if (s.CarrierPendingSystem is { Length: > 0 } pending)
        {
            var when = s.CarrierPendingDeparture is { } dep ? $" (departs {dep.ToLocalTime():HH:mm})" : "";
            CarrierPendingText = $"Jump scheduled → {pending}{when} — not arrived yet.";
            ShowCarrierPending = true;
        }
        else
        {
            ShowCarrierPending = false;
        }

        if (ModeNoBoost) RefreshMode();   // keep the hint's ship-range figure current
    }

    [RelayCommand]
    private async Task PlotRoute()
    {
        var from = RouteFrom.Trim();
        var to = RouteTo.Trim();
        if (from.Length == 0 || to.Length == 0)
        {
            RouteStatus = "Enter a start and destination system.";
            return;
        }

        if (!TryBuildRequest(from, to, out var request, out var error))
        {
            RouteStatus = error;
            return;
        }

        RouteBusy = true;
        RouteStatus = $"Plotting {from} → {to} …";
        RouteHasPlan = false;
        RouteHops.Clear();
        try
        {
            var plan = await Plotter.PlotAsync(request, CancellationToken.None);
            if (plan is null || plan.Hops.Count == 0)
            {
                RouteStatus = "No route found. Check the system names and that the ship's range can bridge the gap.";
                return;
            }

            ShowPlan(plan);
            RouteStatus = $"via {Plotter.SourceName}";
            SetRouteStep(Math.Min(1, RouteHops.Count - 1));   // first target after the origin

            // Straight-line context is a nice-to-have; a failed EDSM lookup must not spoil the plot.
            AnnotateDirectDistanceAsync(from, to).Forget("Route: direct distance");
        }
        catch (Exception ex)
        {
            RouteStatus = "Route plot failed: " + ex.Message;
        }
        finally
        {
            RouteBusy = false;
        }
    }

    /// <summary>Assemble the mode-specific request, or explain why it can't be built yet.</summary>
    private bool TryBuildRequest(string from, string to, out RoutePlotRequest request, out string error)
    {
        request = null!;
        error = "";
        var mode = CurrentMode;
        var hasRange = TryParseRange(RouteJumpRange, out var range);

        switch (mode)
        {
            case RouteMode.NeutronHighway:
                if (!hasRange)
                {
                    error = "Enter the ship's jump range in light years (e.g. 48.5).";
                    return false;
                }
                request = new RoutePlotRequest(from, to, range, Mode: mode);
                return true;

            case RouteMode.NoBoost:
                if (_fsd is null && !Context.DevEnabled)
                {
                    error = "A no-boost route needs your ship's FSD. Jump once in-game so EDNexus can read the Loadout.";
                    return false;
                }
                // Range is a fallback the dev sample plotter uses; live plots read the FSD. Current cargo
                // keeps the jumps achievable for a laden ship rather than optimistically unladen.
                request = new RoutePlotRequest(from, to, hasRange ? range : 0, Mode: mode, Ship: _fsd, CargoTons: _cargoTons);
                return true;

            default: // FleetCarrier — no range or ship needed, but used capacity shapes the fuel model.
                request = new RoutePlotRequest(from, to, 0, Mode: mode, CarrierCargoUsed: _carrierUsedCapacity);
                return true;
        }
    }

    /// <summary>Populate the hop list and totals from a plotted route, live or restored.</summary>
    private void ShowPlan(RoutePlan plan)
    {
        _lastPlan = plan;
        RouteHops.Clear();
        for (var i = 0; i < plan.Hops.Count; i++)
            RouteHops.Add(ToLine(i, plan.Hops[i], plan.Mode));

        RouteHasPlan = true;
        RouteSummary = Summarise(plan);
    }

    /// <summary>
    /// Bring back the route the commander had plotted last session, so restarting EDNexus mid-trip
    /// doesn't lose their place. A missing or empty saved route (the common case — nothing plotted
    /// yet, or it was cleared) leaves the card blank, same as a fresh install.
    /// </summary>
    private void RestoreSavedRoute()
    {
        var saved = Context.GetSavedRoute();
        if (string.IsNullOrWhiteSpace(saved.From) || saved.Hops.Count == 0) return;

        _restoring = true;
        try
        {
            var mode = Enum.TryParse<RouteMode>(saved.Mode, out var m) ? m : RouteMode.NeutronHighway;
            ModeNeutron = mode == RouteMode.NeutronHighway;
            ModeNoBoost = mode == RouteMode.NoBoost;
            ModeCarrier = mode == RouteMode.FleetCarrier;

            RouteFrom = saved.From ?? "";
            RouteTo = saved.To ?? "";
            RouteJumpRange = saved.JumpRangeText;

            var hops = saved.Hops.Select(h => new RouteHop(
                h.System, h.Jumps, h.IsNeutron, h.DistanceJumpedLy, h.DistanceRemainingLy,
                h.FuelUsed, h.FuelInTank, h.IsScoopable, h.MustRestock, h.RestockAmount, h.HasIcyRing)).ToList();
            ShowPlan(new RoutePlan(RouteFrom, RouteTo, hops, mode));
            RouteStatus = "Restored from last session.";
            SetRouteStep(Math.Clamp(saved.StepIndex, 0, RouteHops.Count - 1));
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>
    /// Save the currently displayed route (inputs, mode and every hop) so it survives a restart. A
    /// no-op while restoring (nothing changed) or in developer mode (fabricated data has no business
    /// coming back as a real plan next launch).
    /// </summary>
    private void PersistRoute()
    {
        if (_restoring || _lastPlan is null || Context.DevEnabled) return;

        Context.SaveRoute(new RouteSettings
        {
            From = RouteFrom,
            To = RouteTo,
            Mode = CurrentMode.ToString(),
            JumpRangeText = RouteJumpRange,
            StepIndex = RouteStepIndex,
            Hops = _lastPlan.Hops.Select(h => new SavedRouteHop
            {
                System = h.System,
                Jumps = h.Jumps,
                IsNeutron = h.IsNeutron,
                DistanceJumpedLy = h.DistanceJumpedLy,
                DistanceRemainingLy = h.DistanceRemainingLy,
                FuelUsed = h.FuelUsed,
                FuelInTank = h.FuelInTank,
                IsScoopable = h.IsScoopable,
                MustRestock = h.MustRestock,
                RestockAmount = h.RestockAmount,
                HasIcyRing = h.HasIcyRing,
            }).ToList(),
        });
    }

    /// <summary>Drop the plotted route (from the card and from disk) so the commander can start clean.</summary>
    [RelayCommand]
    private void ClearRoute()
    {
        _lastPlan = null;
        RouteHops.Clear();
        RouteHasPlan = false;
        RouteSummary = "";
        RouteFrom = "";
        RouteTo = "";
        RouteStepIndex = 0;
        RouteNextSystem = "—";
        RouteStatus = "Route cleared.";
        Context.SaveRoute(new RouteSettings());
    }

    /// <summary>Fold in the EDSM straight-line distance once it comes back, without blocking the plot.</summary>
    private async Task AnnotateDirectDistanceAsync(string from, string to)
    {
        if (Context.DevEnabled) return;   // offline mode has no EDSM catalogue to consult
        try
        {
            var direct = await Context.Host.Navigation.DistanceBetweenAsync(from, to, CancellationToken.None);
            if (direct is { } ly && RouteHasPlan)
                RouteSummary += $" · {ly:N0} ly direct";
        }
        catch { /* best effort — the route itself is already shown */ }
    }

    private static RouteHopLine ToLine(int index, RouteHop h, RouteMode mode)
    {
        var fuel = FuelText(h, mode);
        return new RouteHopLine(index, h.System, JumpText(index, h, mode), h.IsNeutron, $"{h.DistanceRemainingLy:N0} ly left", fuel, h.MustRestock);
    }

    /// <summary>
    /// The hop's sub-label. A neutron waypoint can span several jumps, so its count is what matters; a
    /// carrier hop or a no-boost jump is always exactly one jump, so its length in light years is the
    /// useful figure instead.
    /// </summary>
    private static string JumpText(int index, RouteHop h, RouteMode mode)
    {
        if (index == 0) return "start";
        if (mode == RouteMode.NeutronHighway) return h.Jumps == 1 ? "1 jump" : $"{h.Jumps} jumps";
        return $"{h.DistanceJumpedLy:N0} ly";
    }

    /// <summary>Per-hop fuel line: tritium for a carrier, ship fuel for a no-boost run, nothing for neutron.</summary>
    private static string FuelText(RouteHop h, RouteMode mode)
    {
        if (h.FuelUsed is not { } used || used <= 0) return "";
        var tank = h.FuelInTank is { } t ? $" · {t:N0} t in tank" : "";
        var unit = mode == RouteMode.FleetCarrier ? "t tritium" : "t fuel";
        var restock = h.MustRestock ? (h.HasIcyRing ? " · restock (icy ring)" : " · restock needed") : "";
        return $"−{used:N0} {unit}{tank}{restock}";
    }

    private static string Summarise(RoutePlan plan) => plan.Mode switch
    {
        RouteMode.FleetCarrier =>
            $"{plan.WaypointCount} hops"
            + (plan.TotalFuelUsed is { } tri ? $" · {tri:N0} t tritium" : "")
            + $" · {RestockCount(plan)} restocks",
        RouteMode.NoBoost =>
            $"{plan.WaypointCount} jumps"
            + (plan.TotalFuelUsed is { } fuel ? $" · {fuel:N0} t fuel" : ""),
        _ => $"{plan.WaypointCount} waypoints · {plan.TotalJumps} jumps · {plan.NeutronCount} neutron boosts",
    };

    private static int RestockCount(RoutePlan plan)
    {
        // Skip the origin: a "restock" flag there just means "leave with a full tank", not a mid-route stop.
        var count = 0;
        for (var i = 1; i < plan.Hops.Count; i++) if (plan.Hops[i].MustRestock) count++;
        return count;
    }

    /// <summary>Rough unladen jump range for the mode hint (full tank, no cargo, incl. Guardian bonus).</summary>
    private static double ShipRange(ShipFsdProfile f) => f.JumpRangeAt(f.BaseMass + Math.Min(f.MaxFuelPerJump, f.TankSize)) + f.RangeBoost;

    [RelayCommand]
    private async Task CopyNextSystem()
    {
        if (RouteHops.Count == 0) return;
        var system = RouteHops[Math.Clamp(RouteStepIndex, 0, RouteHops.Count - 1)].System;
        await CopyToClipboardAsync(system);
        RouteStatus = $"Copied “{system}” — paste into the galaxy map.";
    }

    [RelayCommand]
    private void NextHop()
    {
        if (RouteStepIndex < RouteHops.Count - 1) SetRouteStep(RouteStepIndex + 1);
    }

    [RelayCommand]
    private void PrevHop()
    {
        if (RouteStepIndex > 0) SetRouteStep(RouteStepIndex - 1);
    }

    /// <summary>Move the "next system" pointer, flipping the highlighted row so the list tracks progress.</summary>
    private void SetRouteStep(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, RouteHops.Count - 1));
        for (var i = 0; i < RouteHops.Count; i++)
            RouteHops[i].IsCurrent = i == index;
        RouteStepIndex = index;
        RouteNextSystem = RouteHops.Count > 0 ? RouteHops[index].System : "—";
        PersistRoute();   // keeps the saved step position current as the commander works through the route
    }

    private static bool TryParseRange(string text, out double range) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out range) && range > 0;
}

/// <summary>
/// One row on the route card: a waypoint with its jump cost, whether it is a neutron boost star, how far
/// along the route it leaves the commander, and (for no-boost / carrier plots) the fuel it burns.
/// <see cref="IsCurrent"/> marks the "next" hop the stepper is pointing at so the list can highlight
/// travel progress.
/// </summary>
public sealed partial class RouteHopLine : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public int Number { get; }
    public string System { get; }
    public string Jumps { get; }
    public bool IsNeutron { get; }
    public string Remaining { get; }
    public string Fuel { get; }
    public bool ShowFuel => Fuel.Length > 0;
    public bool MustRestock { get; }

    [ObservableProperty] private bool _isCurrent;

    public RouteHopLine(int number, string system, string jumps, bool isNeutron, string remaining, string fuel = "", bool mustRestock = false)
    {
        Number = number;
        System = system;
        Jumps = jumps;
        IsNeutron = isNeutron;
        Remaining = remaining;
        Fuel = fuel;
        MustRestock = mustRestock;
    }
}
