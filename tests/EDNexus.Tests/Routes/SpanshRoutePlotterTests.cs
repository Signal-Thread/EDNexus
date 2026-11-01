using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using EDNexus.Core.Routes;
using EDNexus.Core.Trade;
using EDNexus.Tests.Reporting;   // reuse the shared RecordingHandler test double
using EliteDangerous.Spansh;
using Xunit;

namespace EDNexus.Tests.Routes;

public class SpanshRoutePlotterTests
{
    private static readonly SpanshClientOptions Options = new()
    {
        SoftwareName = "EDNexus.Tests",
        SoftwareVersion = "1.0.0",
        RoutePollInterval = TimeSpan.Zero,
    };

    private const string Submit = """{ "job": "j1", "status": "queued" }""";
    private const string Ready = """
    { "status": "ok", "result": { "system_jumps": [
        { "system": "Sol",     "jumps": 0, "distance_jumped": 0,   "distance_left": 400, "neutron_star": false },
        { "system": "Jackson", "jumps": 4, "distance_jumped": 250, "distance_left": 150, "neutron_star": true  },
        { "system": "Colonia", "jumps": 2, "distance_jumped": 150, "distance_left": 0,   "neutron_star": false } ] } }
    """;

    private static SpanshRoutePlotter NewPlotter(RecordingHandler handler, IResponseCache? cache = null)
        => new(new SpanshClient(Options, new HttpClient(handler)), cache);

    [Fact]
    public async Task Maps_waypoints_and_derives_totals()
    {
        var plotter = NewPlotter(new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : Ready)));

        var plan = await plotter.PlotAsync(new RoutePlotRequest("Sol", "Colonia", 48));

        Assert.NotNull(plan);
        Assert.Equal(3, plan!.Hops.Count);
        Assert.Equal(2, plan.WaypointCount);           // hops excluding origin
        Assert.Equal(6, plan.TotalJumps);              // 0 + 4 + 2
        Assert.Equal(1, plan.NeutronCount);
        Assert.True(plan.Hops[1].IsNeutron);
    }

    [Fact]
    public async Task Empty_or_bad_request_is_null_without_touching_the_network()
    {
        var handler = new RecordingHandler(body: Ready);
        var plotter = NewPlotter(handler);

        Assert.Null(await plotter.PlotAsync(new RoutePlotRequest("", "Colonia", 48)));
        Assert.Null(await plotter.PlotAsync(new RoutePlotRequest("Sol", "Colonia", 0)));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task A_failed_plot_is_null_and_not_cached_so_it_retries()
    {
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, "down");
        var cache = new InMemoryCache();
        var plotter = NewPlotter(handler, cache);

        Assert.Null(await plotter.PlotAsync(new RoutePlotRequest("Sol", "Colonia", 48)));
        Assert.Empty(cache.Keys);   // nothing cached — a later attempt will hit the network again
    }

    [Fact]
    public async Task Cached_route_is_reused_and_the_job_runs_only_once()
    {
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : Ready));
        var plotter = NewPlotter(handler, new InMemoryCache());
        var request = new RoutePlotRequest("Sol", "Colonia", 48);

        var first = await plotter.PlotAsync(request);
        var callsAfterFirst = handler.CallCount;
        var second = await plotter.PlotAsync(request);

        Assert.Equal(callsAfterFirst, handler.CallCount);   // second served entirely from cache
        Assert.Equal(first!.TotalJumps, second!.TotalJumps);
        Assert.Equal("Colonia", second.Hops[^1].System);
    }

    [Fact]
    public async Task No_boost_mode_uses_the_galaxy_plotter_and_maps_fuel()
    {
        const string ready = """
        { "status": "ok", "result": { "jumps": [
            { "name": "Sol",     "distance": 0,    "distance_to_destination": 300, "fuel_used": 0,   "fuel_in_tank": 32, "is_scoopable": false },
            { "name": "Wolf 359","distance": 45.2, "distance_to_destination": 0,   "fuel_used": 4.1, "fuel_in_tank": 28, "is_scoopable": true, "must_refuel": true } ] } }
        """;
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : ready));
        var plotter = NewPlotter(handler);
        var ship = new EDNexus.Core.Ship.ShipFsdProfile(1050, 280, 32, 0.63, 0.012, 2.45, 5, 0, 0);

        var plan = await plotter.PlotAsync(new RoutePlotRequest("Sol", "Wolf 359", 0, Mode: RouteMode.NoBoost, Ship: ship));

        Assert.Contains("/generic/route", handler.Uris[0]!.ToString());
        Assert.NotNull(plan);
        Assert.Equal(RouteMode.NoBoost, plan!.Mode);
        Assert.False(plan.Hops[1].IsNeutron);
        Assert.Equal(4.1, plan.Hops[1].FuelUsed);
        Assert.Equal(4.1, plan.TotalFuelUsed);
    }

    [Fact]
    public async Task Injector_boosts_only_the_first_jump_then_replots_at_normal_range()
    {
        const string boostedPlot = """
        { "status": "ok", "result": { "jumps": [
            { "name": "Sol",    "distance": 0,  "distance_to_destination": 300, "fuel_used": 0, "fuel_in_tank": 32 },
            { "name": "Far",    "distance": 70, "distance_to_destination": 230, "fuel_used": 5, "fuel_in_tank": 27 },
            { "name": "Colonia","distance": 70, "distance_to_destination": 0,   "fuel_used": 5, "fuel_in_tank": 22 } ] } }
        """;
        const string restPlot = """
        { "status": "ok", "result": { "jumps": [
            { "name": "Far",    "distance": 0,  "distance_to_destination": 230, "fuel_used": 0, "fuel_in_tank": 32 },
            { "name": "Mid",    "distance": 45, "distance_to_destination": 185, "fuel_used": 4, "fuel_in_tank": 28 },
            { "name": "Colonia","distance": 45, "distance_to_destination": 0,   "fuel_used": 4, "fuel_in_tank": 24 } ] } }
        """;
        // Calls: submit, boosted result, submit, rest result.
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n switch { 1 => Submit, 2 => boostedPlot, 3 => Submit, _ => restPlot }));
        var plotter = NewPlotter(handler);
        var ship = new EDNexus.Core.Ship.ShipFsdProfile(1050, 280, 32, 0.63, 0.012, 2.45, 5, 0, 0);

        var plan = await plotter.PlotAsync(new RoutePlotRequest("Sol", "Colonia", 0, Mode: RouteMode.NoBoost, Ship: ship, FirstJumpMultiplier: 1.5));

        Assert.NotNull(plan);
        Assert.Equal(new[] { "Sol", "Far", "Mid", "Colonia" }, plan!.Hops.Select(h => h.System));
        Assert.Equal(70, plan.Hops[1].DistanceJumpedLy);     // the boosted jump
        Assert.Equal(300, plan.Hops[0].DistanceRemainingLy);
        Assert.Equal(230, plan.Hops[1].DistanceRemainingLy);
        Assert.Contains("optimal_mass=1575", handler.Bodies[0]);   // 1050 × 1.5 for the boosted plot
        Assert.Contains("optimal_mass=1050", handler.Bodies[2]);   // the remainder at normal range
    }

    [Fact]
    public async Task No_boost_mode_without_a_ship_never_touches_the_network()
    {
        var handler = new RecordingHandler(body: Ready);
        var plotter = NewPlotter(handler);

        var plan = await plotter.PlotAsync(new RoutePlotRequest("Sol", "Colonia", 0, Mode: RouteMode.NoBoost, Ship: null));

        Assert.Null(plan);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Fleet_carrier_mode_uses_the_carrier_plotter_and_tracks_tritium()
    {
        const string ready = """
        { "status": "ok", "result": { "jumps": [
            { "name": "Sol",     "distance": 0,   "distance_to_destination": 500, "fuel_used": 0,  "fuel_in_tank": 1000, "must_restock": 0 },
            { "name": "HR 1183", "distance": 374, "distance_to_destination": 0,   "fuel_used": 90, "fuel_in_tank": 910,  "must_restock": 0, "has_icy_ring": true } ] } }
        """;
        var handler = new RecordingHandler(n => (HttpStatusCode.OK, n == 1 ? Submit : ready));
        var plotter = NewPlotter(handler);

        var plan = await plotter.PlotAsync(new RoutePlotRequest("Sol", "HR 1183", 0, Mode: RouteMode.FleetCarrier));

        Assert.Contains("/fleetcarrier/route", handler.Uris[0]!.ToString());
        // Fuel must be modelled as draining from a full tank, not topped up each stop, or the tank level
        // comes back pinned at the maximum and never appears to deduct.
        Assert.Contains("calculate_starting_fuel=0", handler.Bodies[0]);
        Assert.NotNull(plan);
        Assert.Equal(RouteMode.FleetCarrier, plan!.Mode);
        Assert.Equal(90, plan.Hops[1].FuelUsed);
        Assert.True(plan.Hops[1].HasIcyRing);
        Assert.Equal(90, plan.TotalFuelUsed);
    }

    private sealed class InMemoryCache : IResponseCache
    {
        private readonly Dictionary<string, string> _store = new();
        public IReadOnlyCollection<string> Keys => _store.Keys;
        public string? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;
        public void Put(string key, string body) => _store[key] = body;
    }
}
