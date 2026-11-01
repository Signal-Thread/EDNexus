using EDNexus.Core.Journal;
using EDNexus.Core.Ship;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.Ship;

public class ShipPlanningTests
{
    private static JournalEntry Parse(string json)
    {
        Assert.True(JournalEntry.TryParse(json, historical: false, out var entry));
        return entry;
    }

    private const string Loadout = """
    { "event": "Loadout", "UnladenMass": 280.0, "CargoCapacity": 64, "FuelCapacity": { "Main": 32.0, "Reserve": 0.63 },
      "Modules": [
        { "Slot": "PowerPlant", "Item": "int_powerplant_size5_class5", "On": true },
        { "Slot": "FrameShiftDrive", "Item": "int_hyperdrive_size5_class5", "On": true },
        { "Slot": "MediumHardpoint1", "Item": "hpt_pulselaser_gimbal_medium", "On": true },
        { "Slot": "TinyHardpoint1", "Item": "hpt_shieldbooster_size0_class5", "On": true },
        { "Slot": "Decal1", "Item": "decal_explorer_elite", "On": true } ] }
    """;

    [Fact]
    public void Jump_range_falls_with_cargo_and_with_a_shallow_tank()
    {
        var fsd = ShipFsdProfile.FromLoadout(Parse(Loadout))!;

        var empty = fsd.JumpRangeWith(32, 0);
        var laden = fsd.JumpRangeWith(32, 64);
        var low = fsd.JumpRangeWith(1, 0);

        Assert.True(laden < empty);
        Assert.True(low < empty);                 // can only burn what's in the tank
        Assert.Equal(empty, fsd.JumpRangeAt(fsd.BaseMass + 32), 6); // agrees with the existing formula
        Assert.Equal(312 + 64, fsd.MassWith(32, 64));
    }

    [Fact]
    public void Hardpoint_weapons_draw_only_when_deployed_but_shield_boosters_always_do()
    {
        var power = ShipPowerProfile.FromLoadout(Parse(Loadout))!;
        var pulse = ModulePowerCatalog.Lookup("hpt_pulselaser_gimbal_medium")!.Value.Draw;
        var booster = ModulePowerCatalog.Lookup("hpt_shieldbooster_size0_class5")!.Value.Draw;
        var fsdDraw = ModulePowerCatalog.Lookup("int_hyperdrive_size5_class5")!.Value.Draw;

        Assert.Equal(ModulePowerCatalog.Lookup("int_powerplant_size5_class5")!.Value.Generation, power.Capacity);
        Assert.Equal(fsdDraw + booster, power.RetractedDraw, 6);
        Assert.Equal(fsdDraw + booster + pulse, power.DeployedDraw, 6);
        Assert.Equal(0, power.UnknownModules);   // the decal is cosmetic, not "unknown"
    }

    [Fact]
    public void Engineered_power_draw_overrides_the_stock_figure_and_switched_off_modules_dont_count()
    {
        var power = ShipPowerProfile.FromLoadout(Parse("""
        { "event": "Loadout", "Modules": [
          { "Slot": "PowerPlant", "Item": "int_powerplant_size5_class5", "On": true },
          { "Slot": "Slot01_Size5", "Item": "int_shieldgenerator_size5_class5", "On": true,
            "Engineering": { "Modifiers": [ { "Label": "PowerDraw", "Value": 3.0 } ] } },
          { "Slot": "Slot02_Size5", "Item": "int_sensors_size5_class5", "On": false } ] }
        """))!;

        Assert.Equal(3.0, power.RetractedDraw);
    }

    [Fact]
    public void State_tracks_the_loadout_power_and_the_hardpoints_flag()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        bus.Publish(Parse(Loadout));
        bus.Publish(Parse("""{ "event": "Status", "Flags": 64 }"""));

        Assert.NotNull(state.Power);
        Assert.True(state.HardpointsDeployed);

        bus.Publish(Parse("""{ "event": "Status", "Flags": 0 }"""));
        Assert.False(state.HardpointsDeployed);
    }
}
