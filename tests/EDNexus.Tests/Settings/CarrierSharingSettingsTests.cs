using EDNexus.Core.Settings;
using Xunit;

namespace EDNexus.Tests.Settings;

public class CarrierSharingSettingsTests
{
    private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ednexus-carrier-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Sharing_carrier_cargo_is_off_by_default_with_no_key()
    {
        var settings = new AppSettings().Colonisation;

        Assert.False(settings.ShareCarrierCargo);
        Assert.Equal("", settings.RavenApiKey);
        Assert.False(settings.CarrierSharingActive);
    }

    [Theory]
    [InlineData(true, "key-123", true)]
    [InlineData(true, "   ", false)]     // an opt-in is never live without a key to send it with
    [InlineData(true, "", false)]
    [InlineData(false, "key-123", false)]
    public void Sharing_is_only_active_when_switched_on_and_a_key_is_set(bool on, string key, bool active)
    {
        var settings = new ColonisationSettings { ShareCarrierCargo = on, RavenApiKey = key };

        Assert.Equal(active, settings.CarrierSharingActive);
    }

    [Theory]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", true)]
    [InlineData("  abc123  ", true)]            // pasted with stray whitespace: trimmed, so fine
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("two words", false)]
    [InlineData("line\r\nbreak", false)]
    [InlineData("kéy", false)]
    public void A_plausible_key_is_printable_ascii_without_spaces(string? key, bool plausible)
        => Assert.Equal(plausible, ColonisationSettings.IsPlausibleRavenApiKey(key));

    [Fact]
    public void The_choice_and_key_round_trip_through_disk()
    {
        try
        {
            var store = new SettingsStore(_path);
            var settings = store.Load();
            settings.Colonisation.ShareCarrierCargo = true;
            settings.Colonisation.RavenApiKey = "key-123";
            store.Save(settings);

            var reloaded = new SettingsStore(_path).Load();

            Assert.True(reloaded.Colonisation.ShareCarrierCargo);
            Assert.Equal("key-123", reloaded.Colonisation.RavenApiKey);
        }
        finally
        {
            if (System.IO.File.Exists(_path)) System.IO.File.Delete(_path);
        }
    }
}
