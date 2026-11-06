using System.Collections.Concurrent;
using System.Diagnostics;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using static EDNexus.Plugins.Hosting.Tests.TestPackages;
using static EDNexus.Plugins.Hosting.Tests.TestPlugins;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// What a plugin's default log (the one <see cref="PluginRuntime"/> builds when no log factory is
/// given) actually writes to <see cref="Trace"/>: a plugin controls the text, and the app's log file
/// and any terminal that tails it must not receive its control characters. Trace is process-wide and
/// other tests write to it too, so every assertion looks only at lines tagged with this test's own
/// plugin id.
/// </summary>
public sealed class PluginTraceLogTests : IDisposable
{
    private const char Escape = '\u001b';

    private readonly TempDir _dir = new();
    private readonly CapturingListener _listener = new();
    private readonly List<IDisposable> _owned = [];

    public PluginTraceLogTests() => Trace.Listeners.Add(_listener);

    public void Dispose()
    {
        Trace.Listeners.Remove(_listener);
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _dir.Dispose();
    }

    /// <summary>Collects every line written to <see cref="Trace"/> while it is registered.</summary>
    private sealed class CapturingListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public override void Write(string? message) => _lines.Enqueue(message ?? "");

        public override void WriteLine(string? message) => _lines.Enqueue(message ?? "");

        public string[] LinesFor(string pluginId) => _lines.Where(l => l.Contains(pluginId, StringComparison.Ordinal)).ToArray();
    }

    [Fact]
    public void TheDefaultContext_StripsControlCharactersFromWhatAPluginLogs()
    {
        const string id = "com.test.tracelog.default";
        var runtime = PluginRuntime.Create(new JournalEventBus(), new CommanderState(), new PluginRuntimeOptions
        {
            PluginsRoot = _dir.Path,
            AppVersion = SemanticVersion.Parse("1.0.0"),
            Consent = Consent.AllowAll,
            EnablementStore = new InMemoryPluginEnablementStore(),
            // No ContextFactory and no LogFactory: the runtime builds PluginContext + its Trace log.
        })!;
        _owned.Add(runtime);
        WriteFolder(_dir.Path, id, Manifest(id, "TraceLogDefault.dll", "TraceLogDefault.Plugin"),
            ("TraceLogDefault.dll", Compile("TraceLogDefault", PluginSource("TraceLogDefault", initBody: """
                context.Log.Info("hello\u001b[31mred");
                context.Log.Warn("warn\u001b]0;title\u0007");
                context.Log.Error("bad\u001b[2J", new System.Exception("boom\u001b[1m"));
                """))));

        var report = runtime.Start();

        Assert.Equal(PluginLoadStatus.Loaded, Assert.Single(report.Plugins).Status);
        var lines = _listener.LinesFor(id);
        // The plugin's own text reached the log (so the assertions below are about what was written)...
        Assert.Contains(lines, l => l.Contains("hello") && l.Contains("red"));
        Assert.Contains(lines, l => l.Contains("warn"));
        Assert.Contains(lines, l => l.Contains("bad") && l.Contains("boom"));
        // ...with the escape character gone from every line it wrote.
        Assert.DoesNotContain(lines, l => l.Contains(Escape));
    }

    [Fact]
    public void TracePluginLog_StripsControlCharacters_FromMessageAndExceptionAtEveryLevel()
    {
        const string id = "com.test.tracelog.direct";
        var log = new TracePluginLog(id);

        log.Debug("debug\u001b[31m");
        log.Info("info\u001b[31m");
        log.Warn("warn\u001b[31m");
        log.Error("error\u001b[31m");
        log.Error("with exception\u001b[31m", new InvalidOperationException("boom\u001b[31m"));

        var lines = _listener.LinesFor(id);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.DoesNotContain(Escape, l));
        foreach (var word in new[] { "debug", "info", "warn", "error", "with exception", "boom" })
            Assert.Contains(lines, l => l.Contains(word));
    }

    [Fact]
    public void TracePluginLog_StripsControlCharacters_FromThePluginIdItTagsLinesWith()
    {
        // Ids are validated before a plugin loads, but the log is the last line of defence for what it writes.
        var log = new TracePluginLog("com.test.tracelog.id\u001b[31m");

        log.Info("hello");

        var line = Assert.Single(_listener.LinesFor("com.test.tracelog.id"));
        Assert.DoesNotContain(Escape, line);
    }
}
