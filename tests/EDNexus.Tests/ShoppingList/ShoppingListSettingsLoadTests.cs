using System.IO;
using System.Linq;
using EDNexus.Core.Settings;
using EDNexus.Core.ShoppingList;
using Xunit;

namespace EDNexus.Tests.ShoppingList;

public class ShoppingListSettingsLoadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ednx-shop-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private (AppSettings Settings, string Path) LoadJson(string json)
    {
        var path = System.IO.Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        return (new SettingsStore(path).Load(), path);
    }

    private void AssertNotQuarantined() =>
        Assert.Empty(Directory.GetFiles(_dir, "*corrupt*"));

    [Fact]
    public void Unknown_kind_drops_only_that_entry()
    {
        var (s, _) = LoadJson("""
            {"Shopping":{"Manual":[
              {"Kind":"Bogus","Key":"x","Name":"x","Needed":1,"Source":"Manual"},
              {"Kind":"Raw","Key":"iron","Name":"Iron","Needed":2,"Source":"Manual"}],
              "HideCompleted":true},"AutoDownloadUpdates":true}
            """);

        Assert.True(s.AutoDownloadUpdates);
        Assert.True(s.Shopping.HideCompleted);
        var e = Assert.Single(s.Shopping.Manual);
        Assert.Equal("iron", e.Key);
        AssertNotQuarantined();
    }

    [Fact]
    public void Unknown_source_drops_only_that_entry()
    {
        var (s, _) = LoadJson("""
            {"Shopping":{"Manual":[
              {"Kind":"Raw","Key":"a","Name":"A","Needed":1,"Source":"Nope"},
              {"Kind":"Raw","Key":"iron","Name":"Iron","Needed":2,"Source":"Manual"}],
              "LinkEngineering":true},"AutoDownloadUpdates":true}
            """);

        Assert.True(s.AutoDownloadUpdates);
        Assert.True(s.Shopping.LinkEngineering);
        Assert.Equal("iron", Assert.Single(s.Shopping.Manual).Key);
        AssertNotQuarantined();
    }
}
