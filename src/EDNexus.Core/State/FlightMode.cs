namespace EDNexus.Core.State;

/// <summary>
/// What the commander is physically doing right now, as far as the game says. Finer than
/// <see cref="CommanderState.Docked"/> (which is all the app tracked before): a viewer watching the
/// stream card wants to see the undock, the supercruise approach, the landing.
/// </summary>
public enum FlightMode
{
    /// <summary>Nothing reported yet (before the first <c>Status.json</c>, or from the menu).</summary>
    Unknown = 0,

    /// <summary>On a landing pad in a station or on a carrier.</summary>
    Docked,

    /// <summary>On a planet's surface in the ship.</summary>
    Landed,

    /// <summary>Flying the ship in normal space.</summary>
    InSpace,

    /// <summary>Flying the ship close to a planet's surface (the game is reporting a latitude and longitude).</summary>
    NearSurface,

    /// <summary>In supercruise.</summary>
    Supercruise,

    /// <summary>In the hyperspace tunnel between systems.</summary>
    Hyperspace,

    /// <summary>Driving the SRV.</summary>
    Srv,

    /// <summary>On foot (Odyssey), in a station or on a planet.</summary>
    OnFoot,
}

/// <summary>
/// Reads <see cref="FlightMode"/> out of the <c>Flags</c> and <c>Flags2</c> bit fields of the game's
/// <c>Status.json</c>. That file is the one source for this: unlike the individual journal events it
/// is a snapshot, so it cannot disagree with itself, and the journal watcher re-reads it every
/// quarter second.
/// </summary>
/// <remarks>
/// Bit positions are from Frontier's Player Journal documentation, "Status File". Only the bits this
/// needs are named; the rest (shields, lights, hardpoints, …) are ignored.
/// </remarks>
public static class ShipStatusFlags
{
    // Flags
    private const long Docked = 1L << 0;
    private const long Landed = 1L << 1;
    private const long HardpointsOut = 1L << 6;
    private const long Supercruise = 1L << 4;
    private const long HasLatLong = 1L << 21;
    private const long InMainShip = 1L << 24;
    private const long InFighter = 1L << 25;
    private const long InSrv = 1L << 26;
    private const long FsdJump = 1L << 30;

    // Flags2
    private const long OnFoot = 1L << 0;

    /// <summary>Whether the flags say hardpoints are deployed.</summary>
    public static bool HardpointsDeployed(long flags) => (flags & HardpointsOut) != 0;

    /// <summary>
    /// The flight mode the flags describe, or <c>null</c> when they describe none (no ship, fighter,
    /// SRV or suit: the main menu, a loading screen, a taxi). A caller keeps what it already knew
    /// rather than treating that as a change.
    /// </summary>
    /// <remarks>
    /// Where several bits are set, the more specific wins: on foot over the SRV over docked over
    /// landed over a jump over supercruise. A commander on foot in a station can still have the
    /// ship's <see cref="Docked"/> bit set, and is on foot.
    /// </remarks>
    public static FlightMode? Derive(long flags, long flags2)
    {
        if ((flags2 & OnFoot) != 0) return FlightMode.OnFoot;
        if ((flags & InSrv) != 0) return FlightMode.Srv;
        if ((flags & Docked) != 0) return FlightMode.Docked;
        if ((flags & Landed) != 0) return FlightMode.Landed;
        if ((flags & FsdJump) != 0) return FlightMode.Hyperspace;
        if ((flags & Supercruise) != 0) return FlightMode.Supercruise;
        if ((flags & (InMainShip | InFighter)) != 0)
            return (flags & HasLatLong) != 0 ? FlightMode.NearSurface : FlightMode.InSpace;

        return null;
    }
}
