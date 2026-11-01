using System.Text.Json;
using EDNexus.Core.Settings;
using Xunit;

namespace EDNexus.Tests;

public class ShoppingListSettingsNullTests
{
    [Fact]
    public void Null_manual_in_json_becomes_an_empty_list()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Shopping":{"Manual":null,"HideCompleted":true}}""")!;

        Assert.NotNull(settings.Shopping.Manual);
        Assert.Empty(settings.Shopping.Manual);
        Assert.True(settings.Shopping.HideCompleted);
    }

    [Fact]
    public void Assigning_null_manual_keeps_the_list_non_null()
    {
        var s = new ShoppingListSettings { Manual = null! };

        Assert.NotNull(s.Manual);
    }
}
