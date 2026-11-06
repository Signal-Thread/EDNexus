using EDNexus.Plugins.Hosting.Bridge;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// A plugin's stored values can hold the commander's name and credits (via the events / state
/// capabilities), so on Unix the data folder is 0700 and every file 0600, whatever the umask. The
/// Windows case is a no-op (the per-user LocalAppData ACL scopes the folder), so these tests return
/// early there; the Windows behaviour is not verified by them.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]   // for the analyzer; each test also returns early on Windows
public sealed class PluginStoragePermissionsTests : IDisposable
{
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednx-storage-perm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private SandboxedPluginStorage Storage(string id)
    {
        Directory.CreateDirectory(Path.Combine(_root, id));   // the installer owns the plugin folder (default mode)
        return new SandboxedPluginStorage(_root, id);
    }

    [Fact]
    public void TheDataFolderAndItsFiles_AreOwnerOnly_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var storage = Storage("com.test.perm.fresh");

        storage.SetString("commander", "CMDR Jameson, 1,234,567 cr");
        storage.SetString("second", "x");

        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(storage.DataDirectory));
        var files = Directory.GetFiles(storage.DataDirectory);
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.Equal(OwnerFile, File.GetUnixFileMode(f)));
    }

    [Fact]
    public void AnOverwrite_KeepsTheFileOwnerOnly_AndLeavesNoTempFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var storage = Storage("com.test.perm.overwrite");

        storage.SetString("k", "one");
        storage.SetString("k", "two");

        var file = Assert.Single(Directory.GetFiles(storage.DataDirectory));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(file));
        Assert.Equal("two", storage.GetString("k"));
    }

    [Fact]
    public void AFolderAndFileFromBeforeThisWasEnforced_AreClosedOnTheNextWrite()
    {
        if (OperatingSystem.IsWindows()) return;
        var storage = Storage("com.test.perm.legacy");
        Directory.CreateDirectory(storage.DataDirectory);
        var legacy = Path.Combine(storage.DataDirectory, "k_old.txt");
        File.WriteAllText(legacy, "left over");
        File.SetUnixFileMode(storage.DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(legacy, OwnerFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        storage.SetString("old", "rewritten");   // replaces the legacy file under the same key
        storage.SetString("new", "fresh");

        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(storage.DataDirectory));
        Assert.All(Directory.GetFiles(storage.DataDirectory), f => Assert.Equal(OwnerFile, File.GetUnixFileMode(f)));
        Assert.Equal("rewritten", storage.GetString("old"));
    }

    [Fact]
    public void ThePluginFolderItself_IsNotChanged()
    {
        if (OperatingSystem.IsWindows()) return;
        var storage = Storage("com.test.perm.parent");
        var pluginDir = Path.Combine(_root, "com.test.perm.parent");
        var before = File.GetUnixFileMode(pluginDir);

        storage.SetString("k", "v");

        Assert.Equal(before, File.GetUnixFileMode(pluginDir));
    }
}
