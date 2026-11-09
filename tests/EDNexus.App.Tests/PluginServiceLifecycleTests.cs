using EDNexus.App.Services.Plugins;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EDNexus.App.Tests;

/// <summary>
/// Teardown and start/stop races in <see cref="PluginService"/>: overlapping detaches, a detach
/// that lands while a plugin is still initialising, and an exit that begins during an engine
/// rebuild. Plugins park on latches handed over through <see cref="AppDomain"/> data, so nothing
/// sleeps; every wait is on an event, with <see cref="Ceiling"/> only to fail a hang.
/// </summary>
public sealed class PluginServiceLifecycleTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-app-lifecycle-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();
    private readonly List<Exception> _errors = [];
    private readonly List<PluginService> _services = [];

    public PluginServiceLifecycleTests() => Directory.CreateDirectory(_root);

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
            postToUi: action => action(),
            isSimulated: () => false,
            onError: _errors.Add,
            appVersion: SemanticVersion.Parse("1.0.0"),
            pluginsRoot: _root,
            consent: request => request.Manifest.Capabilities);
        _services.Add(service);
        return service;
    }

    /// <summary>Two latches published under a fresh key: the plugin sets <c>Entered</c>, then waits for <c>Gate</c>.</summary>
    private sealed class Latches : IDisposable
    {
        public Latches()
        {
            Key = Guid.NewGuid().ToString("N");
            AppDomain.CurrentDomain.SetData("svc.entered." + Key, Entered);
            AppDomain.CurrentDomain.SetData("svc.gate." + Key, Gate);
        }

        public string Key { get; }
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Gate { get; } = new();
        public object? Seen => AppDomain.CurrentDomain.GetData("svc.seen." + Key);

        public void Dispose()
        {
            Gate.Set();   // never leave a plugin thread parked
        }
    }

    private static string Park(string key) => $$"""
        ((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData("svc.entered.{{key}}")!).Set();
        ((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData("svc.gate.{{key}}")!).Wait();
        """;

    /// <summary>Installs a plugin (namespace <paramref name="ns"/>) whose <c>Initialize</c> / <c>Shutdown</c> run the given bodies.</summary>
    private void Install(string id, string ns, string initBody = "", string shutdownBody = "")
    {
        var source = $$"""
            using EDNexus.Plugins.Abstractions;
            namespace {{ns}};
            public sealed class Plugin : IEDNexusPlugin
            {
                public void Initialize(IPluginContext context) { {{initBody}} }
                public void Shutdown() { {{shutdownBody}} }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p) is var name && (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(IEDNexusPlugin).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            ns,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));

        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginManifestParser.FileName), $$"""
            {
              "id": "{{id}}",
              "name": "Test {{id}}",
              "version": "1.0.0",
              "sdkVersion": "{{PluginSdk.CurrentVersionString}}",
              "entryAssembly": "{{ns}}.dll",
              "entryType": "{{ns}}.Plugin",
              "capabilities": ["events", "state"]
            }
            """);
        File.WriteAllBytes(Path.Combine(dir, ns + ".dll"), stream.ToArray());
    }

    private static (JournalEventBus Bus, CommanderState State) Engine()
    {
        var bus = new JournalEventBus();
        var state = new CommanderState();
        _ = new StateTracker(bus, state);
        return (bus, state);
    }

    // ---- 1: a second detach must not report "done" while the first is still unloading ---------------

    [Fact]
    public async Task ASecondDetach_WhileTheFirstIsStillUnloading_DoesNotReportItselfFinished()
    {
        using var latches = new Latches();
        Install("com.test.svc.overlap", "SvcOverlap", shutdownBody: Park(latches.Key));
        var service = Service();
        var (bus, state) = Engine();
        service.Attach(bus, state);
        await service.StartAsync();
        Assert.Single(service.Runtime!.Host.Loaded);

        var first = Task.Run(service.Detach);
        Assert.True(latches.Entered.Wait(Ceiling));   // the first detach is inside the plugin's Shutdown

        // The runtime is already detached, but its plugin is still shutting down: not "finished in time".
        Assert.False(service.ShutdownForExit(TimeSpan.FromMilliseconds(100)));
        Assert.False(first.IsCompleted);

        latches.Gate.Set();
        await first.WaitAsync(Ceiling);
        Assert.True(service.ShutdownForExit(Ceiling));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task ASecondDetach_Joins_AndReturnsOnceTheFirstHasFinished()
    {
        using var latches = new Latches();
        Install("com.test.svc.join", "SvcJoin", shutdownBody: Park(latches.Key));
        var service = Service();
        var (bus, state) = Engine();
        service.Attach(bus, state);
        await service.StartAsync();

        var first = Task.Run(service.Detach);
        Assert.True(latches.Entered.Wait(Ceiling));
        var second = Task.Run(() => service.Detach(Ceiling));
        // Joined to the first, which is parked: it cannot be done yet.
        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromMilliseconds(200)));

        latches.Gate.Set();

        Assert.True(await second.WaitAsync(Ceiling));
        await first.WaitAsync(Ceiling);
        Assert.Null(service.Runtime);
    }

    // ---- 3: a detach must wait for a plugin that is still inside Initialize ---------------------------

    [Fact]
    public async Task Detach_DuringAPluginsInitialize_KeepsTheBridgeAliveUntilTheLoadPassHasFinished()
    {
        using var latches = new Latches();
        Install("com.test.svc.midinit", "SvcMidInit",
            initBody: Park(latches.Key) + $" System.AppDomain.CurrentDomain.SetData(\"svc.seen.{latches.Key}\", context.State.StarSystem ?? \"(none)\");");
        var service = Service();
        var (bus, state) = Engine();
        Assert.True(service.Attach(bus, state));
        var runtime = service.Runtime!;
        var start = service.StartAsync();
        Assert.True(latches.Entered.Wait(Ceiling));   // the plugin is mid-Initialize: in no list the host keeps yet

        var detach = new Thread(service.Detach) { IsBackground = true };
        detach.Start();
        // The host has stopped loading and the detach is now parked waiting for the load pass.
        Assert.True(SpinWait.SpinUntil(
            () => HostIsDisposed(runtime) && (detach.ThreadState & ThreadState.WaitSleepJoin) != 0, Ceiling));

        // The engine moves on while the plugin is still initialising. The plugin's state view is fed
        // by the bridge: it can only see this if the bridge has not been released yet.
        Assert.True(JournalEntry.TryParse(
            """{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump","StarSystem":"Sol","Body":"Sol"}""", historical: false, out var jump));
        bus.Publish(jump);
        latches.Gate.Set();

        Assert.True(detach.Join(Ceiling));
        await start.WaitAsync(Ceiling);
        Assert.Equal("Sol", latches.Seen);
        Assert.Empty(runtime.Host.Loaded);   // and the plugin that finished initialising was unloaded, not left running
        Assert.Empty(_errors);
    }

    private static bool HostIsDisposed(PluginRuntime runtime)
    {
        try
        {
            runtime.Host.LoadPlugin("com.test.svc.probe");
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;   // a load pass is still running
        }
    }

    // ---- 2: once the exit began, an engine rebuild cannot bring plugins back ---------------------------

    [Fact]
    public async Task AfterShutdownForExit_AttachAndStartAsyncDoNothing()
    {
        Install("com.test.svc.afterexit", "SvcAfterExit");
        var service = Service();
        var (bus1, state1) = Engine();
        Assert.True(service.Attach(bus1, state1));
        Assert.True(service.ShutdownForExit(Ceiling));

        var (bus2, state2) = Engine();
        Assert.False(service.Attach(bus2, state2));   // a rebuild that reaches BuildHost after the exit
        await service.StartAsync();

        Assert.Null(service.Runtime);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task AfterDispose_AttachAndStartAsyncDoNothing_ButDetachThenAttachStillWorksBeforeIt()
    {
        var service = Service();
        var (bus1, state1) = Engine();
        Assert.True(service.Attach(bus1, state1));
        service.Detach();
        var (bus2, state2) = Engine();
        Assert.True(service.Attach(bus2, state2));   // a rebuild is not an exit

        service.Dispose();
        var (bus3, state3) = Engine();

        Assert.False(service.Attach(bus3, state3));
        await service.StartAsync();
        Assert.Null(service.Runtime);
    }
}
