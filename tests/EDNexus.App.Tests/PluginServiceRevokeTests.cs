using EDNexus.App.Services.Plugins;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;

namespace EDNexus.App.Tests;

/// <summary>
/// <see cref="PluginService.Revoke"/>, the entry point the future consent UI uses: removing the grant
/// from <see cref="PluginService.Grants"/> alone leaves a loaded plugin running, so the service must
/// also stop it. Real compiled plugins through the real runtime; the persisted grant is the app's
/// <see cref="AppSettings"/>-backed store.
/// </summary>
public sealed class PluginServiceRevokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-app-revoke-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();
    private readonly List<Action> _uiQueue = [];
    private readonly List<PluginService> _services = [];

    public PluginServiceRevokeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PluginService Service()
    {
        var service = new PluginService(
            _settings,
            _ => { },
            postToUi: _uiQueue.Add,
            isSimulated: () => false,
            appVersion: SemanticVersion.Parse("1.0.0"),
            pluginsRoot: _root);
        _services.Add(service);
        return service;
    }

    private string InstallAndGrant(PluginService service, string id, string ns)
    {
        var dir = RoslynPluginKit.Install(_root, id, ns, RoslynPluginKit.PluginSource(ns, ""));
        service.Grants.Grant(dir, [PluginCapabilities.Events]);
        return dir;
    }

    [Fact]
    public async Task Revoke_StopsALoadedPlugin_RemovesItsGrant_AndLeavesItEnabledInSettings()
    {
        const string revoked = "com.test.appr.target";
        const string other = "com.test.appr.other";
        var service = Service();
        InstallAndGrant(service, revoked, "AppRevokeTarget");
        InstallAndGrant(service, other, "AppRevokeOther");
        service.Attach(new JournalEventBus(), new CommanderState());
        await service.StartAsync();
        var runtime = service.Runtime!;
        Assert.Equal(2, runtime.Host.Loaded.Count);
        var plugin = runtime.Host.Loaded.Single(p => p.Id == revoked);
        Assert.True(plugin.IsLoaded);

        var result = await Task.Run(() => service.Revoke(revoked));

        Assert.True(result.GrantRemoved);
        Assert.False(result.StillRunning);
        Assert.False(plugin.IsLoaded);
        Assert.Null(plugin.Instance);
        Assert.Null(service.Grants.Get(revoked));
        Assert.NotNull(service.Grants.Get(other));
        Assert.Single(runtime.Host.Loaded);
        Assert.Equal(PluginState.Failed, runtime.Supervisor.GetStatus(revoked)!.State);
        Assert.Contains(nameof(PluginLoadStatus.Denied), runtime.Supervisor.GetStatus(revoked)!.LastError);
        Assert.Equal(PluginState.Enabled, runtime.Supervisor.GetStatus(other)!.State);
        Assert.False(service.Enablement.IsDisabled(revoked));   // revoke is not disable
    }

    [Fact]
    public async Task Revoke_IsIdempotent_AndAnUnknownIdIsANoOp()
    {
        const string id = "com.test.appr.twice";
        var service = Service();
        InstallAndGrant(service, id, "AppRevokeTwice");
        service.Attach(new JournalEventBus(), new CommanderState());
        await service.StartAsync();

        Assert.True(service.Revoke(id).GrantRemoved);
        Assert.Equal(default, service.Revoke(id));
        Assert.Equal(default, service.Revoke("com.test.appr.nobody"));
        Assert.Equal(default, service.Revoke(""));
        Assert.Equal(default, service.Revoke(null!));
        Assert.Empty(service.Runtime!.Host.Loaded);
    }

    [Fact]
    public void Revoke_WithNoRuntime_StillRemovesThePersistedGrant()
    {
        var service = Service();
        var dir = RoslynPluginKit.Install(_root, "com.test.appr.detached", "AppRevokeDetached", RoslynPluginKit.PluginSource("AppRevokeDetached", ""));
        service.Grants.Grant(dir, [PluginCapabilities.Events]);
        Assert.Null(service.Runtime);

        var result = service.Revoke("com.test.appr.detached");

        Assert.True(result.GrantRemoved);
        Assert.False(result.StillRunning);
        Assert.Null(service.Grants.Get("com.test.appr.detached"));
    }

    [Fact]
    public async Task Revoke_AfterTheServiceIsDetached_DoesNotThrow()
    {
        const string id = "com.test.appr.late";
        var service = Service();
        InstallAndGrant(service, id, "AppRevokeLate");
        service.Attach(new JournalEventBus(), new CommanderState());
        await service.StartAsync();
        service.Detach();

        var result = service.Revoke(id);

        Assert.True(result.GrantRemoved);
        Assert.False(result.StillRunning);
    }
}
