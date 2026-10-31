using EDNexus.Core.Settings;
using Xunit;

namespace EDNexus.Tests.Settings;

public class SettingsStorePeekTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ednexus-peek-").FullName;
    private string SettingsPath => Path.Combine(_root, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Peek_leaves_a_file_without_install_id_untouched()
    {
        File.WriteAllText(SettingsPath, "{\"Shopping\":{\"LinkEngineering\":true}}");
        var before = File.ReadAllBytes(SettingsPath);

        var settings = new SettingsStore(SettingsPath).Peek();

        Assert.True(settings.Shopping.LinkEngineering);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.Equal([SettingsPath], Directory.GetFileSystemEntries(_root));   // no .bak, .tmp or new files
    }

    [Fact]
    public void Peek_returns_defaults_for_a_missing_file_and_creates_nothing()
    {
        var settings = new SettingsStore(SettingsPath).Peek();

        Assert.NotNull(settings);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Peek_returns_defaults_for_a_corrupt_file_without_renaming_it()
    {
        File.WriteAllText(SettingsPath, "{ not json");

        var settings = new SettingsStore(SettingsPath).Peek();

        Assert.NotNull(settings);
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath));
        Assert.Equal([SettingsPath], Directory.GetFileSystemEntries(_root));
    }
}
