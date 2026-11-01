using System.Text.Json;
using EDNexus.Core.Settings;
using EDNexus.Core.ShoppingList;
using Xunit;

namespace EDNexus.Tests.Settings;

public class ShoppingListSettingsTests : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _root = Directory.CreateTempSubdirectory("ednexus-shopping-settings-").FullName;
    private string Path => System.IO.Path.Combine(_root, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static AppSettings Sample() => new()
    {
        Shopping = new ShoppingListSettings
        {
            Manual =
            {
                new ShoppingEntry(ShoppingItemKind.Commodity, "steel", "Steel", 120),
                new ShoppingEntry(ShoppingItemKind.Raw, "iron", "Iron", 30, ShoppingItemSource.Manual),
            },
            LinkColonisation = true,
            LinkEngineering = true,
            HideCompleted = true,
        },
    };

    [Fact]
    public void Defaults_are_empty()
    {
        var s = new AppSettings().Shopping;

        Assert.Empty(s.Manual);
        Assert.False(s.LinkColonisation);
        Assert.False(s.LinkEngineering);
        Assert.False(s.HideCompleted);
    }

    [Fact]
    public void Json_round_trip_preserves_entries_and_links_with_enums_as_strings()
    {
        var json = JsonSerializer.Serialize(Sample(), Options);
        Assert.Contains("\"Commodity\"", json);
        Assert.Contains("\"Raw\"", json);

        var back = JsonSerializer.Deserialize<AppSettings>(json)!.Shopping;

        Assert.Equal(2, back.Manual.Count);
        Assert.Equal(new ShoppingEntry(ShoppingItemKind.Commodity, "steel", "Steel", 120), back.Manual[0]);
        Assert.Equal(ShoppingItemKind.Raw, back.Manual[1].Kind);
        Assert.Equal(30, back.Manual[1].Needed);
        Assert.True(back.LinkColonisation);
        Assert.True(back.LinkEngineering);
        Assert.True(back.HideCompleted);
    }

    [Fact]
    public void Json_without_a_shopping_key_yields_empty_defaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"InstallId\":\"abc\"}")!;

        Assert.NotNull(settings.Shopping);
        Assert.Empty(settings.Shopping.Manual);
        Assert.False(settings.Shopping.LinkColonisation);
        Assert.False(settings.Shopping.HideCompleted);
    }

    [Fact]
    public void Shopping_settings_round_trip_through_the_store()
    {
        var store = new SettingsStore(Path);
        var settings = store.Load();
        settings.Shopping = Sample().Shopping;
        store.Save(settings);

        var back = new SettingsStore(Path).Load().Shopping;

        Assert.Equal(2, back.Manual.Count);
        Assert.Equal("Steel", back.Manual[0].Name);
        Assert.True(back.LinkColonisation);
        Assert.True(back.LinkEngineering);
        Assert.True(back.HideCompleted);
    }
}
