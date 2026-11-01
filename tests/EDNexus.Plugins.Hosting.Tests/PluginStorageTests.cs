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

    private SandboxedPluginStorage Storage(string id = "my.plugin")
    {
        Directory.CreateDirectory(Path.Combine(_root, id)); // the installer owns the plugin folder
        return new(_root, id);
    }

    /// <summary>True when <paramref name="file"/> is directly inside <paramref name="dir"/> (separator-aware).</summary>
    private static bool IsInside(string dir, string file)
        => Path.GetDirectoryName(file) == dir && !Path.GetRelativePath(dir, file).StartsWith("..", StringComparison.Ordinal);

    private void AssertAllFilesInside(SandboxedPluginStorage s)
        => Assert.All(Directory.GetFiles(Path.Combine(_root, "my.plugin"), "*", SearchOption.AllDirectories),
            f => Assert.True(IsInside(s.DataDirectory, f), f));

    [Fact]
    public void RoundTrips_AndLivesUnderPluginDataDir()
    {
        var s = Storage();
        Assert.Null(s.GetString("a"));
        s.SetString("a", "héllo");
        Assert.Equal("héllo", s.GetString("a"));
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "my.plugin", "data"), s.DataDirectory);
        AssertAllFilesInside(s);
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
    public void DeviceNameKey_StaysInsideDataDir_WithExpectedFilenames()
    {
        var s = Storage();
        s.SetString("CON", "x");
        Assert.Equal("x", s.GetString("CON"));
        // ".." is a legal key but maps to the file k_...txt, so it cannot climb out.
        s.SetString("..", "y");
        Assert.Equal("y", s.GetString(".."));
        AssertAllFilesInside(s);
        Assert.Equal(["k_...txt","k_con.txt"], Directory.GetFiles(s.DataDirectory).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void SiblingDirectorySharingPrefix_IsNotInside()
        => Assert.False(IsInside(Path.Combine(_root, "p", "data"), Path.Combine(_root, "p", "data2", "k_x.txt")));

    [Fact]
    public void Keys_AreCaseInsensitive_OnEveryPlatform()
    {
        var s = Storage();
        s.SetString("Foo", "a");
        s.SetString("foo", "b");
        Assert.Equal("b", s.GetString("FOO"));
        Assert.Single(Directory.GetFiles(s.DataDirectory));
        s.Remove("fOO");
        Assert.Null(s.GetString("Foo"));
    }

    [Fact]
    public void KeyLength_100Ok_101Rejected()
    {
        var s = Storage();
        s.SetString(new string('k', 100), "v");
        Assert.Equal("v", s.GetString(new string('k', 100)));
        Assert.Throws<ArgumentException>(() => s.SetString(new string('k', 101), "v"));
    }

    [Fact]
    public void ArbitraryKeys_NeverEscapeDataDir()
    {
        var s = Storage();
        var rng = new Random(12345);
        const string alphabet = "abcXYZ019._-/\\: \0%~*?\u00e9";
        for (var i = 0; i < 400; i++)
        {
            var key = new string(Enumerable.Range(0, rng.Next(1, 12)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
            try { s.SetString(key, "v"); }
            catch (ArgumentException) { /* rejected keys are fine */ }
        }
        AssertAllFilesInside(s);
        Assert.False(Directory.Exists(Path.Combine(_root, "my.plugin", "..", "..", "x")));
    }

    [Fact]
    public void OversizeValue_IsRejected()
        => Assert.Throws<ArgumentException>(() => Storage().SetString("k", new string('x', SandboxedPluginStorage.MaxValueBytes + 1)));

    [Fact]
    public void MultibyteOversize_IsMeasuredInBytes()
    {
        var s = Storage();
        // 600k two-byte characters: under the limit in chars, over it in bytes.
        Assert.Throws<ArgumentException>(() => s.SetString("k", new string('\u00e9', 600_000)));
        Assert.Null(s.GetString("k"));
    }

    [Fact]
    public void ExactlyOneMiB_IsAccepted_AndRoundTrips()
    {
        var s = Storage();
        var value = new string('x', SandboxedPluginStorage.MaxValueBytes);
        s.SetString("k", value);
        Assert.Equal(value, s.GetString("k"));
    }

    [Fact]
    public void LoneSurrogate_IsRejected_NotSubstituted()
    {
        var s = Storage();
        Assert.Throws<ArgumentException>(() => s.SetString("k", "a\uD800b"));
        Assert.Null(s.GetString("k"));
        s.SetString("k", "\uD83D\uDE00"); // a valid pair is fine
        Assert.Equal("\uD83D\uDE00", s.GetString("k"));
    }

    private sealed record Prefs(string Name, int Count);

    [Fact]
    public void UnreadableValue_Throws_RatherThanReadingAsUnset()
    {
        var s = Storage();
        s.SetString("k", "v");
        var file = Directory.GetFiles(s.DataDirectory).Single();
        File.Delete(file);
        Directory.CreateDirectory(file); // a directory where the file should be
        Assert.ThrowsAny<Exception>(() => s.GetString("k"));
        Assert.ThrowsAny<Exception>(() => s.ReadJson<Prefs>("k"));
    }

    [Fact]
    public void LockedValue_ThrowsOnRead_AndGoodDataSurvives()
    {
        var s = Storage();
        s.SetString("k", "good");
        var file = Directory.GetFiles(s.DataDirectory).Single();
        // Exclusive handle: on Windows this blocks the read; elsewhere it is advisory only.
        using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (OperatingSystem.IsWindows())
                Assert.ThrowsAny<IOException>(() => s.GetString("k"));
        }
        Assert.Equal("good", s.GetString("k"));
    }

    [Fact]
    public void PlantedOversizeFile_IsRefusedOnRead()
    {
        var s = Storage();
        s.SetString("k", "v");
        var file = Directory.GetFiles(s.DataDirectory).Single();
        File.WriteAllBytes(file, new byte[SandboxedPluginStorage.MaxValueBytes + 1]);
        Assert.Throws<IOException>(() => s.GetString("k"));
    }

    [Fact]
    public void Write_LeavesNoTempFiles_AndSweepsStaleOnes()
    {
        var s = Storage();
        s.SetString("k", "1");
        s.SetString("k", "2");
        Assert.DoesNotContain(Directory.GetFiles(s.DataDirectory), f => f.EndsWith(".tmp", StringComparison.Ordinal));

        var stale = Path.Combine(s.DataDirectory, "k_x.txt.deadbeef.tmp");
        File.WriteAllText(stale, "junk");
        Storage().SetString("k", "3"); // a fresh instance sweeps on its first write
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void KeyCountQuota_IsEnforced_ButOverwriteStillWorks()
    {
        var s = Storage();
        for (var i = 0; i < SandboxedPluginStorage.MaxKeyCount; i++) s.SetString("k" + i, "v");
        Assert.Throws<IOException>(() => s.SetString("extra", "v"));
        s.SetString("k0", "updated");
        Assert.Equal("updated", s.GetString("k0"));
    }

    [Fact]
    public void TotalBytesQuota_IsEnforced()
    {
        var s = Storage();
        var mib = new string('x', SandboxedPluginStorage.MaxValueBytes);
        var fit = (int)(SandboxedPluginStorage.MaxTotalBytes / SandboxedPluginStorage.MaxValueBytes);
        for (var i = 0; i < fit; i++) s.SetString("big" + i, mib);
        Assert.Throws<IOException>(() => s.SetString("one-more", "x"));
        s.Remove("big0");
        s.SetString("one-more", "x");
    }

    [Fact]
    public void MissingPluginFolder_IsNotRecreated()
    {
        var s = new SandboxedPluginStorage(_root, "gone.plugin");
        Assert.Throws<DirectoryNotFoundException>(() => s.SetString("k", "v"));
        Assert.False(Directory.Exists(Path.Combine(_root, "gone.plugin")));
    }

    [Fact]
    public void ConcurrentWriters_AcrossInstances_NeverTearAValue()
    {
        Storage();
        var a = new SandboxedPluginStorage(_root, "my.plugin");
        var b = new SandboxedPluginStorage(_root, "my.plugin");
        var x = new string('a', 50_000);
        var y = new string('b', 50_000);
        Parallel.For(0, 200, i =>
        {
            (i % 2 == 0 ? a : b).SetString("k", i % 3 == 0 ? x : y);
            var got = (i % 2 == 0 ? b : a).GetString("k");
            Assert.True(got == x || got == y);
        });
        Assert.DoesNotContain(Directory.GetFiles(a.DataDirectory), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

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

    private sealed class Ambiguous
    {
        public Ambiguous(int a) { }
        public Ambiguous(string b) { }
    }

    [Fact]
    public void ReadJson_UnconstructableType_IsDefault()
    {
        var s = Storage();
        s.SetString("p", "{}");
        Assert.Null(s.ReadJson<Ambiguous>("p"));
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
        Directory.CreateDirectory(Path.Combine(_root, "cap.plugin"));
        using var session = Attach([PluginCapabilities.Storage], [PluginCapabilities.Storage], _root);
        session.Storage.SetString("k", "v");
        Assert.Equal("v", session.Storage.GetString("k"));
    }

    [Fact]
    public void Bridge_DisposedSession_RevokesStorage_AndCannotResurrectFolder()
    {
        var pluginDir = Path.Combine(_root, "cap.plugin");
        Directory.CreateDirectory(pluginDir);
        var session = Attach([PluginCapabilities.Storage], [PluginCapabilities.Storage], _root);
        var storage = session.Storage;
        storage.SetString("k", "v");
        session.Dispose();
        Directory.Delete(pluginDir, recursive: true); // uninstalled
        Assert.Throws<UnauthorizedAccessException>(() => storage.SetString("k", "late"));
        Assert.Throws<UnauthorizedAccessException>(() => storage.GetString("k"));
        Assert.False(Directory.Exists(pluginDir));
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
