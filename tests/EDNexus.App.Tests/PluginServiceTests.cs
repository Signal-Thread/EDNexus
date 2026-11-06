using EDNexus.App.Services.Plugins;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;

namespace EDNexus.App.Tests;

/// <summary>
/// The app's plugin wiring: one runtime per engine, nothing runs without a persisted grant, the
/// choices are saved on the UI thread, and a plugin problem never reaches the caller.
/// </summary>
public sealed class PluginServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-app-plugin-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();
    private readonly List<Action> _uiQueue = [];
    private readonly List<Exception> _errors = [];
    private readonly List<PluginService> _services = [];
    private int _saves;

    public PluginServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PluginService Service(Func<PluginConsentRequest, IReadOnlyCollection<string>?>? consent = null, bool simulated = false)
    {
        var service = new PluginService(
            _settings,
            _ => _saves++,
            postToUi: action => _uiQueue.Add(action),
            isSimulated: () => simulated,
            onError: _errors.Add,
            appVersion: SemanticVersion.Parse("1.0.0"),
            pluginsRoot: _root,
            consent: consent);
        _services.Add(service);
        return service;
    }

    private static (JournalEventBus Bus, CommanderState State) Engine() => (new JournalEventBus(), new CommanderState());

    /// <summary>A folder with a valid manifest and a placeholder DLL: enough to reach the consent check, never loaded.</summary>
    private string InstallUnloadable(string id, string version = "1.0.0")
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginManifestParser.FileName), $$"""
            {
              "id": "{{id}}",
              "name": "Test {{id}}",
              "version": "{{version}}",
              "sdkVersion": "{{PluginSdk.CurrentVersionString}}",
              "entryAssembly": "Plugin.dll",
              "entryType": "Plugin.Entry",
              "capabilities": ["events"]
            }
            """);
        File.WriteAllBytes(Path.Combine(dir, "Plugin.dll"), [0x4D, 0x5A, 0x90, 0x00]);
        return dir;
    }

    [Fact]
    public async Task ByDefault_NoGrantMeansNoPluginRuns()
    {
        InstallUnloadable("com.test.app.default");
        var service = Service();
        var (bus, state) = Engine();

        Assert.True(service.Attach(bus, state));
        await service.StartAsync();

        var status = service.Runtime!.Supervisor.GetStatus("com.test.app.default")!;
        Assert.Equal(PluginState.Failed, status.State);
        Assert.Contains(nameof(PluginLoadStatus.Denied), status.LastError);
        Assert.Empty(service.Runtime.Host.Loaded);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task AGrantForTheseContents_IsAppliedToTheConsentCheck_AndAnEditedManifestIsDeniedAgain()
    {
        const string id = "com.test.app.granted";
        var dir = InstallUnloadable(id);
        var service = Service();
        service.Grants.Grant(dir, [PluginCapabilities.Events]);
        var asked = new List<string>();
        var (bus, state) = Engine();
        // Same store, but observe what the host asks consent for.
        var observing = Service(m =>
        {
            asked.Add(m.Manifest.Id + "@" + m.Manifest.Version);
            return PluginConsent.FromGrants(service.Grants)(m);
        });

        observing.Attach(bus, state);
        await observing.StartAsync();
        Assert.Equal(id + "@1.0.0", Assert.Single(asked));
        // Past consent the placeholder DLL is not a real assembly, so loading it fails: it was allowed to try.
        Assert.DoesNotContain(nameof(PluginLoadStatus.Denied), observing.Runtime!.Supervisor.GetStatus(id)!.LastError);

        InstallUnloadable(id, version: "1.0.1");
        var reloaded = observing.Runtime.Supervisor.Reload(id)!;
        Assert.Equal(PluginLoadStatus.Denied, reloaded.Status);
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public async Task AGrant_DoesNotSurviveReplacedCode_UnderAnUnchangedManifest()
    {
        const string id = "com.test.app.replaced";
        var dir = InstallUnloadable(id);
        var service = Service();
        service.Grants.Grant(dir, [PluginCapabilities.Events]);
        var (bus, state) = Engine();
        service.Attach(bus, state);
        await service.StartAsync();
        // Past consent the placeholder DLL is not a real assembly, so loading fails: it was allowed to try.
        Assert.DoesNotContain(nameof(PluginLoadStatus.Denied), service.Runtime!.Supervisor.GetStatus(id)!.LastError);

        File.WriteAllBytes(Path.Combine(dir, "Plugin.dll"), [0x4D, 0x5A, 0x90, 0x00, 0xFF]);   // other code, same plugin.json

        Assert.Equal(PluginLoadStatus.Denied, service.Runtime.Supervisor.Reload(id)!.Status);
    }

    // ---- pruning saved choices of uninstalled plugins -----------------------------------------------

    private void SeedChoices(PluginService service, params string[] ids)
    {
        foreach (var id in ids)
        {
            service.Grants.Set(id, new PluginGrant("m", "e", "f", [PluginCapabilities.Events]));
            service.Enablement.SetDisabled(id, true);
        }
    }

    private bool HasChoices(string id) => _settings.Plugins.Grants.ContainsKey(id) || _settings.Plugins.DisabledIds.Contains(id);

    [Fact]
    public async Task StartAsync_ForgetsTheChoicesOfPluginsWithNoFolder_AndKeepsTheOthers()
    {
        InstallUnloadable("com.test.app.prune.kept");
        var service = Service();
        SeedChoices(service, "com.test.app.prune.kept", "com.test.app.prune.gone");
        var (bus, state) = Engine();
        service.Attach(bus, state);

        await service.StartAsync();

        Assert.False(HasChoices("com.test.app.prune.gone"));
        Assert.True(_settings.Plugins.Grants.ContainsKey("com.test.app.prune.kept"));
        Assert.Contains("com.test.app.prune.kept", _settings.Plugins.DisabledIds);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task Pruning_RunsAfterInstallRecovery_SoAPluginWhoseOnlyCopyIsABackupKeepsItsChoices()
    {
        const string id = "com.test.app.prune.backup";
        // A replace that was interrupted: the folder is missing and the previous version sits in a backup.
        var live = InstallUnloadable(id);
        Directory.Move(live, Path.Combine(_root, $".replaced-{id}.{Guid.NewGuid():N}"));
        var service = Service();
        SeedChoices(service, id);
        var (bus, state) = Engine();
        service.Attach(bus, state);

        await service.StartAsync();

        Assert.True(Directory.Exists(Path.Combine(_root, id)));   // recovery put it back
        Assert.True(HasChoices(id));
    }

    [Fact]
    public async Task Pruning_DoesNothing_WhenThePluginsRootIsMissing()
    {
        var service = Service();
        SeedChoices(service, "com.test.app.prune.a", "com.test.app.prune.b");
        Directory.Delete(_root, recursive: true);   // e.g. an unplugged drive
        var (bus, state) = Engine();
        service.Attach(bus, state);

        await service.StartAsync();

        Assert.True(HasChoices("com.test.app.prune.a"));
        Assert.True(HasChoices("com.test.app.prune.b"));
    }

    [Fact]
    public async Task Pruning_DoesNothing_WhenThePluginsRootIsEmpty_BecauseThatLooksLikeAnUnmountedShare()
    {
        var service = Service();
        SeedChoices(service, "com.test.app.prune.a");
        Directory.CreateDirectory(Path.Combine(_root, ".staging-leftover"));   // hidden work folders are not plugins
        var (bus, state) = Engine();
        service.Attach(bus, state);

        await service.StartAsync();

        Assert.True(HasChoices("com.test.app.prune.a"));
    }

    [Fact]
    public void Pruning_DoesNothing_WhenTheRootCannotBeEnumerated_AndNeverThrows()
    {
        InstallUnloadable("com.test.app.prune.other");
        var service = Service();
        SeedChoices(service, "com.test.app.prune.a");
        var denied = new Func<string, IEnumerable<string>>(_ => throw new UnauthorizedAccessException("access denied"));
        var io = new Func<string, IEnumerable<string>>(_ => throw new IOException("network share went away"));
        IEnumerable<string> HalfWay(string path)
        {
            yield return Path.Combine(path, "com.test.app.prune.other");
            throw new IOException("dropped mid-listing");
        }

        Assert.Empty(PluginChoicePruner.Prune(_settings, _root, service.Grants, service.Enablement, denied));
        Assert.Empty(PluginChoicePruner.Prune(_settings, _root, service.Grants, service.Enablement, io));
        Assert.Empty(PluginChoicePruner.Prune(_settings, _root, service.Grants, service.Enablement, HalfWay));

        Assert.True(HasChoices("com.test.app.prune.a"));
        // And the positive control: the real enumerator does prune it.
        Assert.Equal(["com.test.app.prune.a"], PluginChoicePruner.Prune(_settings, _root, service.Grants, service.Enablement));
        Assert.False(HasChoices("com.test.app.prune.a"));
    }

    [Fact]
    public void Pruning_NeverTouchesAnIdFromSettingsAsAPath()
    {
        InstallUnloadable("com.test.app.prune.other");
        var service = Service();
        // A hand-edited settings.json can hold anything as an id; it is only ever compared to folder names.
        SeedChoices(service, "../escape", "a/b", "");

        var pruned = PluginChoicePruner.Prune(_settings, _root, service.Grants, service.Enablement);

        Assert.Equal(3, pruned.Count);
        Assert.Empty(_settings.Plugins.Grants);
    }

    [Fact]
    public void Grants_AreSavedThroughTheUiThread_NotOnTheCallersThread()
    {
        var service = Service();

        service.Grants.Set("com.test.app.save", new PluginGrant("hash", "entry", "files", [PluginCapabilities.Events]));
        service.Enablement.SetDisabled("com.test.app.save", true);

        Assert.Equal(0, _saves);   // posted, not run
        Assert.Equal(2, _uiQueue.Count);
        foreach (var action in _uiQueue) action();
        Assert.Equal(2, _saves);
        Assert.True(_settings.Plugins.Grants.ContainsKey("com.test.app.save"));
        Assert.Contains("com.test.app.save", _settings.Plugins.DisabledIds);
    }

    [Fact]
    public async Task EachEngineGetsItsOwnRuntime_AndTheOldOneIsReleased()
    {
        var service = Service();
        var (bus1, state1) = Engine();
        var (bus2, state2) = Engine();

        Assert.True(service.Attach(bus1, state1));
        var first = service.Runtime!;
        await service.StartAsync();

        // The app rebuilds its engine: detach, then attach to the new one.
        service.Detach();
        Assert.Null(service.Runtime);
        Assert.True(service.Attach(bus2, state2));

        Assert.NotSame(first, service.Runtime);
        Assert.Equal(first.PluginsRoot, service.Runtime!.PluginsRoot);
        Assert.Throws<ObjectDisposedException>(() => first.Supervisor.Start());
    }

    [Fact]
    public void Attach_WhenOneIsAlreadyAttached_ReplacesIt()
    {
        var service = Service();
        var (bus1, state1) = Engine();
        var (bus2, state2) = Engine();
        service.Attach(bus1, state1);
        var first = service.Runtime!;

        service.Attach(bus2, state2);

        Assert.NotSame(first, service.Runtime);
        Assert.Throws<ObjectDisposedException>(() => first.Supervisor.Start());
    }

    [Fact]
    public async Task StartAsync_WithNothingAttached_CompletesAndDetachIsIdempotent()
    {
        var service = Service();

        await service.StartAsync();
        service.Detach();
        service.Detach();
        service.Dispose();

        Assert.Null(service.Runtime);
        Assert.True(service.ShutdownForExit(Wait));
    }

    [Fact]
    public async Task AMissingPluginsFolder_IsNotAnError()
    {
        var service = Service();
        Directory.Delete(_root, recursive: true);
        var (bus, state) = Engine();

        Assert.True(service.Attach(bus, state));
        await service.StartAsync();

        Assert.Empty(service.Runtime!.Supervisor.Statuses);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task AFailureLoadingPlugins_IsContainedAndReported_NeverThrownToTheCaller()
    {
        InstallUnloadable("com.test.app.throwing");
        var service = Service(consent: _ => throw new InvalidOperationException("consent exploded"));
        var (bus, state) = Engine();
        service.Attach(bus, state);

        await service.StartAsync();   // would throw here if the failure escaped

        var status = service.Runtime!.Supervisor.GetStatus("com.test.app.throwing")!;
        Assert.Equal(PluginState.Failed, status.State);
        Assert.Contains("consent exploded", status.LastError);
    }

    [Fact]
    public void AFailureBuildingTheRuntime_ReturnsFalse_ReportsIt_AndLeavesTheAppRunning()
    {
        // A plugins root under a file cannot be scanned, and an invalid path cannot even be resolved.
        var service = new PluginService(
            _settings, _ => { }, postToUi: a => a(), isSimulated: () => false,
            onError: _errors.Add, appVersion: SemanticVersion.Parse("1.0.0"), pluginsRoot: "bad\0path");
        _services.Add(service);
        var (bus, state) = Engine();

        Assert.False(service.Attach(bus, state));

        Assert.Null(service.Runtime);
        Assert.Single(_errors);
    }

    [Fact]
    public async Task StatusChanges_AreForwarded()
    {
        InstallUnloadable("com.test.app.status");
        var service = Service();
        var changes = new System.Collections.Concurrent.ConcurrentQueue<PluginStatus>();
        service.StatusChanged += changes.Enqueue;
        var (bus, state) = Engine();
        service.Attach(bus, state);

        await service.StartAsync();

        Assert.Contains(changes, s => s.Id == "com.test.app.status" && s.State == PluginState.Failed);
    }
}
