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

    private PluginService Service(Func<PluginManifest, IReadOnlyCollection<string>?>? consent = null, bool simulated = false)
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
        var manifest = PluginManifestParser.ParseFile(Path.Combine(dir, PluginManifestParser.FileName)).Manifest!;
        service.Grants.Grant(manifest, [PluginCapabilities.Events]);
        var asked = new List<string>();
        var (bus, state) = Engine();
        // Same store, but observe what the host asks consent for.
        var observing = Service(m =>
        {
            asked.Add(m.Id + "@" + m.Version);
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
    public void Grants_AreSavedThroughTheUiThread_NotOnTheCallersThread()
    {
        var service = Service();

        service.Grants.Set("com.test.app.save", new PluginGrant("hash", [PluginCapabilities.Events]));
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
