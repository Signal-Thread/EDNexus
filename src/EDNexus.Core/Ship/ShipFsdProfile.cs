using System.Text.Json;
using EDNexus.Core.Journal;

namespace EDNexus.Core.Ship;

/// <summary>
/// The Frame Shift Drive parameters a no-boost ("galaxy") route plot needs, derived from the journal
/// <c>Loadout</c> event. These feed Spansh's galaxy plotter, which models fuel burn per jump — so the
/// plotter needs the drive's real physics, not just a headline jump range. Masses are tonnes, distances
/// light years.
/// </summary>
public sealed record ShipFsdProfile(
    double OptimalMass,
    double BaseMass,
    double TankSize,
    double ReserveSize,
    double FuelMultiplier,
    double FuelPower,
    double MaxFuelPerJump,
    double RangeBoost,
    double CargoCapacity,
    double MaxJumpRange = 0)
{
    /// <summary>
    /// Build a profile from a <c>Loadout</c> event, or null if the drive can't be identified (no FSD
    /// module, or an unknown symbol). Reads the drive's base physics from <see cref="FsdCatalog"/>, then
    /// applies the engineered optimal-mass / max-fuel overrides the journal reports and any Guardian FSD
    /// booster bonus.
    /// </summary>
    public static ShipFsdProfile? FromLoadout(JournalEntry loadout)
    {
        if (!loadout.Raw.TryGetProperty("Modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
            return null;

        FsdCatalog.Fsd? drive = null;
        double? engineeredOptimalMass = null;
        double? engineeredMaxFuel = null;
        double rangeBoost = 0;

        foreach (var module in modules.EnumerateArray())
        {
            var item = ReadString(module, "Item");
            if (item is null) continue;

            if (FsdCatalog.Lookup(item) is { } found)
            {
                drive = found;
                (engineeredOptimalMass, engineeredMaxFuel) = ReadFsdEngineering(module);
            }
            else if (FsdCatalog.GuardianBoosterBoost(item) is { } boost)
            {
                rangeBoost = boost;
            }
        }

        if (drive is not { } d) return null;

        var (main, reserve) = ReadFuelCapacity(loadout);
        return new ShipFsdProfile(
            OptimalMass: engineeredOptimalMass ?? d.OptimalMass,
            BaseMass: loadout.GetDouble("UnladenMass") ?? 0,
            TankSize: main,
            ReserveSize: reserve,
            FuelMultiplier: d.FuelMultiplier,
            FuelPower: d.FuelPower,
            MaxFuelPerJump: engineeredMaxFuel ?? d.MaxFuelPerJump,
            RangeBoost: rangeBoost,
            CargoCapacity: loadout.GetDouble("CargoCapacity") ?? 0,
            // The game's own headline figure for this build. Taken verbatim rather than derived:
            // JumpRangeAt() below models the drive without the Guardian booster, so for a boosted build
            // it is not the number to show a commander.
            MaxJumpRange: loadout.GetDouble("MaxJumpRange") ?? 0);
    }

    /// <summary>
    /// The jump range this drive gives at the given mass — the standard FSD formula,
    /// <c>optimalMass / mass × (fuel / fuelMultiplier)^(1 / fuelPower)</c>, with <c>fuel</c> the most one
    /// jump may burn. At <c>BaseMass + MaxFuelPerJump</c> it reproduces the journal's own
    /// <c>MaxJumpRange</c> exactly for any build without a Guardian booster. Excludes that booster,
    /// whose in-game bonus is non-linear and modelled server-side by Spansh.
    /// </summary>
    public double JumpRangeAt(double mass)
    {
        if (mass <= 0 || FuelMultiplier <= 0 || FuelPower <= 0) return 0;
        var fuel = Math.Min(MaxFuelPerJump, TankSize);
        return OptimalMass / mass * Math.Pow(fuel / FuelMultiplier, 1 / FuelPower);
    }

    /// <summary>Ship mass (t) with the given fuel and cargo aboard: the unladen hull plus both.</summary>
    public double MassWith(double fuelOnBoard, double cargo) => BaseMass + fuelOnBoard + cargo;

    /// <summary>
    /// The jump range with <paramref name="fuelOnBoard"/> tonnes of fuel and <paramref name="cargo"/>
    /// tonnes of cargo — what the drive can do right now. Fuel counts twice: it adds mass, and a jump
    /// can't burn more than is in the tank. Zero without a known hull mass.
    /// </summary>
    public double JumpRangeWith(double fuelOnBoard, double cargo)
    {
        if (BaseMass <= 0 || FuelMultiplier <= 0 || FuelPower <= 0) return 0;
        var fuel = Math.Min(MaxFuelPerJump, Math.Max(fuelOnBoard, 0));
        return OptimalMass / MassWith(fuelOnBoard, cargo) * Math.Pow(fuel / FuelMultiplier, 1 / FuelPower);
    }

    /// <summary>How many full-range jumps the given fuel covers (each burns <see cref="MaxFuelPerJump"/>).</summary>
    public double FullRangeJumps(double fuelOnBoard)
        => MaxFuelPerJump > 0 ? Math.Max(fuelOnBoard, 0) / MaxFuelPerJump : 0;

    /// <summary>Read the engineered FSDOptimalMass / MaxFuelPerJump overrides the journal records on the module.</summary>
    private static (double? OptimalMass, double? MaxFuel) ReadFsdEngineering(JsonElement module)
    {
        if (!module.TryGetProperty("Engineering", out var eng) ||
            !eng.TryGetProperty("Modifiers", out var mods) || mods.ValueKind != JsonValueKind.Array)
            return (null, null);

        double? optimalMass = null, maxFuel = null;
        foreach (var mod in mods.EnumerateArray())
        {
            var label = ReadString(mod, "Label");
            if (!mod.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var value))
                continue;
            if (string.Equals(label, "FSDOptimalMass", StringComparison.OrdinalIgnoreCase)) optimalMass = value;
            else if (string.Equals(label, "MaxFuelPerJump", StringComparison.OrdinalIgnoreCase)) maxFuel = value;
        }
        return (optimalMass, maxFuel);
    }

    private static (double Main, double Reserve) ReadFuelCapacity(JournalEntry loadout)
    {
        if (!loadout.Raw.TryGetProperty("FuelCapacity", out var fc) || fc.ValueKind != JsonValueKind.Object)
            return (0, 0);
        var main = fc.TryGetProperty("Main", out var m) && m.TryGetDouble(out var mv) ? mv : 0;
        var reserve = fc.TryGetProperty("Reserve", out var r) && r.TryGetDouble(out var rv) ? rv : 0;
        return (main, reserve);
    }

    private static string? ReadString(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
