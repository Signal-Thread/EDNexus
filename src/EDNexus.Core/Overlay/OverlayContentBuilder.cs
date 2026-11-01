using EDNexus.Core.Colonisation;
using EDNexus.Core.Exobio;
using EDNexus.Core.Settings;
using EDNexus.Core.State;

namespace EDNexus.Core.Overlay;

/// <summary>
/// Pure function that turns live state into an <see cref="OverlayContent"/> snapshot. Kept free of
/// any UI or platform dependency so both the Windows overlay window and unit tests can drive it the
/// same way.
/// </summary>
public static class OverlayContentBuilder
{
    /// <summary>How many outstanding commodities the overlay's colonisation panel shows at once.</summary>
    public const int MaxShortfallLines = 5;

    /// <param name="state">The live commander state (never mutated).</param>
    /// <param name="currentBodySignals">The body the commander is currently at, from <see cref="ExobiologyTracker.CurrentBody"/>, or null.</param>
    /// <param name="activeSite">The most recently touched colonisation site, from <see cref="ColonisationTracker.ActiveSite"/>, or null.</param>
    /// <param name="route">The last saved/plotted route, or null when nothing has been plotted.</param>
    /// <param name="activeScan">The sample run in progress, from <see cref="ExobiologyTracker.ActiveScan"/>, or null.</param>
    /// <param name="carrierStock">Fleet-carrier stock from <see cref="CarrierCargoTracker.Stock"/>, so stored cargo isn't listed as still to buy.</param>
    public static OverlayContent Build(
        CommanderState state,
        BodyBioSignals? currentBodySignals,
        ColonisationSite? activeSite,
        RouteSettings? route,
        OrganicScan? activeScan = null,
        IReadOnlyDictionary<string, int>? carrierStock = null)
    {
        IReadOnlyList<OverlayShortfallLine> shortfalls = activeSite is null
            ? Array.Empty<OverlayShortfallLine>()
            : activeSite.BuildShoppingList(state.Cargo, carrierStock)
                .Where(i => i.StillNeeded > 0)
                .OrderByDescending(i => i.StillNeeded)
                .Take(MaxShortfallLines)
                .Select(i => new OverlayShortfallLine(i.Name, i.StillNeeded))
                .ToList();

        return new OverlayContent(
            StarSystem: state.StarSystem,
            NextJumpSystem: NextJumpSystem(route),
            FuelMain: state.FuelMain,
            FuelCapacity: state.FuelCapacity,
            BioSignalBody: currentBodySignals is { SignalCount: > 0 } ? currentBodySignals.BodyName : null,
            BioSignalCount: currentBodySignals?.SignalCount ?? 0,
            ColonisationShortfalls: shortfalls,
            BioSignalDetail: BioDetail(currentBodySignals, activeScan));
    }

    /// <summary>
    /// The one bio line worth a glance on foot. Mid-run, that is how far to walk before the next
    /// sample counts; otherwise the richest candidate here and its sample distance.
    /// </summary>
    private static string? BioDetail(BodyBioSignals? body, OrganicScan? scan)
    {
        if (scan is { Complete: false })
            return scan.SampleDistanceMeters > 0
                ? $"Sampling {scan.SpeciesName} {scan.Progress} · move {scan.SampleDistanceMeters} m"
                : $"Sampling {scan.SpeciesName} {scan.Progress}";

        if (body is not { SignalCount: > 0 }) return null;

        if (body.Predictions.Count > 0)
        {
            var top = body.Predictions[0];
            return $"Top candidate: {top.Species.Name} · {top.SampleDistanceMeters} m · ~{Money(top.EstimatedValue)}";
        }

        if (body.Genera.Count > 0)
        {
            var richest = body.Genera.OrderByDescending(g => g.MaxValue).First();
            return $"Richest genus: {richest.Name} · {richest.SampleDistanceMeters} m";
        }

        return null;
    }

    private static string Money(long credits) => credits >= 1_000_000
        ? $"{credits / 1_000_000.0:0.#}M cr"
        : $"{credits:N0} cr";

    /// <summary>
    /// Mirrors the route card's "next system" pointer: the saved route's hop at
    /// <see cref="RouteSettings.StepIndex"/>, clamped to the hop list. That index is left pointing at
    /// the first real waypoint after the origin once a route has been plotted, not the origin itself.
    /// </summary>
    private static string? NextJumpSystem(RouteSettings? route)
    {
        if (route?.Hops is not { Count: > 0 } hops) return null;
        var index = Math.Clamp(route.StepIndex, 0, hops.Count - 1);
        var system = hops[index].System;
        return string.IsNullOrWhiteSpace(system) ? null : system;
    }
}
