using EDNexus.App.Services.Plugins;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Plugins.Hosting;

namespace EDNexus.App.Tests;

/// <summary>
/// <c>EDNEXUS_PLUGINS_DIR</c> redirects where plugin code is loaded from, so the app honours it only
/// where developer tools exist (<c>FeatureFlags.DeveloperTools</c>, AGENTS.md convention 7). The CLI
/// harness, which uses <see cref="PluginPaths.Resolve()"/> directly, keeps honouring it.
/// The flag itself is process-wide and fixed at startup, so these tests drive both states through the
/// service's injectable parameters; that the real flag is what the app passes is by inspection
/// (the constructor's default), not by test.
/// </summary>
public sealed class PluginServiceRootTests : IDisposable
{
    private readonly string _override = Path.Combine(Path.GetTempPath(), "ednexus-app-root-override-" + Guid.NewGuid().ToString("N"));
    private readonly List<PluginService> _services = [];

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
    }

    private Func<string, string?> Env => name => name == PluginPaths.OverrideEnvVar ? _override : null;

    private PluginService Service(bool developerTools)
    {
        var service = new PluginService(new AppSettings(), _ => { }, postToUi: _ => { }, isSimulated: () => false,
            appVersion: SemanticVersion.Parse("1.0.0"), developerTools: developerTools, getEnvironmentVariable: Env);
        _services.Add(service);
        return service;
    }

    [Fact]
    public void WithoutDeveloperTools_TheOverrideIsIgnored()
    {
        Assert.Equal(PluginPaths.DefaultRoot(), PluginService.ResolvePluginsRoot(developerTools: false, Env));
    }

    [Fact]
    public void WithDeveloperTools_TheOverrideIsHonoured()
    {
        Assert.Equal(Path.GetFullPath(_override), PluginService.ResolvePluginsRoot(developerTools: true, Env));
        Assert.Equal(PluginPaths.DefaultRoot(), PluginService.ResolvePluginsRoot(developerTools: true, _ => null));
    }

    [Fact]
    public void TheRuntimeOfAShippedBuild_UsesTheDefaultFolder_EvenWithTheVariableSet()
    {
        var service = Service(developerTools: false);

        Assert.True(service.Attach(new JournalEventBus(), new CommanderState()));

        Assert.Equal(PluginPaths.DefaultRoot(), service.Runtime!.PluginsRoot);
        Assert.NotEqual(Path.GetFullPath(_override), service.Runtime.PluginsRoot);
    }

    [Fact]
    public void TheRuntimeOfADeveloperBuild_UsesTheOverride()
    {
        var service = Service(developerTools: true);

        Assert.True(service.Attach(new JournalEventBus(), new CommanderState()));

        Assert.Equal(Path.GetFullPath(_override), service.Runtime!.PluginsRoot);
    }

    [Fact]
    public void AnExplicitRoot_StillWins_InBothStates()
    {
        var explicitRoot = Path.Combine(Path.GetTempPath(), "ednexus-app-root-explicit-" + Guid.NewGuid().ToString("N"));
        foreach (var developerTools in new[] { true, false })
        {
            var service = new PluginService(new AppSettings(), _ => { }, postToUi: _ => { }, isSimulated: () => false,
                appVersion: SemanticVersion.Parse("1.0.0"), pluginsRoot: explicitRoot, developerTools: developerTools, getEnvironmentVariable: Env);
            _services.Add(service);

            Assert.True(service.Attach(new JournalEventBus(), new CommanderState()));
            Assert.Equal(Path.GetFullPath(explicitRoot), service.Runtime!.PluginsRoot);
        }
    }
}
