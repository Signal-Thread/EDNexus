using System.Globalization;
using System.Text.Json;
using EDNexus.Core.Trade;
using EliteDangerous.Spansh;

namespace EDNexus.Core.Routes;

/// <summary>
/// The engine-side <see cref="IRoutePlotter"/> adapter over the reusable <see cref="SpanshClient"/>.
/// The library is pure transport and returns Spansh-shaped waypoints; this adapter picks the right
/// planner for the requested <see cref="RouteMode"/>, maps the waypoints to the engine's
/// <see cref="RoutePlan"/>, and caches plotted routes on disk so repeating a plot skips the (slow,
/// job-based) Spansh round-trip — reusing the same <see cref="IResponseCache"/> the trade search uses.
/// </summary>
public sealed class SpanshRoutePlotter : IRoutePlotter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SpanshClient _client;
    private readonly IResponseCache? _cache;

    public string SourceName => "Spansh";

    public SpanshRoutePlotter(SpanshClient client, IResponseCache? cache = null)
    {
        _client = client;
        _cache = cache;
    }

    public async Task<RoutePlan?> PlotAsync(RoutePlotRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.From) || string.IsNullOrWhiteSpace(request.To))
            return null;
        // A no-boost plot needs the ship's FSD; a neutron plot needs a jump range. Fleet-carrier needs neither.
        if (request.Mode == RouteMode.NoBoost && request.Ship is null) return null;
        if (request.Mode == RouteMode.NeutronHighway && request.JumpRangeLy <= 0) return null;

        if (request.FirstJumpMultiplier > 1 && request.Mode != RouteMode.FleetCarrier)
            return await PlotWithInjectionAsync(request, ct).ConfigureAwait(false);

        var key = CacheKey(request);
        if (_cache.GetTyped<List<RouteHop>>(key, Json) is { Count: > 0 } cachedHops)
            return new RoutePlan(request.From, request.To, cachedHops, request.Mode);

        var result = await PlotWithSpanshAsync(request, ct).ConfigureAwait(false);

        // A failed or empty plot is transient/uninteresting — surface it as "no route" without caching,
        // so the next attempt retries rather than serving an empty answer for the whole TTL.
        if (!result.IsOk || result.Waypoints.Count == 0) return null;

        var hops = result.Waypoints.Select(ToHop).ToList();
        _cache?.Put(key, JsonSerializer.Serialize(hops, Json));
        return new RoutePlan(request.From, request.To, hops, request.Mode);
    }

    /// <summary>
    /// An FSD Injection boosts one jump only, and Spansh can't model that, so: plot the whole route at the
    /// boosted range and keep just its first jump, then plot the rest from there at normal range. Falls
    /// back to the plain route when the boosted plot fails or its first waypoint isn't a single jump.
    /// </summary>
    private async Task<RoutePlan?> PlotWithInjectionAsync(RoutePlotRequest request, CancellationToken ct)
    {
        var m = request.FirstJumpMultiplier;
        var plain = request with { FirstJumpMultiplier = 1 };
        var boosted = plain with
        {
            JumpRangeLy = request.JumpRangeLy * m,
            Ship = request.Ship is { } s ? s with { OptimalMass = s.OptimalMass * m } : null,
        };

        var first = await PlotAsync(boosted, ct).ConfigureAwait(false);
        if (first is null || first.Hops.Count < 2 || first.Hops[1].Jumps != 1)
            return await PlotAsync(plain, ct).ConfigureAwait(false);
        if (first.Hops.Count == 2) return first;   // the boosted jump reaches the destination

        var step = first.Hops[1];
        var rest = await PlotAsync(plain with { From = step.System }, ct).ConfigureAwait(false);
        if (rest is null || rest.Hops.Count < 2)
            return await PlotAsync(plain, ct).ConfigureAwait(false);

        var left = rest.Hops[0].DistanceRemainingLy;
        var hops = new List<RouteHop>(rest.Hops.Count + 1)
        {
            first.Hops[0] with { DistanceRemainingLy = left + step.DistanceJumpedLy },
            step with { DistanceRemainingLy = left },
        };
        hops.AddRange(rest.Hops.Skip(1));
        return new RoutePlan(request.From, request.To, hops, request.Mode);
    }

    private Task<SpanshRouteResult> PlotWithSpanshAsync(RoutePlotRequest r, CancellationToken ct) => r.Mode switch
    {
        RouteMode.FleetCarrier => _client.PlotFleetCarrierRouteAsync(new SpanshFleetCarrierRouteQuery
        {
            From = r.From,
            To = r.To,
            CapacityUsed = r.CarrierCargoUsed,
        }, ct),

        RouteMode.NoBoost => _client.PlotGalaxyRouteAsync(new SpanshGalaxyRouteQuery
        {
            From = r.From,
            To = r.To,
            OptimalMass = r.Ship!.OptimalMass,
            BaseMass = r.Ship.BaseMass,
            TankSize = r.Ship.TankSize,
            ReserveSize = r.Ship.ReserveSize,
            FuelMultiplier = r.Ship.FuelMultiplier,
            FuelPower = r.Ship.FuelPower,
            MaxFuelPerJump = r.Ship.MaxFuelPerJump,
            RangeBoost = r.Ship.RangeBoost,
            Cargo = r.CargoTons,
            UseSupercharge = false,
        }, ct),

        _ => _client.PlotRouteAsync(new SpanshRouteQuery
        {
            From = r.From,
            To = r.To,
            RangeLy = r.JumpRangeLy,
            Efficiency = r.Efficiency,
        }, ct),
    };

    private static RouteHop ToHop(SpanshRouteWaypoint w) => new(
        w.System, w.Jumps, w.IsNeutron, w.DistanceJumpedLy, w.DistanceRemainingLy,
        w.FuelUsed, w.FuelInTank, w.IsScoopable, w.MustRestock, w.RestockAmount, w.HasIcyRing);

    private static string CacheKey(RoutePlotRequest r) =>
        "spansh|route|" + string.Join("|", new[]
        {
            r.Mode.ToString().ToLowerInvariant(),
            r.From.Trim().ToLowerInvariant(),
            r.To.Trim().ToLowerInvariant(),
            ModeParams(r),
        });

    /// <summary>The mode-specific inputs that change the answer, so caches don't collide across ships/loads.</summary>
    private static string ModeParams(RoutePlotRequest r) => r.Mode switch
    {
        RouteMode.FleetCarrier => "cargo:" + r.CarrierCargoUsed.ToString("0.##", CultureInfo.InvariantCulture),
        RouteMode.NoBoost => r.Ship is { } s
            ? string.Join(",", new[] { s.OptimalMass, s.BaseMass, s.TankSize, s.ReserveSize, s.MaxFuelPerJump, s.RangeBoost, r.CargoTons }
                .Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)))
            : "noship",
        _ => "range:" + r.JumpRangeLy.ToString("0.##", CultureInfo.InvariantCulture)
             + "|eff:" + r.Efficiency.ToString(CultureInfo.InvariantCulture),
    };
}
