using EDNexus.App;
using EDNexus.App.Telemetry;
using EDNexus.Core.Mining;
using EDNexus.Core.Settings;

namespace EDNexus.App.Tests;

/// <summary>
/// Developer mode fabricates journal events through the real pipeline and is documented as never
/// persisted: nothing the dashboard learns from them may reach the settings file.
/// </summary>
public sealed class DeveloperModePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ednexus-apptest-" + Guid.NewGuid().ToString("N"));
    private readonly Bootstrap _boot;

    public DeveloperModePersistenceTests()
    {
        Directory.CreateDirectory(_dir);
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _boot = new Bootstrap(store, new AppSettings(), new CrashReporting());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private bool SettingsFileExists => File.Exists(Path.Combine(_dir, "settings.json"));

    [Fact]
    public void LearnCommodityPrices_Live_IsPersisted()
    {
        _boot.Dev.Enabled = false;

        _boot.LearnCommodityPrices([("gold", 9400)]);

        Assert.Equal(9400, _boot.Settings.Mining.KnownPrices["gold"]);
        Assert.True(SettingsFileExists);
    }

    [Fact]
    public void LearnCommodityPrices_InDeveloperMode_IsNotLearnedOrSaved()
    {
        Assert.True(_boot.Dev.Available, "developer tools are compiled out; this test cannot exercise the guard");
        _boot.Dev.Enabled = true;

        _boot.LearnCommodityPrices([("gold", 123456)]);

        Assert.Empty(_boot.Settings.Mining.KnownPrices);
        Assert.False(SettingsFileExists);
    }

    [Fact]
    public void RecordMiningRefined_InDeveloperMode_DoesNotTouchTheDailyTotal()
    {
        Assert.True(_boot.Dev.Available);
        _boot.Dev.Enabled = true;

        _boot.RecordMiningRefined(DateTimeOffset.Now, 5000);

        Assert.Equal(0, _boot.Settings.Mining.SessionUnits);
        Assert.Equal(0, _boot.Settings.Mining.SessionValue);
        Assert.False(SettingsFileExists);
    }

    [Fact]
    public void RecordMiningRefined_Live_CountsTowardTheDailyTotal()
    {
        _boot.Dev.Enabled = false;

        _boot.RecordMiningRefined(DateTimeOffset.Now, 5000);

        Assert.Equal(1, _boot.Settings.Mining.SessionUnits);
        Assert.Equal(5000, _boot.Settings.Mining.SessionValue);
    }

    [Fact]
    public void RecordMiningSpot_InDeveloperMode_AddsNothingToTheSpotBook()
    {
        Assert.True(_boot.Dev.Available);
        _boot.Dev.Enabled = true;
        var unit = new RefinedUnit(
            DateTimeOffset.Now, "gold", "Gold",
            new SurfacePosition(1, "Sol", "Earth", 10.0, 20.0, 6371000));

        _boot.RecordMiningSpot(unit, 9000);

        Assert.Empty(_boot.Settings.Mining.KnownSpots);
        Assert.False(SettingsFileExists);
    }

    [Fact]
    public void ColonisationSharedLookup_DefaultsOn_SoExistingBehaviourIsKept()
        => Assert.True(new AppSettings().Colonisation.SharedProjectLookup);
}
