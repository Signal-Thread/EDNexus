using System.Collections.Concurrent;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// <see cref="PluginRuntime.Revoke"/>: withdrawing consent must stop a plugin that is already
/// running (consent is otherwise only evaluated at load), without persisting a disable. Real
/// compiled plugins, a real bridge and bus; no sleeps (waits are on events, bounded by <see cref="Wait"/>).
/// </summary>
public sealed class PluginRevokeTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string Jump = """{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump","StarSystem":"Sol","Body":"Sol"}""";

    private readonly TempDir _dir = new();
    private readonly JournalEventBus _bus = new();
    private readonly CommanderState _state = new();
    private readonly InMemoryPluginGrantStore _grants = new();
    private readonly InMemoryPluginEnablementStore _enablement = new();
    private readonly ConcurrentDictionary<string, BlockingCollection<string>> _logs = new();
    private readonly ConcurrentQueue<(string Id, PluginBridgeSession Session)> _sessions = new();
    private readonly List<IDisposable> _owned = [];

    public PluginRevokeTests() => _ = new StateTracker(_bus, _state);

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _dir.Dispose();
    }

    private PluginRuntime Runtime(Func<PluginConsentRequest, IReadOnlyCollection<string>?>? consent = null, IPluginGrantStore? grantStore = null)
    {
        var runtime = PluginRuntime.Create(_bus, _state, new PluginRuntimeOptions
        {
            PluginsRoot = _dir.Path,
            AppVersion = SemanticVersion.Parse("1.0.0"),
            Consent = consent ?? PluginConsent.FromGrants(_grants),
            EnablementStore = _enablement,
            GrantStore = grantStore ?? _grants,
            ContextFactory = (manifest, _, session) =>
            {
                _sessions.Enqueue((manifest.Id, session));
                return new PluginContext(manifest, session, new RecordingLog(LogOf(manifest.Id)));
            },
        })!;
        _owned.Add(runtime);
        return runtime;
    }

    private BlockingCollection<string> LogOf(string id) => _logs.GetOrAdd(id, _ => new BlockingCollection<string>());

    /// <summary>Installs a plugin that logs "got &lt;event&gt;" for every event it receives, and grants it exactly what it declares.</summary>
    private string Install(string id, string ns, params string[] capabilities)
    {
        if (capabilities.Length == 0) capabilities = [PluginCapabilities.Events];
        var dir = WriteFolder(_dir.Path, id,
            Manifest(id, ns + ".dll", ns + ".Plugin", capabilities: string.Join(",", capabilities.Select(c => $"\"{c}\""))),
            (ns + ".dll", Compile(ns, PluginSource(ns, initBody: "context.Events.SubscribeAny(e => context.Log.Info(\"got \" + e.Event));"))));
        _grants.Grant(dir, capabilities);
        return dir;
    }

    private void Publish()
    {
        Assert.True(JournalEntry.TryParse(Jump, historical: false, out var entry));
        _bus.Publish(entry);
    }

    private void AwaitGot(string id)
    {
        while (true)
        {
            Assert.True(LogOf(id).TryTake(out var line, Wait), $"{id} never logged an event");
            if (line == "got FSDJump") return;
        }
    }

    private int GotCount(string id) => LogOf(id).Count(l => l == "got FSDJump");

    private PluginBridgeSession SessionOf(string id) => _sessions.Last(s => s.Id == id).Session;

    [Fact]
    public void Revoke_StopsARunningPlugin_AndLeavesOthersAlone()
    {
        const string revoked = "com.test.revoke.target";
        const string other = "com.test.revoke.bystander";
        Install(revoked, "RevokeTarget", PluginCapabilities.Events, PluginCapabilities.State);
        Install(other, "RevokeBystander");
        var runtime = Runtime();
        runtime.Start();
        Assert.Equal(PluginState.Enabled, runtime.Supervisor.GetStatus(revoked)!.State);
        Publish();
        AwaitGot(revoked);
        AwaitGot(other);
        var session = SessionOf(revoked);

        var result = runtime.Revoke(revoked);

        Assert.True(result.GrantRemoved);
        Assert.False(result.StillRunning);
        Assert.Null(_grants.Get(revoked));
        Assert.NotNull(_grants.Get(other));
        var status = runtime.Supervisor.GetStatus(revoked)!;
        Assert.Equal(PluginState.Failed, status.State);
        Assert.Contains(nameof(PluginLoadStatus.Denied), status.LastError);
        Assert.DoesNotContain(runtime.Host.Loaded, p => p.Id == revoked);
        Assert.Contains(runtime.Host.Loaded, p => p.Id == other);
        // Revoke is not disable: nothing was persisted.
        Assert.False(_enablement.IsDisabled(revoked));

        // The revoked plugin's session is gone: its dispatch thread has exited and it hears nothing more.
        Assert.True(session.WaitForExit(Wait));
        Publish();
        AwaitGot(other);
        Assert.Equal(0, GotCount(revoked));
        Assert.Equal(PluginState.Enabled, runtime.Supervisor.GetStatus(other)!.State);
    }

    [Fact]
    public void Revoke_IsNotADisable_ARegrantLetsThePluginRunAgain()
    {
        const string id = "com.test.revoke.regrant";
        var dir = Install(id, "RevokeRegrant");
        var runtime = Runtime();
        runtime.Start();

        runtime.Revoke(id);
        Assert.Equal(PluginState.Failed, runtime.Supervisor.GetStatus(id)!.State);
        Assert.Empty(runtime.Host.Loaded);

        _grants.Grant(dir, [PluginCapabilities.Events]);
        var reloaded = runtime.Supervisor.Reload(id);   // no Enable needed: it was never disabled

        Assert.Equal(PluginLoadStatus.Loaded, reloaded!.Status);
        Publish();
        AwaitGot(id);
    }

    [Fact]
    public void Revoke_UnknownId_IsANoOp()
    {
        const string known = "com.test.revoke.known";
        Install(known, "RevokeKnown");
        var runtime = Runtime();
        runtime.Start();

        var result = runtime.Revoke("com.test.revoke.nobody");

        Assert.Equal(default, result);
        Assert.Equal(default, runtime.Revoke(""));
        Assert.Equal(default, runtime.Revoke(null!));
        Assert.Null(runtime.Supervisor.GetStatus("com.test.revoke.nobody"));
        Assert.Equal(PluginState.Enabled, runtime.Supervisor.GetStatus(known)!.State);
        Assert.NotNull(_grants.Get(known));
    }

    [Fact]
    public void Revoke_IsIdempotent()
    {
        const string id = "com.test.revoke.twice";
        Install(id, "RevokeTwice");
        var runtime = Runtime();
        runtime.Start();

        var first = runtime.Revoke(id);
        var second = runtime.Revoke(id);

        Assert.True(first.GrantRemoved);
        Assert.False(first.StillRunning);
        Assert.False(second.GrantRemoved);
        Assert.False(second.StillRunning);
        Assert.Equal(PluginState.Failed, runtime.Supervisor.GetStatus(id)!.State);
        Assert.Empty(runtime.Host.Loaded);
    }

    [Fact]
    public void Revoke_FromManyThreadsAtOnce_NeverThrows_AndRemovesTheGrantOnce()
    {
        const string id = "com.test.revoke.racing";
        Install(id, "RevokeRacing");
        var runtime = Runtime();
        runtime.Start();
        var results = new PluginRevocation[8];

        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = results.Length }, i => results[i] = runtime.Revoke(id));

        Assert.Equal(1, results.Count(r => r.GrantRemoved));
        Assert.All(results, r => Assert.False(r.StillRunning));
        Assert.Equal(PluginState.Failed, runtime.Supervisor.GetStatus(id)!.State);
        Assert.Empty(runtime.Host.Loaded);
    }

    [Fact]
    public void Revoke_OfADisabledPlugin_RemovesTheGrant_AndKeepsItDisabled()
    {
        const string id = "com.test.revoke.disabled";
        Install(id, "RevokeDisabled");
        var runtime = Runtime();
        runtime.Start();
        runtime.Supervisor.Disable(id);

        var result = runtime.Revoke(id);

        Assert.True(result.GrantRemoved);
        Assert.False(result.StillRunning);
        Assert.Equal(PluginState.Disabled, runtime.Supervisor.GetStatus(id)!.State);
        Assert.True(_enablement.IsDisabled(id));
    }

    [Fact]
    public void Revoke_WhenThePolicyIgnoresTheGrantStore_SaysThePluginIsStillRunning()
    {
        // The documented limit: Revoke re-runs the consent policy, so a policy that does not read the
        // grant store allows the plugin again. The result must not pretend otherwise.
        const string id = "com.test.revoke.policy";
        Install(id, "RevokePolicy");
        var runtime = Runtime(Consent.AllowAll);
        runtime.Start();

        var result = runtime.Revoke(id);

        Assert.True(result.GrantRemoved);
        Assert.True(result.StillRunning);
    }

    [Fact]
    public void Revoke_NeverThrows_WhenTheGrantStoreDoes_AndStillStopsThePlugin()
    {
        const string id = "com.test.revoke.badstore";
        Install(id, "RevokeBadStore");
        var failing = new ThrowOnRemoveStore();
        // The policy sees the grant as withdrawn even though the store refused to forget it.
        var runtime = Runtime(request => failing.RemoveAttempted ? null : PluginConsent.FromGrants(_grants)(request), failing);
        runtime.Start();
        Assert.Equal(PluginState.Enabled, runtime.Supervisor.GetStatus(id)!.State);

        var result = runtime.Revoke(id);

        Assert.False(result.GrantRemoved);
        Assert.False(result.StillRunning);
        Assert.Equal(PluginState.Failed, runtime.Supervisor.GetStatus(id)!.State);
    }

    [Fact]
    public void Revoke_AfterTheRuntimeIsDisposed_DoesNotThrow()
    {
        const string id = "com.test.revoke.disposed";
        Install(id, "RevokeDisposed");
        var runtime = Runtime();
        runtime.Start();
        runtime.Dispose();

        var result = runtime.Revoke(id);

        Assert.True(result.GrantRemoved);
        Assert.False(result.StillRunning);
    }

    private sealed class ThrowOnRemoveStore : IPluginGrantStore
    {
        public volatile bool RemoveAttempted;
        public PluginGrant? Get(string pluginId) => null;
        public void Set(string pluginId, PluginGrant grant) { }
        public bool Remove(string pluginId)
        {
            RemoveAttempted = true;
            throw new IOException("disk full");
        }
    }

    private sealed class RecordingLog(BlockingCollection<string> lines) : IPluginLog
    {
        public void Debug(string message) => lines.Add(message);
        public void Info(string message) => lines.Add(message);
        public void Warn(string message) => lines.Add(message);
        public void Error(string message, Exception? exception = null) => lines.Add(message);
    }
}
