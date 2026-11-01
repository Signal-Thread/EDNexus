using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;

namespace EDNexus.Plugins.Hosting.Tests;

public sealed class PluginStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednx-storage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private SandboxedPluginStorage Storage(string id = "my.plugin") => new(_root, id);

    [Fact]
    public void RoundTrips_AndLivesUnderPluginDataDir()
    {
        var s = Storage();
        Assert.Null(s.GetString("a"));
        s.SetString("a", "héllo");
        Assert.Equal("héllo", s.GetString("a"));
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "my.plugin", "data"), s.DataDirectory);
        Assert.All(Directory.GetFiles(_root, "*", SearchOption.AllDirectories), f => Assert.StartsWith(s.DataDirectory, f));
        s.SetString("a", "two");
        Assert.Equal("two", s.GetString("a"));
        s.Remove("a");
        Assert.Null(s.GetString("a"));
        s.Remove("a");
    }

    [Fact]
    public void PluginsAreIsolated()
    {
        Storage("one.plugin").SetString("k", "1");
        Assert.Null(Storage("two.plugin").GetString("k"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("a/b")]
    [InlineData("C:\\x")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a\0b")]
    public void BadKeys_AreRejected(string key)
    {
        var s = Storage();
        Assert.Throws<ArgumentException>(() => s.GetString(key));
        Assert.Throws<ArgumentException>(() => s.SetString(key, "x"));
        Assert.Throws<ArgumentException>(() => s.Remove(key));
    }

    [Fact]
    public void DeviceNameKey_StaysInsideDataDir()
    {
        var s = Storage();
        s.SetString("CON", "x");
        Assert.Equal("x", s.GetString("CON"));
        // ".." is a legal key but maps to the file k_...txt, so it cannot climb out.
        s.SetString("..", "y");
        Assert.Equal("y", s.GetString(".."));
        Assert.All(Directory.GetFiles(_root, "*", SearchOption.AllDirectories), f => Assert.StartsWith(s.DataDirectory, f));
    }

    [Fact]
    public void OversizeValue_IsRejected()
        => Assert.Throws<ArgumentException>(() => Storage().SetString("k", new string('x', SandboxedPluginStorage.MaxValueBytes + 1)));

    [Fact]
    public void UnreadableFile_ReadsAsUnset()
    {
        var s = Storage();
        s.SetString("k", "v");
        var file = Directory.GetFiles(s.DataDirectory).Single();
        File.Delete(file);
        Directory.CreateDirectory(file); // a directory where the file should be
        Assert.Null(s.GetString("k"));
    }

    private sealed record Prefs(string Name, int Count);

    [Fact]
    public void Json_RoundTrips_AndCorruptOrMissingIsDefault()
    {
        var s = Storage();
        Assert.Null(s.ReadJson<Prefs>("p"));
        s.WriteJson("p", new Prefs("x", 3));
        Assert.Equal(new Prefs("x", 3), s.ReadJson<Prefs>("p"));
        s.SetString("p", "{ not json");
        Assert.Null(s.ReadJson<Prefs>("p"));
        s.SetString("p", "[1,2]");
        Assert.Null(s.ReadJson<Prefs>("p"));
    }

    private PluginBridgeSession Attach(string[] declared, string[] granted, string? root)
    {
        var bridge = new PluginBridge(new JournalEventBus(), new CommanderState(), new PluginBridgeOptions { PluginsRoot = root });
        var manifest = new PluginManifest("cap.plugin", "cap", "1.0.0", PluginSdk.CurrentVersionString) { Capabilities = declared };
        return bridge.Attach(manifest, granted);
    }

    [Fact]
    public void Bridge_GrantedStorage_GetsWorkingSandbox()
    {
        using var session = Attach([PluginCapabilities.Storage], [PluginCapabilities.Storage], _root);
        session.Storage.SetString("k", "v");
        Assert.Equal("v", session.Storage.GetString("k"));
    }

    [Fact]
    public void Bridge_WithoutGrantOrRoot_Denies()
    {
        using var notGranted = Attach([PluginCapabilities.Storage], [], _root);
        Assert.Throws<UnauthorizedAccessException>(() => notGranted.Storage.SetString("k", "v"));
        using var noRoot = Attach([PluginCapabilities.Storage], [PluginCapabilities.Storage], null);
        Assert.Throws<UnauthorizedAccessException>(() => noRoot.Storage.GetString("k"));
        Assert.False(Directory.Exists(_root));
    }
}
