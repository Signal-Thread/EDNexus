using EDNexus.Core.Ship;

namespace EDNexus.Core.Routes;

/// <summary>How a route should be plotted — which Spansh planner and travel model to use.</summary>
public enum RouteMode
{
    /// <summary>Neutron highway: boost off neutron stars for the fewest jumps. Default for exploration ships.</summary>
    NeutronHighway,

    /// <summary>Plain jump range with neutron boosting off, using the ship's real FSD. Needs a <see cref="RoutePlotRequest.Ship"/>.</summary>
    NoBoost,

    /// <summary>Fleet-carrier hops: fixed 500 ly jumps, tritium-fuelled, never boosting.</summary>
    FleetCarrier,
}

/// <summary>
/// A request to plot a route: from where, to where, and how. <see cref="JumpRangeLy"/> drives the
/// neutron plot; <see cref="Ship"/> drives a no-boost plot (its FSD physics are what let Spansh model
/// fuel burn) with <see cref="CargoTons"/> aboard, so laden ships get jumps they can actually make;
/// <see cref="CarrierCargoUsed"/> is the carrier's total used capacity (crew, services and cargo,
/// tonnes — matching the figure shown on the carrier management screen) for a fleet-carrier plot.
/// <see cref="Efficiency"/>
/// is Spansh's 0–100 neutron detour tolerance (higher = more boosts, fewer jumps, longer path); 60
/// matches Spansh's own default. <see cref="FirstJumpMultiplier"/> is a one-shot range boost (an FSD
/// Injection) that applies to the first jump only, so the plotter plots that jump at the boosted range
/// and the rest at normal range.
/// </summary>
public sealed record RoutePlotRequest(
    string From,
    string To,
    double JumpRangeLy,
    int Efficiency = 60,
    RouteMode Mode = RouteMode.NeutronHighway,
    ShipFsdProfile? Ship = null,
    double CarrierCargoUsed = 0,
    double CargoTons = 0,
    double FirstJumpMultiplier = 1);

/// <summary>
/// One waypoint on a plotted route: the system to jump to, how many jumps it takes from the previous
/// waypoint (a neutron boost condenses several plain jumps into one), whether it is a neutron star to
/// boost off, and how far along the route this leaves the commander. The fuel fields are set for
/// no-boost and fleet-carrier plots (ship fuel / tritium in tonnes) and null for neutron plots.
/// </summary>
public sealed record RouteHop(
    string System,
    int Jumps,
    bool IsNeutron,
    double DistanceJumpedLy,
    double DistanceRemainingLy,
    double? FuelUsed = null,
    double? FuelInTank = null,
    bool IsScoopable = false,
    bool MustRestock = false,
    double? RestockAmount = null,
    bool HasIcyRing = false);

/// <summary>
/// A plotted route: its ordered <see cref="Hops"/> (source first, destination last) plus the totals
/// derived from them, so the UI can step through the hops and show progress.
/// </summary>
public sealed record RoutePlan(string From, string To, IReadOnlyList<RouteHop> Hops, RouteMode Mode = RouteMode.NeutronHighway)
{
    /// <summary>Total in-game jumps across the whole route (neutron boosts counted as they occur).</summary>
    public int TotalJumps => Hops.Sum(h => h.Jumps);

    /// <summary>Number of waypoints excluding the origin — i.e. how many systems there are to travel to.</summary>
    public int WaypointCount => Math.Max(0, Hops.Count - 1);

    /// <summary>Neutron scoop-boost stars along the way.</summary>
    public int NeutronCount => Hops.Count(h => h.IsNeutron);

    /// <summary>Total fuel burned across the route (ship fuel or tritium, in tonnes), when the plot reports it.</summary>
    public double? TotalFuelUsed => Hops.Any(h => h.FuelUsed.HasValue) ? Hops.Sum(h => h.FuelUsed ?? 0) : null;
}

/// <summary>
/// Plots long-distance routes for the app. Backed by an external plotter (Spansh); implementations
/// are network-bound and cancellable, and return null when no route could be plotted (unknown system,
/// out-of-range ship, or a transient failure) rather than throwing.
/// </summary>
public interface IRoutePlotter
{
    /// <summary>Human-readable name of the backing data source, e.g. "Spansh".</summary>
    string SourceName { get; }

    /// <summary>
    /// Plot the route for <paramref name="request"/>, or null when none could be produced. The backing
    /// client never throws for network/HTTP problems, matching the reporting clients' convention.
    /// </summary>
    Task<RoutePlan?> PlotAsync(RoutePlotRequest request, CancellationToken ct = default);
}
