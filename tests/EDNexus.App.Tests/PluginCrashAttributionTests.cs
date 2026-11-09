using System.Reflection;
using EDNexus.App.Telemetry;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;
using Sentry;

namespace EDNexus.App.Tests;

/// <summary>
/// A crash that comes from plugin code must not be sent to EDNexus's Sentry and must be labelled in
/// the local crash file. Exceptions here are thrown from a Roslyn-compiled plugin running in a real
/// <c>PluginLoadContext</c> (loaded through <see cref="PluginRuntime"/>), so the attribution is tested
/// against the genuine context name, not a stand-in.
///
/// Not covered: the actual network send (<c>SentrySdk.Init</c> is process-global and needs a DSN), and
/// an unhandled exception on a real plugin thread (it would end the test host). The gate those paths
/// share, <c>CrashReporting.ScrubEvent</c> (the Sentry before-send hook), and the local crash file are tested.
/// </summary>
public sealed class PluginCrashAttributionTests : IDisposable
{
    private const string Members = """
        public static void Boom() => throw new System.InvalidOperationException("credits 1234567 in Sol");
        public static void FrameworkBoom() { int.Parse("not a number"); }
        public static System.Exception MakeCustom() => new Custom();
        public sealed class Custom : System.Exception { }
        public sealed class Evil : System.Exception
        {
            public override string Message => throw new System.InvalidOperationException("Message getter");
            public override string? StackTrace => throw new System.InvalidOperationException("StackTrace getter");
            public override string ToString() => throw new System.InvalidOperationException("ToString");
        }
        public static void EvilBoom() => throw new Evil();
        public static System.Threading.Tasks.Task FailInTask() => System.Threading.Tasks.Task.Run(() => { throw new System.InvalidOperationException("in a task"); });
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-crash-attr-" + Guid.NewGuid().ToString("N"));
    private readonly List<PluginRuntime> _runtimes = [];

    public PluginCrashAttributionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var runtime in _runtimes) runtime.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Installs and loads the plugin for real; returns its type (whose assembly lives in the plugin's load context).</summary>
    private Type Load(string id, string ns)
    {
        RoslynPluginKit.Install(_root, id, ns, RoslynPluginKit.PluginSource(ns, Members));
        var runtime = PluginRuntime.Create(new JournalEventBus(), new CommanderState(), new PluginRuntimeOptions
        {
            PluginsRoot = _root,
            AppVersion = SemanticVersion.Parse("1.0.0"),
            Consent = request => request.Manifest.Capabilities,
            EnablementStore = new InMemoryPluginEnablementStore(),
        })!;
        _runtimes.Add(runtime);
        var report = runtime.Start();
        Assert.Equal(PluginLoadStatus.Loaded, report.Plugins.Single().Status);
        return runtime.Host.Loaded.Single().Instance!.GetType();
    }

    /// <summary>Calls a static method of the plugin; returns what it threw, unwrapped from the reflection wrapper, plus the wrapper.</summary>
    private static (Exception Thrown, TargetInvocationException Wrapper) Call(Type plugin, string method)
    {
        var info = plugin.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!;
        var wrapper = Assert.Throws<TargetInvocationException>(() => info.Invoke(null, null));
        return (wrapper.InnerException!, wrapper);
    }

    [Fact]
    public void AnExceptionThrownByPluginCode_IsAttributedToThatPlugin()
    {
        var plugin = Load("com.test.crash.thrown", "CrashThrown");

        var (thrown, _) = Call(plugin, "Boom");

        Assert.Equal("com.test.crash.thrown", PluginCrashAttribution.FindPlugin(thrown));
        Assert.True(PluginCrashAttribution.IsFromPlugin(thrown));
        Assert.False(PluginCrashAttribution.ShouldSendToSentry(thrown));
    }

    [Fact]
    public void AFrameworkExceptionRaisedByACallFromPluginCode_IsAttributed()
    {
        var plugin = Load("com.test.crash.framework", "CrashFramework");

        var (thrown, _) = Call(plugin, "FrameworkBoom");

        Assert.IsType<FormatException>(thrown);   // thrown by the BCL; the plugin's frame is further down the stack
        Assert.Equal("com.test.crash.framework", PluginCrashAttribution.FindPlugin(thrown));
    }

    [Fact]
    public void WrappersAroundAPluginException_AreFollowed()
    {
        var plugin = Load("com.test.crash.wrapped", "CrashWrapped");
        var (thrown, reflectionWrapper) = Call(plugin, "Boom");

        // Reflection's wrapper has no plugin frame of its own that matters: the inner exception decides.
        Assert.Equal("com.test.crash.wrapped", PluginCrashAttribution.FindPlugin(reflectionWrapper));
        Assert.Equal("com.test.crash.wrapped", PluginCrashAttribution.FindPlugin(new InvalidOperationException("outer", thrown)));
        Assert.Equal("com.test.crash.wrapped", PluginCrashAttribution.FindPlugin(new AggregateException(new ArgumentException("first"), thrown)));
        Assert.Equal("com.test.crash.wrapped", PluginCrashAttribution.FindPlugin(
            new AggregateException(new AggregateException(new ArgumentException("deep"), new Exception("x", thrown)))));
    }

    [Fact]
    public async Task AFailedTaskStartedByThePlugin_IsAttributed_ThroughBothTheAwaitAndTheAggregate()
    {
        var plugin = Load("com.test.crash.task", "CrashTask");
        var started = (Task)plugin.GetMethod("FailInTask")!.Invoke(null, null)!;

        var awaited = await Assert.ThrowsAsync<InvalidOperationException>(() => started);
        var aggregate = Assert.Throws<AggregateException>(() => started.Wait());

        Assert.Equal("com.test.crash.task", PluginCrashAttribution.FindPlugin(awaited));
        // What TaskScheduler.UnobservedTaskException hands CrashReporting.Capture.
        Assert.Equal("com.test.crash.task", PluginCrashAttribution.FindPlugin(aggregate));
    }

    [Fact]
    public void AnExceptionWhoseTypeComesFromThePlugin_IsAttributed_EvenWhenOnlyHostCodeThrewIt()
    {
        var plugin = Load("com.test.crash.type", "CrashType");
        var made = (Exception)plugin.GetMethod("MakeCustom")!.Invoke(null, null)!;
        Exception? rethrown = null;
        try { throw made; } catch (Exception ex) { rethrown = ex; }   // the stack now holds only this test's frames

        Assert.Equal("com.test.crash.type", PluginCrashAttribution.FindPlugin(rethrown));
    }

    [Fact]
    public void AnExceptionThatBreaksItsOwnMembers_IsStillAttributed_AndNothingThrows()
    {
        var plugin = Load("com.test.crash.evil", "CrashEvil");
        var (evil, _) = Call(plugin, "EvilBoom");

        Assert.Equal("com.test.crash.evil", PluginCrashAttribution.FindPlugin(evil));
        var file = PluginCrashAttribution.FormatCrashFile(DateTime.UtcNow, evil);
        Assert.StartsWith("PLUGIN CRASH: plugin 'com.test.crash.evil'", file);
        Assert.Contains("could not be described", file);
        Assert.NotNull(PluginCrashAttribution.Describe(evil));
    }

    [Fact]
    public void OrdinaryExceptions_AreNotAttributed_EvenWithAPluginLoaded()
    {
        Load("com.test.crash.bystander", "CrashBystander");   // a plugin context exists, but this code is not in it

        Exception? thrown = null;
        try { int.Parse("not a number"); } catch (Exception ex) { thrown = ex; }

        Assert.Null(PluginCrashAttribution.FindPlugin(thrown));
        Assert.Null(PluginCrashAttribution.FindPlugin(new InvalidOperationException("outer", new ArgumentException("inner"))));
        Assert.Null(PluginCrashAttribution.FindPlugin(new AggregateException(new ArgumentException("a"), new FormatException("b"))));
        Assert.Null(PluginCrashAttribution.FindPlugin(new Exception("never thrown, no stack")));
        Assert.Null(PluginCrashAttribution.FindPlugin(null));
        Assert.True(PluginCrashAttribution.ShouldSendToSentry(thrown));
    }

    [Fact]
    public void AVeryDeepInnerChain_IsBoundedAndNotAttributed()
    {
        Exception chain = new Exception("bottom");
        for (var i = 0; i < 500; i++) chain = new Exception("level " + i, chain);

        Assert.Null(PluginCrashAttribution.FindPlugin(chain));
    }

    // ---- the crash file ----------------------------------------------------------------------------

    [Fact]
    public void CrashFile_NamesThePlugin_OnItsFirstLine_ForAPluginCrash_ButStillHasTheDetail()
    {
        var plugin = Load("com.test.crash.file", "CrashFile");
        var (thrown, _) = Call(plugin, "Boom");
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        var text = PluginCrashAttribution.FormatCrashFile(now, thrown);

        var lines = text.Split('\n');
        Assert.StartsWith("PLUGIN CRASH: plugin 'com.test.crash.file' caused this crash", lines[0]);
        Assert.Equal(now.ToString("o"), lines[1]);
        Assert.Contains("credits 1234567 in Sol", text);   // local only: the commander sees the detail
    }

    [Fact]
    public void CrashFile_ForAnOrdinaryCrash_IsUnchanged()
    {
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        var ex = new InvalidOperationException("ordinary");

        var text = PluginCrashAttribution.FormatCrashFile(now, ex);

        Assert.Equal(now.ToString("o") + "\n" + ex, text);
    }

    [Fact]
    public void CaptureCrash_WritesTheLabelledFile_OnlyWhenTerminating()
    {
        var plugin = Load("com.test.crash.capture", "CrashCapture");
        var (thrown, _) = Call(plugin, "Boom");
        var dir = Path.Combine(_root, "state");
        using var reporting = new CrashReporting(dir);

        reporting.CaptureCrash(thrown, terminating: false);
        Assert.False(File.Exists(Path.Combine(dir, "last_crash.txt")));

        reporting.CaptureCrash(thrown, terminating: true);
        Assert.StartsWith("PLUGIN CRASH: plugin 'com.test.crash.capture'", File.ReadAllText(Path.Combine(dir, "last_crash.txt")));

        reporting.CaptureCrash(new InvalidOperationException("ours"), terminating: true);
        var ordinary = File.ReadAllText(Path.Combine(dir, "last_crash.txt"));
        Assert.DoesNotContain("PLUGIN CRASH", ordinary);
        Assert.Contains("ours", ordinary);
    }

    // ---- the Sentry gate ---------------------------------------------------------------------------

    [Fact]
    public void BeforeSend_DropsAPluginEvent_AndKeepsAnOrdinaryOne()
    {
        var plugin = Load("com.test.crash.sentry", "CrashSentry");
        var (thrown, _) = Call(plugin, "Boom");
        using var reporting = new CrashReporting();

        Assert.Null(reporting.ScrubEvent(new SentryEvent(thrown), new SentryHint()));
        Assert.Null(reporting.ScrubEvent(new SentryEvent(new AggregateException(thrown)), new SentryHint()));

        var ordinary = reporting.ScrubEvent(new SentryEvent(new InvalidOperationException("ours")), new SentryHint());
        Assert.NotNull(ordinary);
        Assert.Null(ordinary!.ServerName);
        Assert.NotNull(reporting.ScrubEvent(new SentryEvent(), new SentryHint()));   // no exception at all: unaffected
    }

    [Fact]
    public void TheLoadContextName_StillStartsWithThePrefixThisHelperLooksFor()
    {
        // Pins the coupling to EDNexus.Plugins.Hosting.PluginLoadContext (internal): if it renames its
        // contexts, every attribution test above fails, and so does this one with a clearer message.
        var plugin = Load("com.test.crash.name", "CrashName");

        var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(plugin.Assembly);

        Assert.Equal(PluginCrashAttribution.ContextNamePrefix + "com.test.crash.name", context!.Name);
    }
}
