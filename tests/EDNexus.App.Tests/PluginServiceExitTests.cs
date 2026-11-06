using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
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
/// How <see cref="PluginService.ShutdownForExit"/>, <see cref="PluginService.Detach(TimeSpan)"/> and
/// <see cref="PluginService.Detach()"/> behave when something in the teardown does not go to plan: a
/// plugin that never returns from <c>Shutdown</c>, a handler that never returns, a runtime that
/// throws while being disposed. Each plugin blocks on a latch the test releases, so nothing here
/// sleeps; the only waits are on events, with a 30 s ceiling that is reached only when the code under
/// test hangs.
/// </summary>
public sealed class PluginServiceExitTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    /// <summary>The bound handed to <c>ShutdownForExit</c> when the plugin is meant to outlast it.</summary>
    private static readonly TimeSpan ShortBound = TimeSpan.FromMilliseconds(150);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-app-exit-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly List<PluginService> _services = [];
    private readonly List<ManualResetEventSlim> _gates = [];

    public PluginServiceExitTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Whatever a test left blocked is released first, so no stuck thread outlives the test.
        foreach (var gate in _gates) gate.Set();
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
            onError: _errors.Enqueue,
            appVersion: SemanticVersion.Parse("1.0.0"),
            pluginsRoot: _root,
            consent: request => request.Manifest.Capabilities);
        _services.Add(service);
        return service;
    }

    private static (JournalEventBus Bus, CommanderState State) Engine() => (new JournalEventBus(), new CommanderState());

    private static void Publish(JournalEventBus bus)
    {
        Assert.True(JournalEntry.TryParse("""{"timestamp":"2026-09-26T10:00:00Z","event":"FSDJump","StarSystem":"Sol"}""", historical: false, out var entry));
        bus.Publish(entry);
    }

    private sealed record Latches(ManualResetEventSlim ShutdownEntered, ManualResetEventSlim ShutdownGate, ManualResetEventSlim ShutdownReturned,
        ManualResetEventSlim HandlerEntered, ManualResetEventSlim HandlerGate, ManualResetEventSlim HandlerReturned);

    /// <summary>
    /// Installs a real plugin whose <c>Shutdown</c> and its event handler each block on a latch (and
    /// so are "stuck" until the test releases them), loads it, and returns its latches.
    /// </summary>
    private async Task<(PluginService Service, JournalEventBus Bus, Latches Latches)> LoadBlockingPlugin(string id, string ns, bool shutdownBlocks, bool handlerBlocks)
    {
        InstallBlocking(id, ns, shutdownBlocks, handlerBlocks);
        var service = Service();
        var (bus, state) = Engine();
        Assert.True(service.Attach(bus, state));
        await service.StartAsync();
        var plugin = Assert.Single(service.Runtime!.Host.Loaded);
        var type = plugin.Instance!.GetType();
        ManualResetEventSlim Get(string name)
        {
            var latch = (ManualResetEventSlim)type.GetField(name)!.GetValue(null)!;
            _gates.Add(latch);   // released on cleanup (a latch that was never waited on is harmless to set)
            return latch;
        }
        return (service, bus, new Latches(Get("ShutdownEntered"), Get("ShutdownGate"), Get("ShutdownReturned"), Get("HandlerEntered"), Get("HandlerGate"), Get("HandlerReturned")));
    }

    private static T Bounded<T>(Func<T> action, string what)
    {
        var task = Task.Run(action);
        if (!task.Wait(Ceiling))
            throw new Xunit.Sdk.XunitException(what + " did not return within the bound it was given");
        return task.Result;
    }

    // ---- a: ShutdownForExit -------------------------------------------------------------------------

    [Fact]
    public async Task ShutdownForExit_WhenAPluginsShutdownNeverReturns_GivesUpAtTheBound_AndReturnsFalse()
    {
        var (service, _, latches) = await LoadBlockingPlugin("com.test.exit.stuckshutdown", "ExitStuckShutdown", shutdownBlocks: true, handlerBlocks: false);

        var finishedInTime = Bounded(() => service.ShutdownForExit(ShortBound), "ShutdownForExit");

        Assert.False(finishedInTime);
        Assert.True(latches.ShutdownEntered.Wait(Ceiling));   // the plugin really is inside Shutdown, on the worker
        Assert.False(latches.ShutdownReturned.IsSet);
        Assert.Null(service.Runtime);                          // detached either way: the app is exiting
        Assert.Empty(_errors);

        // Releasing the plugin afterwards lets the background teardown finish; it must be harmless.
        latches.ShutdownGate.Set();
        Assert.True(latches.ShutdownReturned.Wait(Ceiling));
        Assert.True(Bounded(() => service.ShutdownForExit(Ceiling), "a second ShutdownForExit"));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task ShutdownForExit_WhenThePluginsStopInTime_ReturnsTrue_AndHasRunTheirShutdown()
    {
        var (service, _, latches) = await LoadBlockingPlugin("com.test.exit.promptshutdown", "ExitPromptShutdown", shutdownBlocks: true, handlerBlocks: false);
        latches.ShutdownGate.Set();   // positive control: the same plugin, but Shutdown returns at once

        var finishedInTime = Bounded(() => service.ShutdownForExit(Ceiling), "ShutdownForExit");

        Assert.True(finishedInTime);
        Assert.True(latches.ShutdownReturned.IsSet);   // it had completed by the time ShutdownForExit said so
        Assert.Null(service.Runtime);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task ShutdownForExit_WhenAHandlerNeverReturns_GivesUpAtTheBound_AndReturnsFalse()
    {
        var (service, bus, latches) = await LoadBlockingPlugin("com.test.exit.stuckhandler", "ExitStuckHandler", shutdownBlocks: false, handlerBlocks: true);
        Publish(bus);
        Assert.True(latches.HandlerEntered.Wait(Ceiling));   // the handler is running on the plugin's dispatch thread

        // The runtime is torn down while that handler is mid-flight: the unload waits for it (up to the
        // host's unload timeout), so the bounded exit has to give up first.
        var finishedInTime = Bounded(() => service.ShutdownForExit(ShortBound), "ShutdownForExit");

        Assert.False(finishedInTime);
        Assert.False(latches.HandlerReturned.IsSet);
        Assert.Null(service.Runtime);
        Assert.Empty(_errors);

        latches.HandlerGate.Set();
        Assert.True(latches.HandlerReturned.Wait(Ceiling));   // releasing it afterwards is harmless
        Assert.True(Bounded(() => service.ShutdownForExit(Ceiling), "a second ShutdownForExit"));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task Detach_WithABound_WhenAHandlerNeverReturns_ReturnsFalse_AndTheRuntimeIsDetached()
    {
        var (service, bus, latches) = await LoadBlockingPlugin("com.test.exit.boundedhandler", "ExitBoundedHandler", shutdownBlocks: false, handlerBlocks: true);
        Publish(bus);
        Assert.True(latches.HandlerEntered.Wait(Ceiling));

        Assert.False(Bounded(() => service.Detach(ShortBound), "Detach(timeout)"));

        Assert.Null(service.Runtime);
        latches.HandlerGate.Set();
        Assert.True(latches.HandlerReturned.Wait(Ceiling));
    }

    // ---- a: a runtime that fails while being disposed ----------------------------------------------

    /// <summary>
    /// Puts a runtime into <paramref name="service"/> whose <see cref="PluginRuntime.Dispose"/> throws.
    /// There is no seam to inject one (the runtime is sealed and built by <c>PluginRuntime.Create</c>,
    /// and nothing in a real runtime's teardown throws), so this builds one without running its
    /// constructor and gives it a supervisor but no host or bridge: disposing it fails with a
    /// <see cref="NullReferenceException"/> on the host.
    /// </summary>
    private void InjectRuntimeThatFailsToDispose(PluginService service)
    {
        // The premise, on a throwaway instance (a runtime only disposes once): it really does fail.
        Assert.Throws<NullReferenceException>(NewRuntimeThatFailsToDispose().Dispose);
        typeof(PluginService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(PluginRuntime))
            .SetValue(service, NewRuntimeThatFailsToDispose());
    }

    private PluginRuntime NewRuntimeThatFailsToDispose()
    {
        var runtime = (PluginRuntime)RuntimeHelpers.GetUninitializedObject(typeof(PluginRuntime));
        var host = new PluginHost(_root, SemanticVersion.Parse("1.0.0"), request => request.Manifest.Capabilities,
            (_, _) => throw new InvalidOperationException("never asked to build a context"));
        var supervisor = new PluginSupervisor(host, new InMemoryPluginEnablementStore());
        typeof(PluginRuntime).GetField("<Supervisor>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, supervisor);
        return runtime;
    }

    public static TheoryData<string> TeardownEntryPoints => ["Detach()", "Detach(timeout)", "ShutdownForExit", "Dispose"];

    [Theory]
    [MemberData(nameof(TeardownEntryPoints))]
    public void WhenTheRuntimeFailsToDispose_TeardownNeverThrows_ReportsIt_AndReleasesTheRuntime(string entryPoint)
    {
        var service = Service();
        InjectRuntimeThatFailsToDispose(service);

        var result = Bounded(() => entryPoint switch
        {
            "Detach()" => Ret(service.Detach),
            "Detach(timeout)" => service.Detach(Ceiling),
            "ShutdownForExit" => service.ShutdownForExit(Ceiling),
            _ => Ret(service.Dispose),
        }, entryPoint);

        Assert.True(result);                                     // contained: the failure is not the caller's problem
        Assert.Null(service.Runtime);                            // and the broken runtime is not kept
        Assert.IsType<NullReferenceException>(Assert.Single(_errors));   // but the failure was reported to onError

        // The service recovers: a new engine gets a working runtime.
        var (bus, state) = Engine();
        Assert.True(service.Attach(bus, state));
        Assert.NotNull(service.Runtime);

        static bool Ret(Action action)
        {
            action();
            return true;
        }
    }

    // ---- the helper that builds the blocking plugin -------------------------------------------------

    private void InstallBlocking(string id, string ns, bool shutdownBlocks, bool handlerBlocks)
    {
        var source = $$"""
            using EDNexus.Plugins.Abstractions;
            namespace {{ns}};
            public sealed class Plugin : IEDNexusPlugin
            {
                public static readonly System.Threading.ManualResetEventSlim
                    ShutdownEntered = new(false), ShutdownGate = new(false), ShutdownReturned = new(false),
                    HandlerEntered = new(false), HandlerGate = new(false), HandlerReturned = new(false);
                public void Initialize(IPluginContext context)
                {
                    {{(handlerBlocks ? "context.Events.SubscribeAny(e => { HandlerEntered.Set(); HandlerGate.Wait(); HandlerReturned.Set(); });" : "")}}
                }
                public void Shutdown()
                {
                    {{(shutdownBlocks ? "ShutdownEntered.Set(); ShutdownGate.Wait(); ShutdownReturned.Set();" : "")}}
                }
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
              "capabilities": ["events"]
            }
            """);
        File.WriteAllBytes(Path.Combine(dir, ns + ".dll"), stream.ToArray());
    }
}
