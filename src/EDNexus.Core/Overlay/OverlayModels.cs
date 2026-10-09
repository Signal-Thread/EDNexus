namespace EDNexus.Core.Overlay;

/// <summary>
/// One line of the overlay's colonisation panel: an outstanding commodity and how much of it is
/// still needed after accounting for what is already in the hold.
/// </summary>
public sealed record OverlayShortfallLine(string Name, int Remaining);

/// <summary>
/// Everything the in-game overlay shows, computed fresh each tick from live state. Immutable so the
/// overlay window (and tests) can compare one snapshot to the next without racing the engine thread
/// that produces it.
/// </summary>
/// <param name="StarSystem">The system the commander is currently in, or null before the first Location/FSDJump.</param>
/// <param name="NextJumpSystem">The next waypoint on the plotted route, or null when no route is active.</param>
/// <param name="FuelMain">Main tank fuel, in tonnes.</param>
/// <param name="FuelCapacity">Main tank capacity, in tonnes. 0 means unknown (no Loadout seen yet).</param>
/// <param name="BioSignalBody">Name of the body the commander is currently at, if it carries biological signals.</param>
/// <param name="BioSignalCount">Biological signal count for <paramref name="BioSignalBody"/>. 0 when none/unknown.</param>
/// <param name="ColonisationShortfalls">Worst-shortfall-first outstanding commodities for the active construction site.</param>
/// <param name="BioSignalDetail">
/// One exobiology hint: the sample run in progress and how far to move before the next sample, or
/// the richest predicted species here with its sample distance. Null when there is nothing to add.
/// </param>
public sealed record OverlayContent(
    string? StarSystem,
    string? NextJumpSystem,
    double FuelMain,
    double FuelCapacity,
    string? BioSignalBody,
    int BioSignalCount,
    IReadOnlyList<OverlayShortfallLine> ColonisationShortfalls,
    string? BioSignalDetail = null)
{
    /// <summary>An empty snapshot — nothing known yet, shown before the engine has any state.</summary>
    public static OverlayContent Empty { get; } =
        new(null, null, 0, 0, null, 0, Array.Empty<OverlayShortfallLine>(), null);

    /// <summary>Fuel remaining as a 0..1 fraction of capacity. 0 when capacity is unknown.</summary>
    public double FuelPercent => FuelCapacity > 0 ? Math.Clamp(FuelMain / FuelCapacity, 0, 1) : 0;

    /// <summary>True once fuel has dropped to a quarter of the tank or below.</summary>
    public bool FuelLow => FuelCapacity > 0 && FuelPercent <= 0.25;

    /// <summary>True when the current body is known to carry at least one biological signal.</summary>
    public bool HasBioSignals => BioSignalCount > 0;

    /// <summary>True when there is an active construction site with at least one outstanding commodity.</summary>
    public bool HasColonisationShortfall => ColonisationShortfalls.Count > 0;

    /// <summary>
    /// Whether <paramref name="other"/> would draw exactly the same overlay: every field equal, with the
    /// shortfall lines compared by value. The built-in record equality compares that list by reference,
    /// and a fresh list is built every tick, so it cannot tell an unchanged snapshot from a changed one.
    /// The overlay uses this to skip redundant updates, so an idle overlay touches nothing.
    /// </summary>
    public bool SameAs(OverlayContent? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        return StarSystem == other.StarSystem
            && NextJumpSystem == other.NextJumpSystem
            && FuelMain.Equals(other.FuelMain)
            && FuelCapacity.Equals(other.FuelCapacity)
            && BioSignalBody == other.BioSignalBody
            && BioSignalCount == other.BioSignalCount
            && BioSignalDetail == other.BioSignalDetail
            && ColonisationShortfalls.SequenceEqual(other.ColonisationShortfalls);
    }
}
