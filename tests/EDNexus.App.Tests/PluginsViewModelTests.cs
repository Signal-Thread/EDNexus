using EDNexus.App.Services.Plugins;
using EDNexus.App.ViewModels;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;

namespace EDNexus.App.Tests;

/// <summary>
/// The plugin manager in Settings (#61): real compiled plugins through the real
/// <see cref="PluginService"/> and runtime, with settings saved to a real <see cref="SettingsStore"/>
/// file so "survives restart" means a fresh load of that file. The UI thread is the test thread
/// (<c>postToUi</c> runs inline).
/// </summary>
public sealed class PluginsViewModelTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ednexus-app-pluginvm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly SettingsStore _store;
    private readonly List<PluginService> _services = [];
    private readonly List<PluginsViewModel> _vms = [];
    private readonly List<string> _opened = [];

    public PluginsViewModelTests()
    {
        _root = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(_root);
        _store = new SettingsStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose()
    {
        foreach (var vm in _vms) vm.Dispose();
        foreach (var service in _services) service.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A service over the settings file as it is on disk now (a fresh launch), attached and started.</summary>
    private async Task<PluginService> Launch(bool attach = true)
    {
        var settings = _store.Load();
        var service = new PluginService(
            settings,
            s => _store.TrySave(s),
            postToUi: action => action(),
            isSimulated: () => false,
            appVersion: SemanticVersion.Parse("1.0.0"),
            pluginsRoot: _root);
        _services.Add(service);
        if (attach)
        {
            service.Attach(_bus, new CommanderState());
            await service.StartAsync();
        }
        return service;
    }

    private readonly JournalEventBus _bus = new();

    private async Task<PluginsViewModel> Manager(PluginService service)
    {
        var vm = new PluginsViewModel(service, postToUi: action => action(), openFolder: _opened.Add);
        _vms.Add(vm);
        await vm.RefreshAsync();
        return vm;
    }

    /// <summary>Installs a real plugin with a full manifest; <paramref name="initialize"/> is the body of <c>Initialize(IPluginContext context)</c>.</summary>
    private string Install(string id, string ns, string initialize = "", string[]? capabilities = null, string version = "1.2.3")
    {
        capabilities ??= [PluginCapabilities.Events];
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "Name of {{ns}}",
              "version": "{{version}}",
              "author": "Author {{ns}}",
              "description": "Does {{ns}} things.",
              "sdkVersion": "{{PluginSdk.CurrentVersionString}}",
              "entryAssembly": "{{ns}}.dll",
              "entryType": "{{ns}}.Plugin",
              "capabilities": [{{string.Join(",", capabilities.Select(c => $"\"{c}\""))}}]
            }
            """);
        File.WriteAllBytes(Path.Combine(dir, ns + ".dll"), RoslynPluginKit.Compile(ns, $$"""
            using System;
            using System.IO;
            using EDNexus.Plugins.Abstractions;
            namespace {{ns}};
            public sealed class Plugin : IEDNexusPlugin
            {
                public void Initialize(IPluginContext context) { {{initialize}} }
                public void Shutdown() { }
            }
            """));
        return dir;
    }

    /// <summary>Initialize body that appends whether <c>State</c> was usable to <paramref name="marker"/>.</summary>
    private static string ProbeState(string marker) => $$"""
        try { context.State.Snapshot(); File.AppendAllText(@"{{marker}}", "state-ok\n"); }
        catch (UnauthorizedAccessException) { File.AppendAllText(@"{{marker}}", "state-denied\n"); }
        """;

    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Wait, "timed out waiting for the condition");
            await Task.Delay(20);
        }
    }

    // ---- listing ----------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_ListsInstalledPlugins_WithTheirManifestDetails_AndNeedsApproval_WithoutRecordingAnyGrant()
    {
        Install("com.test.vm.listed", "VmListed", capabilities: [PluginCapabilities.Events, PluginCapabilities.Storage]);
        var service = await Launch();

        var vm = await Manager(service);

        var row = Assert.Single(vm.Plugins);
        Assert.Equal("com.test.vm.listed", row.Id);
        Assert.Equal("Name of VmListed", row.Name);
        Assert.Equal("1.2.3", row.Version);
        Assert.Equal("Author VmListed", row.Author);
        Assert.Equal("Does VmListed things.", row.Description);
        Assert.Equal(PluginRowState.NeedsApproval, row.State);
        Assert.True(row.CanApprove);
        Assert.False(row.CanRevoke);
        Assert.Equal([PluginCapabilities.Events, PluginCapabilities.Storage], row.Capabilities.Select(c => c.Name));
        Assert.Null(service.Grants.Get("com.test.vm.listed"));   // looking is not consenting
        Assert.Empty(service.Runtime!.Host.Loaded);
    }

    [Fact]
    public async Task Refresh_ShowsAFolderThatCannotBeLoaded_WithItsProblem_AndOffersNoApproval()
    {
        var bad = Path.Combine(_root, "com.test.vm.broken");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "plugin.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(_root, ".staging-leftover"));   // installer work folder: not a plugin
        var service = await Launch();

        var vm = await Manager(service);

        var row = Assert.Single(vm.Plugins);
        Assert.Equal("com.test.vm.broken", row.Id);
        Assert.Equal(PluginRowState.Invalid, row.State);
        Assert.False(string.IsNullOrEmpty(row.LastError));
        Assert.False(row.CanApprove);
        Assert.Empty(row.Capabilities);
    }

    [Fact]
    public async Task Refresh_WhenTheFilesChangedSinceApproval_AsksAgain()
    {
        var dir = Install("com.test.vm.changed", "VmChanged");
        var service = await Launch();
        var vm = await Manager(service);
        await vm.Plugins[0].ApproveCommand.ExecuteAsync(null);
        Assert.Equal(PluginRowState.Running, vm.Plugins[0].State);

        File.WriteAllText(Path.Combine(dir, "extra.txt"), "a file the commander never approved");
        await vm.RefreshAsync();

        // The approved copy is still the one in memory, so it is still running, but the commander is told.
        Assert.Equal(PluginRowState.Running, vm.Plugins[0].State);
        Assert.Contains("changed", vm.Plugins[0].StateText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Allow", vm.Plugins[0].ApproveLabel);

        await vm.Plugins[0].ReloadCommand.ExecuteAsync(null);

        Assert.Equal(PluginRowState.NeedsApproval, vm.Plugins[0].State);
        Assert.Contains("changed", vm.Plugins[0].StateText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.Runtime!.Host.Loaded);
    }

    // ---- consent ----------------------------------------------------------------------------

    [Fact]
    public async Task Approve_RecordsAGrantBoundToTheFiles_WithTheCheckedCapabilities_AndStartsThePluginLive()
    {
        var marker = Path.Combine(_dir, "approve.marker");
        Install("com.test.vm.approve", "VmApprove", ProbeState(marker), [PluginCapabilities.Events, PluginCapabilities.State]);
        var service = await Launch();
        var vm = await Manager(service);
        var row = vm.Plugins[0];
        Assert.All(row.Capabilities, c => Assert.True(c.IsGranted));   // offered as declared

        await row.ApproveCommand.ExecuteAsync(null);

        var grant = service.Grants.Get("com.test.vm.approve");
        Assert.NotNull(grant);
        Assert.Equal([PluginCapabilities.Events, PluginCapabilities.State], grant.Capabilities.Order(StringComparer.Ordinal));
        Assert.Contains(service.Runtime!.Host.Loaded, p => p.Id == "com.test.vm.approve");
        Assert.Equal("state-ok\n", File.ReadAllText(marker));
        Assert.Equal(PluginRowState.Running, vm.Plugins[0].State);
        Assert.True(vm.Plugins[0].CanRevoke);
    }

    [Fact]
    public async Task UncheckingACapability_AndApplying_NarrowsTheGrant_AndReloadsThePluginWithoutIt()
    {
        var marker = Path.Combine(_dir, "narrow.marker");
        Install("com.test.vm.narrow", "VmNarrow", ProbeState(marker), [PluginCapabilities.Events, PluginCapabilities.State]);
        var service = await Launch();
        var vm = await Manager(service);
        await vm.Plugins[0].ApproveCommand.ExecuteAsync(null);
        var first = service.Runtime!.Host.Loaded.Single(p => p.Id == "com.test.vm.narrow");

        vm.Plugins[0].Capabilities.Single(c => c.Name == PluginCapabilities.State).IsGranted = false;
        await vm.Plugins[0].ApproveCommand.ExecuteAsync(null);

        Assert.Equal([PluginCapabilities.Events], service.Grants.Get("com.test.vm.narrow")!.Capabilities);
        Assert.False(first.IsLoaded);   // the copy holding the wider grant is gone
        Assert.Equal("state-ok\nstate-denied\n", File.ReadAllText(marker));
        Assert.False(vm.Plugins[0].Capabilities.Single(c => c.Name == PluginCapabilities.State).IsGranted);
    }

    [Fact]
    public async Task Revoke_RemovesTheGrant_StopsThePlugin_AndAsksForApprovalAgain()
    {
        Install("com.test.vm.revoke", "VmRevoke");
        var service = await Launch();
        var vm = await Manager(service);
        await vm.Plugins[0].ApproveCommand.ExecuteAsync(null);
        var plugin = service.Runtime!.Host.Loaded.Single(p => p.Id == "com.test.vm.revoke");

        await vm.Plugins[0].RevokeCommand.ExecuteAsync(null);

        Assert.Null(service.Grants.Get("com.test.vm.revoke"));
        Assert.False(plugin.IsLoaded);
        Assert.Equal(PluginRowState.NeedsApproval, vm.Plugins[0].State);
        Assert.False(_store.Load().Plugins.Grants.ContainsKey("com.test.vm.revoke"));
    }

    [Fact]
    public async Task Network_IsShownWithAWarning_CannotBeUnchecked_AndIsPartOfTheGrant()
    {
        Install("com.test.vm.net", "VmNet", capabilities: [PluginCapabilities.Events, PluginCapabilities.Network]);
        var service = await Launch();
        var vm = await Manager(service);
        var row = vm.Plugins[0];

        var network = row.Capabilities.Single(c => c.Name == PluginCapabilities.Network);
        Assert.False(network.CanChange);
        Assert.True(network.IsGranted);
        Assert.NotNull(row.NetworkWarning);
        Assert.Contains("commander name", row.NetworkWarning, StringComparison.OrdinalIgnoreCase);

        row.Capabilities.Single(c => c.Name == PluginCapabilities.Events).IsGranted = false;
        await row.ApproveCommand.ExecuteAsync(null);

        Assert.Equal([PluginCapabilities.Network], service.Grants.Get("com.test.vm.net")!.Capabilities);
    }

    [Fact]
    public async Task APluginWithoutNetwork_HasNoNetworkWarning()
    {
        Install("com.test.vm.offline", "VmOffline");
        var vm = await Manager(await Launch());

        Assert.Null(vm.Plugins[0].NetworkWarning);
    }

    // ---- enable / disable -------------------------------------------------------------------

    [Fact]
    public async Task Disabling_StopsThePluginLive_AndSurvivesARestart_AndEnablingStartsItAgain()
    {
        Install("com.test.vm.toggle", "VmToggle");
        var service = await Launch();
        var vm = await Manager(service);
        await vm.Plugins[0].ApproveCommand.ExecuteAsync(null);
        Assert.True(vm.Plugins[0].IsEnabled);

        await vm.Plugins[0].ToggleEnabledCommand.ExecuteAsync(null);

        Assert.False(vm.Plugins[0].IsEnabled);
        Assert.Equal(PluginRowState.Disabled, vm.Plugins[0].State);
        Assert.Empty(service.Runtime!.Host.Loaded);

        // Restart: the disable flag and the grant were written to settings.json.
        service.Dispose();
        var relaunched = await Launch();
        Assert.Empty(relaunched.Runtime!.Host.Loaded);
        var vm2 = await Manager(relaunched);
        Assert.Equal(PluginRowState.Disabled, vm2.Plugins[0].State);

        await vm2.Plugins[0].ToggleEnabledCommand.ExecuteAsync(null);

        Assert.True(vm2.Plugins[0].IsEnabled);
        Assert.Equal(PluginRowState.Running, vm2.Plugins[0].State);
        Assert.Contains(relaunched.Runtime!.Host.Loaded, p => p.Id == "com.test.vm.toggle");
    }

    [Fact]
    public async Task AnApprovedPlugin_RunsAfterARestart()
    {
        Install("com.test.vm.persist", "VmPersist");
        var service = await Launch();
        var vm = await Manager(service);
        await vm.Plugins[0].ApproveCommand.ExecuteAsync(null);
        service.Dispose();

        var relaunched = await Launch();

        Assert.Contains(relaunched.Runtime!.Host.Loaded, p => p.Id == "com.test.vm.persist");
        Assert.Equal(PluginRowState.Running, (await Manager(relaunched)).Plugins[0].State);
    }

    [Fact]
    public async Task WithNoRuntime_ApprovingAndDisablingStillPersist()
    {
        Install("com.test.vm.detached", "VmDetached");
        var service = await Launch(attach: false);
        var vm = await Manager(service);

        await vm.Plugins[0].ApproveCommand.ExecuteAsync(null);
        await vm.Plugins[0].ToggleEnabledCommand.ExecuteAsync(null);

        var saved = _store.Load().Plugins;
        Assert.True(saved.Grants.ContainsKey("com.test.vm.detached"));
        Assert.Contains("com.test.vm.detached", saved.DisabledIds);
        Assert.Equal(PluginRowState.Disabled, vm.Plugins[0].State);
    }

    // ---- faults -----------------------------------------------------------------------------

    [Fact]
    public async Task AQuarantinedPlugin_IsMarkedWithItsError_AndCounted_AndReloadRetriesIt()
    {
        Install("com.test.vm.faulty", "VmFaulty",
            """context.Events.Subscribe("FSDJump", e => throw new InvalidOperationException("boom"));""");
        var service = await Launch();
        var vm = await Manager(service);
        await vm.Plugins[0].ApproveCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.ErrorCount);

        for (var i = 0; i < 15; i++)
        {
            Assert.True(JournalEntry.TryParse("""{"timestamp":"2026-11-09T10:00:00Z","event":"FSDJump","StarSystem":"Sol"}""", historical: false, out var entry));
            _bus.Publish(entry);
        }
        await Until(() => vm.Plugins[0].State == PluginRowState.Quarantined);   // pushed by StatusChanged, no refresh

        Assert.Contains("boom", vm.Plugins[0].LastError);
        Assert.Contains("quarantined", vm.Plugins[0].LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, vm.ErrorCount);

        await vm.Plugins[0].ReloadCommand.ExecuteAsync(null);

        Assert.Equal(PluginRowState.Running, vm.Plugins[0].State);
        Assert.Equal(0, vm.ErrorCount);
    }

    // ---- folder -----------------------------------------------------------------------------

    [Fact]
    public async Task OpenFolder_OpensThePluginsRootTheAppLoadsFrom_CreatingItIfNeeded()
    {
        Directory.Delete(_root, recursive: true);
        var service = await Launch();
        var vm = await Manager(service);

        vm.OpenFolderCommand.Execute(null);

        Assert.Equal([Path.GetFullPath(_root)], _opened);
        Assert.Equal(service.PluginsRoot, vm.PluginsFolder);
        Assert.True(Directory.Exists(_root));
        Assert.Empty(vm.Plugins);
    }

    [Fact]
    public async Task Rescan_PicksUpAPluginInstalledWhileTheWindowIsOpen()
    {
        var service = await Launch();
        var vm = await Manager(service);
        Assert.Empty(vm.Plugins);

        Install("com.test.vm.late", "VmLate");
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.Plugins[0].ApproveCommand.ExecuteAsync(null);

        Assert.Equal(PluginRowState.Running, vm.Plugins[0].State);
        Assert.Contains(service.Runtime!.Host.Loaded, p => p.Id == "com.test.vm.late");
    }
}
