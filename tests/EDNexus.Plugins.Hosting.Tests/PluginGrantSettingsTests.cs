using EDNexus.Core.Settings;
using EDNexus.Plugins.Abstractions;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// The consent grants as they come off a real <c>settings.json</c>: loaded through
/// <see cref="SettingsStore"/> (the System.Text.Json deserialiser), not built by calling the setters.
/// A file the commander edited by hand, or one an older build wrote, may lack the grants altogether,
/// hold <c>null</c>s, or hold grants without the content hashes. Every such shape must load without
/// throwing and mean "no grant": the default is deny.
/// </summary>
public sealed class PluginGrantSettingsTests : IDisposable
{
    private const string Id = "com.test.grantjson";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PluginManifest Sample(string id = Id) => new(id, "Grant JSON test", "1.0.0", PluginSdk.CurrentVersionString, "Author", "Does things")
    {
        EntryAssembly = "A.dll",
        EntryType = "A.Plugin",
        Capabilities = ["events"],
    };

    private static PluginConsentRequest Request(PluginManifest manifest) => new(manifest, "e1", "f1");

    private string Path(string name = "settings.json") => System.IO.Path.Combine(_dir.Path, name);

    private AppSettings Load(string json)
    {
        File.WriteAllText(Path(), json);
        return new SettingsStore(Path()).Load();
    }

    private static string FullGrantJson(string id = Id) => $$"""
        "{{id}}": {
          "ManifestHash": "{{PluginManifestFingerprint.Compute(Sample(id))}}",
          "EntryHash": "e1",
          "FilesHash": "f1",
          "Capabilities": ["events"]
        }
        """;

    public static TheoryData<string> DocumentsWithoutAnyUsableGrant => new()
    {
        "{}",                                                                   // no Plugins section at all
        """{ "Plugins": null }""",                                              // the whole section nulled
        """{ "Plugins": {} }""",                                                // no Grants / DisabledIds
        """{ "Plugins": { "Grants": null } }""",                                // Grants nulled
        """{ "Plugins": { "Grants": {} } }""",                                  // explicitly none
        """{ "Plugins": { "Grants": { "com.test.grantjson": null } } }""",      // a null entry
        """{ "Plugins": { "Grants": { "com.test.grantjson": {} } } }""",        // an entry with nothing in it
        """{ "Plugins": { "Grants": { "com.test.grantjson": { "ManifestHash": null, "EntryHash": null, "FilesHash": null, "Capabilities": null } } } }""",
        """{ "Plugins": { "DisabledIds": null, "Grants": null } }""",
    };

    [Theory]
    [MemberData(nameof(DocumentsWithoutAnyUsableGrant))]
    public void ADocumentWithoutAUsableGrant_LoadsAsNoGrants_AndNothingIsAllowedToRun(string json)
    {
        var settings = Load(json);
        var store = new AppSettingsPluginGrantStore(settings, _ => { });
        var consent = PluginConsent.FromGrants(store);

        Assert.NotNull(settings.Plugins);
        Assert.NotNull(settings.Plugins.Grants);
        Assert.NotNull(settings.Plugins.DisabledIds);
        // A null entry is dropped; an empty one is kept but carries no hashes, which denies.
        Assert.All(settings.Plugins.Grants.Values, g => Assert.Equal(("", "", ""), (g.ManifestHash, g.EntryHash, g.FilesHash)));
        Assert.Null(consent(Request(Sample())));
    }

    [Fact]
    public void ANullPluginsSection_DoesNotStopTheStoreFromRecordingAndPersistingAGrant()
    {
        var settingsStore = new SettingsStore(Path());
        File.WriteAllText(Path(), """{ "Plugins": null }""");
        var settings = settingsStore.Load();
        var store = new AppSettingsPluginGrantStore(settings, s => Assert.True(settingsStore.TrySave(s)));

        store.Grant(Request(Sample()), ["events"]);

        // Round trip through the file: the grant written into the formerly-null section is honoured on the next load.
        var reloaded = new AppSettingsPluginGrantStore(new SettingsStore(Path()).Load(), _ => { });
        Assert.Equal(["events"], PluginConsent.FromGrants(reloaded)(Request(Sample()))!.ToArray());
    }

    [Fact]
    public void ANullOrMixedCapabilitiesList_LoadsAsWhatIsLeft_AndNeverAsNullEntries()
    {
        var settings = Load($$"""
            { "Plugins": { "Grants": {
              "com.test.grantjson.null":  { "ManifestHash": "m", "EntryHash": "e", "FilesHash": "f", "Capabilities": null },
              "com.test.grantjson.mixed": { "ManifestHash": "m", "EntryHash": "e", "FilesHash": "f", "Capabilities": [null, "events", null, "state"] },
              "com.test.grantjson.none":  { "ManifestHash": "m", "EntryHash": "e", "FilesHash": "f" }
            } } }
            """);
        var store = new AppSettingsPluginGrantStore(settings, _ => { });

        Assert.Empty(store.Get("com.test.grantjson.null")!.Capabilities);
        Assert.Equal(["events", "state"], store.Get("com.test.grantjson.mixed")!.Capabilities);
        Assert.Empty(store.Get("com.test.grantjson.none")!.Capabilities);
        Assert.Empty(settings.Plugins.Grants["com.test.grantjson.null"].Capabilities);
    }

    [Fact]
    public void GrantsWrittenBeforeContentHashesExisted_LoadWithEmptyHashes_AndAreNeverHonoured_WhileACompleteOneBesideThemIs()
    {
        var old = "com.test.grantjson.old";
        var entryOnly = "com.test.grantjson.entryonly";
        var filesOnly = "com.test.grantjson.filesonly";
        var settings = Load($$"""
            { "Plugins": { "Grants": {
              "{{old}}": { "ManifestHash": "{{PluginManifestFingerprint.Compute(Sample(old))}}", "Capabilities": ["events"] },
              "{{entryOnly}}": { "ManifestHash": "{{PluginManifestFingerprint.Compute(Sample(entryOnly))}}", "EntryHash": "e1", "Capabilities": ["events"] },
              "{{filesOnly}}": { "ManifestHash": "{{PluginManifestFingerprint.Compute(Sample(filesOnly))}}", "FilesHash": "f1", "Capabilities": ["events"] },
              {{FullGrantJson()}}
            } } }
            """);
        var store = new AppSettingsPluginGrantStore(settings, _ => { });
        var consent = PluginConsent.FromGrants(store);

        // They parse (a missing hash reads as empty rather than failing the whole file)...
        Assert.Equal(("", ""), (store.Get(old)!.EntryHash, store.Get(old)!.FilesHash));
        Assert.Equal(("e1", ""), (store.Get(entryOnly)!.EntryHash, store.Get(entryOnly)!.FilesHash));
        Assert.Equal(("", "f1"), (store.Get(filesOnly)!.EntryHash, store.Get(filesOnly)!.FilesHash));
        // ...and deny, however well the manifest and the hashes that are present match what is on disk.
        Assert.Null(consent(Request(Sample(old))));
        Assert.Null(consent(Request(Sample(entryOnly))));
        Assert.Null(consent(Request(Sample(filesOnly))));
        // The positive control: the same document's complete grant works, so the denials above are about the missing hashes.
        Assert.Equal(["events"], consent(Request(Sample()))!.ToArray());
    }

    [Fact]
    public void Peek_AppliesTheSameNormalisation_WithoutWritingAnything()
    {
        var path = Path();
        File.WriteAllText(path, """{ "Plugins": { "Grants": null, "DisabledIds": null } }""");
        var before = File.ReadAllText(path);

        var settings = new SettingsStore(path).Peek();

        Assert.NotNull(settings.Plugins.Grants);
        Assert.Empty(settings.Plugins.Grants);
        Assert.Empty(settings.Plugins.DisabledIds);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void AGrantsSectionOfTheWrongShape_IsSetAsideAsCorrupt_AndTheCommanderStartsWithNoGrants()
    {
        // An array where the object belongs cannot be read at all: the file is kept for recovery and
        // the session runs on defaults, which is deny. It must not throw into startup.
        var settings = Load("""{ "Plugins": { "Grants": [] } }""");

        Assert.Empty(settings.Plugins.Grants);
        Assert.Single(Directory.GetFiles(_dir.Path, "settings.json.corrupt-*"));
    }
}
