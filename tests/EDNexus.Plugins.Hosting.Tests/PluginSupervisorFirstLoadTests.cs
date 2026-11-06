using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting.Bridge;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// <see cref="PluginSupervisor.RecordAttach"/> during <see cref="PluginSupervisor.Start"/>: the plugin
/// has no status yet (that is recorded from the load result), so the first attach has to create the
/// bookkeeping itself, and only while a start is under way. The supervisor is driven directly, with a
/// host whose context factory reports the way <see cref="PluginRuntime"/> does: attach first, then
/// whatever the plugin's handlers fault with while it loads.
/// </summary>
public sealed class PluginSupervisorFirstLoadTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TempDir _dir = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _dir.Dispose();
    }

    /// <summary>
    /// A supervisor over a host holding one standard plugin. <paramref name="duringLoad"/> runs inside
    /// the context factory, i.e. while <see cref="PluginSupervisor.Start"/> is loading the plugin.
    /// </summary>
    private PluginSupervisor Build(string id, string ns, Action<PluginSupervisor> duringLoad, PluginFaultPolicy? policy = null)
    {
        WriteStandard(_dir.Path, id, ns);
        PluginSupervisor? supervisor = null;
        var contexts = new RecordingContexts();
        var host = new PluginHost(_dir.Path, SemanticVersion.Parse("1.0.0"), Consent.AllowAll, (manifest, granted) =>
        {
            duringLoad(supervisor!);
            return contexts.Factory(manifest, granted);
        });
        _owned.Add(host);
        supervisor = new PluginSupervisor(host, new InMemoryPluginEnablementStore(), policy);
        _owned.Add(supervisor);
        return supervisor;
    }

    private static void Fault(PluginSupervisor supervisor, string id, string message, long session)
        => supervisor.ReportHandlerError(new PluginHandlerError(id, "FSDJump", "System.InvalidOperationException", message, session));

    [Fact]
    public void ReportsDuringTheFirstLoad_AreCountedOnlyForTheSessionThatAttached()
    {
        const string id = "com.test.firstload.sessions";
        var supervisor = Build(id, "FirstLoadSessions", s =>
        {
            s.RecordAttach(id, 7);                  // the plugin's bridge session, before it can subscribe
            Fault(s, id, "from another session", 6);   // a stamped report from any other session is not this load's
            Fault(s, id, "from this session", 7);
        });

        supervisor.Start();

        var status = supervisor.GetStatus(id)!;
        Assert.Equal(PluginState.Enabled, status.State);
        Assert.Equal(1, status.TotalFaults);
        Assert.Contains("from this session", status.LastError);
        Assert.DoesNotContain("another session", status.LastError);
    }

    [Fact]
    public void AnUnstampedReport_DuringTheFirstLoad_IsStillCounted()
    {
        // Control for the test above: a report with no session stamp (ReportFault) is charged to the load.
        const string id = "com.test.firstload.unstamped";
        var supervisor = Build(id, "FirstLoadUnstamped", s =>
        {
            s.RecordAttach(id, 7);
            s.ReportFault(id, "unstamped");
        });

        supervisor.Start();

        Assert.Equal(1, supervisor.GetStatus(id)!.TotalFaults);
    }

    [Fact]
    public async Task EnoughFaultsDuringTheFirstLoad_QuarantineThePluginOnceItIsLoaded()
    {
        const string id = "com.test.firstload.quarantine";
        var supervisor = Build(id, "FirstLoadQuarantine", s =>
        {
            s.RecordAttach(id, 3);
            for (var i = 0; i < 3; i++) Fault(s, id, "while loading", 3);
        }, new PluginFaultPolicy { Threshold = 2, Window = TimeSpan.FromMinutes(5) });
        var quarantined = new TaskCompletionSource<PluginStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.StatusChanged += s =>
        {
            if (s.State == PluginState.Quarantined) quarantined.TrySetResult(s);
        };

        supervisor.Start();

        // Never quarantined mid-load; the verdict is taken once the load result is recorded.
        var status = await quarantined.Task.WaitAsync(Wait);
        Assert.Equal(id, status.Id);
        Assert.Contains("quarantined", status.LastError);
        Assert.Equal(3, status.TotalFaults);
    }

    [Fact]
    public void RecordAttach_OutsideStart_ForAnUnknownPlugin_CreatesNothing_AndLaterReportsAreIgnored()
    {
        const string id = "com.test.firstload.outside";
        var supervisor = Build(id, "FirstLoadOutside", _ => { });

        supervisor.RecordAttach(id, 5);
        Fault(supervisor, id, "late", 5);
        supervisor.ReportFault(id, "late too");

        Assert.Null(supervisor.GetStatus(id));
        Assert.Empty(supervisor.Statuses);
        // And the plugin is still loadable afterwards, from a clean slate.
        supervisor.Start();
        Assert.Equal(0, supervisor.GetStatus(id)!.TotalFaults);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecordAttach_WithANonPositiveSessionNumber_IsIgnoredEvenDuringStart(long session)
    {
        const string id = "com.test.firstload.nonpositive";
        var supervisor = Build(id, "FirstLoadNonPositive", s =>
        {
            s.RecordAttach(id, session);   // not a real session number: nothing is tracked...
            Fault(s, id, "stamped 5", 5);  // ...so a stamped report is charged to the load, as with an untracked supervisor
        });

        supervisor.Start();

        Assert.Equal(1, supervisor.GetStatus(id)!.TotalFaults);
    }
}
