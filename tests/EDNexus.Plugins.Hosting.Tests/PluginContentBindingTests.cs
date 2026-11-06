using System.Security.Cryptography;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// A consent grant is bound to the plugin's code, not just its <c>plugin.json</c>: the same manifest
/// over different bytes (a swapped DLL, a changed dependency, an added file, a package re-installed
/// over the old one) is denied, while the plugin's own <c>data/</c> folder is free to change. Real
/// compiled plugins are driven through a real <see cref="PluginHost"/>; every test has its own
/// plugin ids and nothing sleeps.
/// </summary>
public sealed class PluginContentBindingTests : IDisposable
{
    private static readonly string[] Events = ["events"];

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Installs a standard plugin in <c>&lt;root&gt;/&lt;id&gt;</c>; <paramref name="ctorBody"/> makes its DLL's bytes differ.</summary>
    private string Install(string id, string ns, string ctorBody = "", params (string Name, byte[] Content)[] extra)
        => WriteFolder(_dir.Path, id, Manifest(id, ns + ".dll", ns + ".Plugin", capabilities: "\"events\",\"storage\""),
            [(ns + ".dll", Compile(ns, PluginSource(ns, ctorBody: ctorBody))), .. extra]);

    private string Folder(string id) => Path.Combine(_dir.Path, id);

    /// <summary>Loads <paramref name="id"/> on a fresh host (a fresh process-equivalent: nothing carried over) and returns what happened.</summary>
    private (PluginLoadResult Result, RecordingContexts Contexts) Load(string id, IPluginGrantStore store)
    {
        var contexts = new RecordingContexts();
        using var host = contexts.Host(_dir.Path, consent: PluginConsent.FromGrants(store));
        return (host.LoadPlugin(id), contexts);
    }

    private static string KeyRan(string key) => $"System.AppDomain.CurrentDomain.SetData(\"{key}\", \"ctor ran\");";

    [Fact]
    public void AMatchingGrant_Loads_WithOnlyTheGrantedCapabilities_AndAReloadOfTheSameFilesStillMatches()
    {
        const string id = "com.test.bind.match";
        Install(id, "BindMatch", "", ("lib/Dep.dll", [1, 2, 3]));
        var store = new InMemoryPluginGrantStore();
        Assert.Equal(PluginLoadStatus.Denied, Load(id, store).Result.Status);   // nothing granted yet

        store.Grant(Folder(id), Events);   // storage is declared but not granted
        var (first, contexts) = Load(id, store);
        var (second, _) = Load(id, store);

        Assert.Equal(PluginLoadStatus.Loaded, first.Status);
        Assert.Equal(["events"], contexts.Granted[id]);
        Assert.Equal(PluginLoadStatus.Loaded, second.Status);
    }

    [Fact]
    public void SameManifest_DifferentDllBytes_IsDenied_AndTheNewCodeNeverRuns()
    {
        const string id = "com.test.bind.dll";
        var key = "bind.ran." + Guid.NewGuid().ToString("N");
        AppDomain.CurrentDomain.SetData(key, null);
        Install(id, "BindDll");
        var store = new InMemoryPluginGrantStore();
        store.Grant(Folder(id), Events);
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, store).Result.Status);   // positive control: the grant is good

        var manifestBefore = File.ReadAllText(Path.Combine(Folder(id), "plugin.json"));
        // The attacker's DLL under a byte-identical plugin.json.
        File.WriteAllBytes(Path.Combine(Folder(id), "BindDll.dll"), Compile("BindDll", PluginSource("BindDll", ctorBody: KeyRan(key))));
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(Folder(id), "plugin.json")));

        var (result, contexts) = Load(id, store);

        Assert.Equal(PluginLoadStatus.Denied, result.Status);
        Assert.Null(AppDomain.CurrentDomain.GetData(key));
        Assert.Empty(contexts.Lines);
    }

    [Fact]
    public void TheClassicAttack_UninstallThenAPackageWithACopiedManifest_IsDenied()
    {
        const string id = "com.test.bind.trojan";
        var key = "bind.ran." + Guid.NewGuid().ToString("N");
        AppDomain.CurrentDomain.SetData(key, null);
        var legit = Install(id, "BindTrojan", "", ("lib/Dep.dll", [1, 2, 3]));
        var manifestJson = File.ReadAllText(Path.Combine(legit, "plugin.json"));
        var store = new InMemoryPluginGrantStore();
        store.Grant(legit, Events);
        Directory.Delete(legit, recursive: true);   // removed by hand: the grant stays behind

        // Later: a trojanised package with a verbatim copy of the (public) manifest.
        WriteFolder(_dir.Path, id, manifestJson, ("BindTrojan.dll", Compile("BindTrojan", PluginSource("BindTrojan", ctorBody: KeyRan(key)))));
        var (result, _) = Load(id, store);

        Assert.Equal(PluginLoadStatus.Denied, result.Status);
        Assert.Null(AppDomain.CurrentDomain.GetData(key));
    }

    [Fact]
    public void SameManifestAndDll_ButAChangedSiblingFile_IsDenied()
    {
        const string id = "com.test.bind.sibling";
        Install(id, "BindSibling", "", ("lib/Dep.dll", [1, 2, 3]), ("native/libfoo.so", [9, 9]));
        var store = new InMemoryPluginGrantStore();
        store.Grant(Folder(id), Events);
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, store).Result.Status);

        File.WriteAllBytes(Path.Combine(Folder(id), "lib", "Dep.dll"), [1, 2, 4]);

        Assert.Equal(PluginLoadStatus.Denied, Load(id, store).Result.Status);
    }

    [Fact]
    public void AnAddedFile_OrARemovedFile_IsDenied()
    {
        const string id = "com.test.bind.addremove";
        Install(id, "BindAddRemove", "", ("lib/Dep.dll", [1, 2, 3]));
        var store = new InMemoryPluginGrantStore();
        store.Grant(Folder(id), Events);
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, store).Result.Status);

        var added = Path.Combine(Folder(id), "notes.txt");
        File.WriteAllText(added, "added later");
        Assert.Equal(PluginLoadStatus.Denied, Load(id, store).Result.Status);

        File.Delete(added);
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, store).Result.Status);   // back to what was approved

        File.Delete(Path.Combine(Folder(id), "lib", "Dep.dll"));
        Assert.Equal(PluginLoadStatus.Denied, Load(id, store).Result.Status);
    }

    [Fact]
    public void ARenamedFile_WithTheSameContent_IsDenied()
    {
        const string id = "com.test.bind.rename";
        Install(id, "BindRename", "", ("lib/Dep.dll", [1, 2, 3]));
        var store = new InMemoryPluginGrantStore();
        store.Grant(Folder(id), Events);

        File.Move(Path.Combine(Folder(id), "lib", "Dep.dll"), Path.Combine(Folder(id), "lib", "Dep2.dll"));

        Assert.Equal(PluginLoadStatus.Denied, Load(id, store).Result.Status);
    }

    [Fact]
    public void ChangesUnderTheDataFolder_DoNotInvalidateTheGrant()
    {
        const string id = "com.test.bind.data";
        Install(id, "BindData", "", ("lib/Dep.dll", [1, 2, 3]));
        var store = new InMemoryPluginGrantStore();
        store.Grant(Folder(id), Events);
        var data = Path.Combine(Folder(id), "data");

        // The plugin's own storage appears, changes, grows a sub-folder and is cleared.
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "k_state.txt"), "one");
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, store).Result.Status);
        File.WriteAllText(Path.Combine(data, "k_state.txt"), "two, and longer");
        Directory.CreateDirectory(Path.Combine(data, "cache"));
        File.WriteAllBytes(Path.Combine(data, "cache", "blob.bin"), [7, 7, 7]);
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, store).Result.Status);
        Directory.Delete(data, recursive: true);
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, store).Result.Status);

        // A grant made while data/ already had content is the same grant.
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "k_x.txt"), "x");
        var again = new InMemoryPluginGrantStore();
        again.Grant(Folder(id), Events);
        Directory.Delete(data, recursive: true);
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, again).Result.Status);
    }

    [Fact]
    public void ADataFolderNestedBelowTheTop_IsHashedLikeAnyOtherFolder()
    {
        const string id = "com.test.bind.nesteddata";
        Install(id, "BindNestedData", "", ("lib/data/x.dll", [1]));
        var store = new InMemoryPluginGrantStore();
        store.Grant(Folder(id), Events);
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, store).Result.Status);

        File.WriteAllBytes(Path.Combine(Folder(id), "lib", "data", "x.dll"), [2]);

        Assert.Equal(PluginLoadStatus.Denied, Load(id, store).Result.Status);
    }

    [Fact]
    public void AnOldFormatGrant_WithoutContentHashes_IsDenied_EvenWithAMatchingManifest()
    {
        const string id = "com.test.bind.old";
        var dir = Install(id, "BindOld");
        var manifest = PluginManifestParser.ParseFile(Path.Combine(dir, "plugin.json")).Manifest!;
        var store = new InMemoryPluginGrantStore();
        store.Set(id, new PluginGrant(PluginManifestFingerprint.Compute(manifest), "", "", Events));

        var (result, contexts) = Load(id, store);

        Assert.Equal(PluginLoadStatus.Denied, result.Status);
        Assert.Empty(contexts.Lines);
    }

    [Fact]
    public void AGrantForOneId_NeverAppliesToAnotherPluginWithIdenticalFiles()
    {
        const string x = "com.test.bind.idx";
        const string y = "com.test.bind.idy";
        Install(x, "BindIdX");
        // Y is a copy of X's folder under another id (so identical DLL and sibling bytes).
        var xDir = Folder(x);
        var yDir = Path.Combine(_dir.Path, y);
        Directory.CreateDirectory(yDir);
        File.Copy(Path.Combine(xDir, "BindIdX.dll"), Path.Combine(yDir, "BindIdX.dll"));
        File.WriteAllText(Path.Combine(yDir, "plugin.json"), File.ReadAllText(Path.Combine(xDir, "plugin.json")).Replace(x, y));
        var store = new InMemoryPluginGrantStore();
        store.Grant(xDir, Events);

        Assert.Equal(PluginLoadStatus.Loaded, Load(x, store).Result.Status);
        Assert.Equal(PluginLoadStatus.Denied, Load(y, store).Result.Status);
    }

    private sealed class ThrowingStore : IPluginGrantStore
    {
        public PluginGrant? Get(string pluginId) => throw new IOException("settings unreadable");
        public void Set(string pluginId, PluginGrant grant) => throw new IOException();
        public bool Remove(string pluginId) => throw new IOException();
    }

    [Fact]
    public void AStoreThatThrows_DeniesTheLoad()
    {
        const string id = "com.test.bind.throwing";
        Install(id, "BindThrowing");

        var (result, contexts) = Load(id, new ThrowingStore());

        Assert.Equal(PluginLoadStatus.Denied, result.Status);
        Assert.Empty(contexts.Lines);
    }

    [Fact]
    public void TheEntryHash_IsTheSha256OfTheBytesThatAreLoaded_EvenIfTheFileChangesDuringConsent()
    {
        const string id = "com.test.bind.toctou";
        var key = "bind.ran." + Guid.NewGuid().ToString("N");
        var original = Compile("BindToctou", PluginSource("BindToctou", ctorBody: $"System.AppDomain.CurrentDomain.SetData(\"{key}\", \"original\");"));
        var swapped = Compile("BindToctou", PluginSource("BindToctou", ctorBody: $"System.AppDomain.CurrentDomain.SetData(\"{key}\", \"swapped\");"));
        WriteFolder(_dir.Path, id, Manifest(id, "BindToctou.dll", "BindToctou.Plugin"), ("BindToctou.dll", original));
        PluginConsentRequest? seen = null;
        var contexts = new RecordingContexts();
        using var host = contexts.Host(_dir.Path, consent: request =>
        {
            seen = request;
            // The file is replaced after it was read and hashed, before the plugin is loaded.
            File.WriteAllBytes(Path.Combine(Folder(id), "BindToctou.dll"), swapped);
            return request.Manifest.Capabilities;
        });

        var result = host.LoadPlugin(id);

        Assert.Equal(PluginLoadStatus.Loaded, result.Status);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), seen!.EntryAssemblyHash);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(swapped)), seen.EntryAssemblyHash);
        Assert.Equal("original", AppDomain.CurrentDomain.GetData(key));   // the hashed bytes are what ran
    }

    [Fact]
    public void ALinkInsideThePluginFolder_IsRefusedBeforeConsentIsAsked()
    {
        const string id = "com.test.bind.link";
        var dir = Install(id, "BindLink");
        var outside = Path.Combine(_dir.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "Evil.dll"), [6, 6, 6]);
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "Evil.dll"), Path.Combine(outside, "Evil.dll"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;   // symlinks unavailable here (e.g. a restricted Windows account)
        }
        var asked = 0;
        var contexts = new RecordingContexts();
        using var host = contexts.Host(_dir.Path, consent: request => { asked++; return request.Manifest.Capabilities; });

        var result = host.LoadPlugin(id);

        Assert.Equal(PluginLoadStatus.Rejected, result.Status);
        Assert.Contains("symbolic link", result.ReasonSummary);
        Assert.Equal(0, asked);
        Assert.Empty(contexts.Lines);
    }

    // ---- the hash itself -------------------------------------------------------------------------

    private string Tree(string name, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(dir);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return dir;
    }

    private static string Hash(string dir, string? entry = null)
    {
        var result = PluginContentHash.OfFolder(dir, entry);
        Assert.True(result.Hash is not null, result.Problem);
        return result.Hash!;
    }

    [Fact]
    public void FolderHash_IsDeterministic_AndIndependentOfCreationOrder()
    {
        var a = Tree("order-a", ("b.txt", "2"), ("a.txt", "1"), ("sub/z.txt", "3"), ("sub/y.txt", "4"), ("A.txt", "5"));
        var b = Tree("order-b", ("A.txt", "5"), ("sub/y.txt", "4"), ("sub/z.txt", "3"), ("a.txt", "1"), ("b.txt", "2"));

        Assert.Equal(Hash(a), Hash(a));
        Assert.Equal(Hash(a), Hash(b));
        Assert.Matches("^[0-9a-f]{64}$", Hash(a));
    }

    [Fact]
    public void FolderHash_DistinguishesPathsContentAndBoundaries()
    {
        var baseline = Hash(Tree("fmt-base", ("a.txt", "xy"), ("b.txt", "z")));

        Assert.NotEqual(baseline, Hash(Tree("fmt-split", ("a.txt", "x"), ("b.txt", "yz"))));      // content boundary moved
        Assert.NotEqual(baseline, Hash(Tree("fmt-name", ("a.txt", "xy"), ("c.txt", "z"))));       // renamed
        Assert.NotEqual(baseline, Hash(Tree("fmt-sub", ("a.txt", "xy"), ("sub/b.txt", "z"))));    // moved into a folder
        Assert.NotEqual(baseline, Hash(Tree("fmt-extra", ("a.txt", "xy"), ("b.txt", "z"), ("c.txt", ""))));   // an empty extra file
        Assert.NotEqual(baseline, Hash(Tree("fmt-merged", ("ab.txt", "xyz"))));                   // names merged
    }

    [Fact]
    public void FolderHash_ExcludesTheEntryAssembly_TheManifest_AndOnlyTheTopLevelDataFolder()
    {
        var dir = Tree("excl", ("plugin.json", "{}"), ("Entry.dll", "e1"), ("Dep.dll", "d"), ("data/k_a.txt", "a"));
        var entry = Path.Combine(dir, "Entry.dll");
        var baseline = Hash(dir, entry);

        File.WriteAllText(entry, "e2");
        File.WriteAllText(Path.Combine(dir, "plugin.json"), "{ \"changed\": true }");
        File.WriteAllText(Path.Combine(dir, "data", "k_a.txt"), "changed");
        File.WriteAllText(Path.Combine(dir, "data", "new.bin"), "new");
        Assert.Equal(baseline, Hash(dir, entry));

        // With no entry path named, the assembly counts like any other file.
        Assert.NotEqual(Hash(dir, entry), Hash(dir));
        File.WriteAllText(Path.Combine(dir, "Dep.dll"), "d2");
        Assert.NotEqual(baseline, Hash(dir, entry));
    }

    [Fact]
    public void FolderHash_IsRefused_WhenTheFolderBreaksTheBounds()
    {
        var dir = Tree("bounds", ("a.txt", "12345"), ("b.txt", "12345"), ("c.txt", "12345"));

        var tooMany = PluginContentHash.OfFolder(dir, null, new PluginPackageLimits { MaxEntries = 2 });
        var tooBig = PluginContentHash.OfFolder(dir, null, new PluginPackageLimits { MaxEntryBytes = 4 });
        var tooMuch = PluginContentHash.OfFolder(dir, null, new PluginPackageLimits { MaxTotalUncompressedBytes = 14 });
        var fine = PluginContentHash.OfFolder(dir, null, new PluginPackageLimits { MaxEntries = 3, MaxEntryBytes = 5, MaxTotalUncompressedBytes = 15 });

        Assert.Null(tooMany.Hash);
        Assert.Contains("more than 2", tooMany.Problem);
        Assert.Null(tooBig.Hash);
        Assert.Contains("per-file limit", tooBig.Problem);
        Assert.Null(tooMuch.Hash);
        Assert.Contains("limit", tooMuch.Problem);
        Assert.NotNull(fine.Hash);

        var deep = Tree("deep", (string.Join("/", Enumerable.Repeat("d", PluginPathRules.MaxDepth + 1)) + "/f.txt", "x"));
        Assert.Contains("nested deeper", PluginContentHash.OfFolder(deep, null).Problem);
    }

    [Fact]
    public void FolderHash_OfAMissingFolder_IsReportedNotThrown()
    {
        var result = PluginContentHash.OfFolder(Path.Combine(_dir.Path, "nope"), null);

        Assert.Null(result.Hash);
        Assert.True(result.Unreadable);
    }

    [Fact]
    public void AssemblyHash_IsThePlainSha256_ReproducibleWithStandardTools()
    {
        byte[] image = [1, 2, 3, 4];

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(image)), PluginContentHash.OfAssembly(image));
        Assert.Equal("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", PluginContentHash.OfAssembly(image));
    }

    // ---- install / update -----------------------------------------------------------------------

    private byte[] Package(string id, string ns, string ctorBody = "", string version = "1.0.0", params (string Name, byte[] Content)[] extra)
        => Zip([
            ("plugin.json", Utf8(Manifest(id, ns + ".dll", ns + ".Plugin").Replace("\"version\": \"1.0.0\"", $"\"version\": \"{version}\""))),
            (ns + ".dll", Compile(ns, PluginSource(ns, ctorBody: ctorBody))),
            .. extra]);

    [Fact]
    public void AnUpdateOverAnExistingId_IsDeniedUntilGrantedAgain()
    {
        const string id = "com.test.bind.update";
        var root = _dir.Path;
        var store = new InMemoryPluginGrantStore();
        Assert.True(PluginInstaller.Install(_dir.Write("v1.ednplugin", Package(id, "BindUpdate")), root).Succeeded);
        store.Grant(Folder(id), Events);
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, store).Result.Status);

        // Same id, same plugin.json bytes, different code.
        var v2 = Package(id, "BindUpdate", ctorBody: "System.GC.KeepAlive(this);");
        Assert.True(PluginInstaller.Install(_dir.Write("v2.ednplugin", v2), root, replaceExisting: true).Succeeded);
        Assert.Equal(PluginLoadStatus.Denied, Load(id, store).Result.Status);

        store.Grant(Folder(id), Events);
        Assert.Equal(PluginLoadStatus.Loaded, Load(id, store).Result.Status);

        // Same id, same DLL, only a dependency file changed.
        var v3 = Package(id, "BindUpdate", "System.GC.KeepAlive(this);", "1.0.0", ("lib/Dep.dll", [1]));
        Assert.True(PluginInstaller.Install(_dir.Write("v3.ednplugin", v3), root, replaceExisting: true).Succeeded);
        Assert.Equal(PluginLoadStatus.Denied, Load(id, store).Result.Status);
    }

    [Fact]
    public void ConsentRequest_TryRead_MatchesWhatTheHostBuilds()
    {
        const string id = "com.test.bind.tryread";
        var dir = Install(id, "BindTryRead", "", ("lib/Dep.dll", [1, 2, 3]));
        PluginConsentRequest? fromHost = null;
        var contexts = new RecordingContexts();
        using var host = contexts.Host(_dir.Path, consent: request => { fromHost = request; return request.Manifest.Capabilities; });
        host.LoadPlugin(id);

        Assert.True(PluginConsentRequest.TryRead(dir, out var fromRead, out var problem), problem);

        Assert.Equal(fromHost!.EntryAssemblyHash, fromRead!.EntryAssemblyHash);
        Assert.Equal(fromHost.FilesHash, fromRead.FilesHash);
        Assert.Equal(fromHost.Manifest, fromRead.Manifest);
        Assert.False(PluginConsentRequest.TryRead(Path.Combine(_dir.Path, "missing"), out var none, out var why));
        Assert.Null(none);
        Assert.False(string.IsNullOrEmpty(why));
    }
}
