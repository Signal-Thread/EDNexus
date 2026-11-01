using System.Text.Json;
using EDNexus.Core.Journal;

namespace EDNexus.Core.Ship;

/// <summary>
/// How much of the power plant's output the current build uses, derived from the journal
/// <c>Loadout</c> event. Weapons and most utility mounts only draw while hardpoints are deployed, so the
/// build has two figures: <see cref="RetractedDraw"/> and <see cref="DeployedDraw"/>. All values MW.
/// </summary>
/// <param name="Capacity">The power plant's output.</param>
/// <param name="RetractedDraw">Draw with hardpoints retracted.</param>
/// <param name="DeployedDraw">Draw with hardpoints deployed (always at least the retracted figure).</param>
/// <param name="UnknownModules">Powered modules missing from <see cref="ModulePowerCatalog"/>; their draw is not counted.</param>
public sealed record ShipPowerProfile(double Capacity, double RetractedDraw, double DeployedDraw, int UnknownModules)
{
    /// <summary>The draw for the given hardpoint state.</summary>
    public double Draw(bool hardpointsDeployed) => hardpointsDeployed ? DeployedDraw : RetractedDraw;

    /// <summary>Output left over for the given hardpoint state; negative means the plant is overloaded.</summary>
    public double Headroom(bool hardpointsDeployed) => Capacity - Draw(hardpointsDeployed);

    /// <summary>
    /// Build a profile from a <c>Loadout</c> event, or null if there is no recognised power plant. Only
    /// modules switched on count. Hardpoint slots (weapons and the tiny utility mounts) draw only when
    /// deployed, except shield boosters, which are always powered.
    /// </summary>
    public static ShipPowerProfile? FromLoadout(JournalEntry loadout)
    {
        if (!loadout.Raw.TryGetProperty("Modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
            return null;

        double capacity = 0, always = 0, deployedOnly = 0;
        var unknown = 0;
        var foundPlant = false;

        foreach (var module in modules.EnumerateArray())
        {
            var item = ReadString(module, "Item");
            if (item is null) continue;
            if (module.TryGetProperty("On", out var on) && on.ValueKind == JsonValueKind.False) continue;

            var stock = ModulePowerCatalog.Lookup(item);
            var slot = ReadString(module, "Slot") ?? "";
            var (engDraw, engCapacity) = ReadEngineering(module);

            if (stock is not { } p)
            {
                // Hull parts and cosmetics aren't powered and aren't in the table.
                if (!IsCosmetic(item)) unknown++;
                continue;
            }

            if (p.Generation > 0)
            {
                capacity += engCapacity ?? p.Generation;
                foundPlant = true;
                continue;
            }

            var draw = engDraw ?? p.Draw;
            var isHardpoint = slot.Contains("Hardpoint", StringComparison.OrdinalIgnoreCase);
            var alwaysOn = item.StartsWith("hpt_shieldbooster", StringComparison.OrdinalIgnoreCase);
            if (isHardpoint && !alwaysOn) deployedOnly += draw;
            else always += draw;
        }

        return foundPlant ? new ShipPowerProfile(capacity, always, always + deployedOnly, unknown) : null;
    }

    // Powered modules are all "int_*" (internal) or "hpt_*" (hardpoint) symbols. Everything else —
    // paint, decals, nameplates, armour, ship kits, cargo hatches — draws nothing the table could miss.
    private static bool IsCosmetic(string item)
        => !item.StartsWith("int_", StringComparison.OrdinalIgnoreCase)
           && !item.StartsWith("hpt_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Engineered PowerDraw / PowerCapacity overrides the journal records on the module.</summary>
    private static (double? Draw, double? Capacity) ReadEngineering(JsonElement module)
    {
        if (!module.TryGetProperty("Engineering", out var eng) ||
            !eng.TryGetProperty("Modifiers", out var mods) || mods.ValueKind != JsonValueKind.Array)
            return (null, null);

        double? draw = null, capacity = null;
        foreach (var mod in mods.EnumerateArray())
        {
            var label = ReadString(mod, "Label");
            if (!mod.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var value))
                continue;
            if (string.Equals(label, "PowerDraw", StringComparison.OrdinalIgnoreCase)) draw = value;
            else if (string.Equals(label, "PowerCapacity", StringComparison.OrdinalIgnoreCase)) capacity = value;
        }
        return (draw, capacity);
    }

    private static string? ReadString(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
