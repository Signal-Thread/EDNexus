using System.Collections.Concurrent;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;
using EDNexus.Plugins.Hosting.Bridge;

namespace EDNexus.Samples.Tests;

/// <summary>
/// Loads the built "Hello Commander" sample through the real <see cref="PluginHost"/> and
/// <see cref="PluginBridge"/> and drives it with real journal events, so the sample's manifest,
/// folder layout and behaviour are all proved against the SDK it ships with.
/// </summary>
public sealed class HelloCommanderTests : IDisposable
{
    private const string PluginId = "com.ednexus.samples.hellocommander";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-sample-tests-" + Guid.NewGuid().ToString("N"));

    public HelloCommanderTests()
    {
        Directory.CreateDirectory(_root);
        // Install the sample exactly as a user would: copy the build's plugin folder under the plugins root.
        var built = Path.Combine(FindRepoRoot(), "samples", "HelloCommander", "out", PluginId);
        Assert.True(Directory.Exists(built), $"the sample build did not produce {built}");
        var installed = Path.Combine(_root, PluginId);
        Directory.CreateDirectory(installed);
        foreach (var file in Directory.GetFiles(built))
            File.Copy(file, Path.Combine(installed, Path.GetFileName(file)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* best effort: the unloaded assembly can stay locked briefly */
        }
    }

    [Fact]
    public void Manifest_IsValidAndDeclaresWhatTheSampleUses()
    {
        var parsed = PluginManifestParser.ParseFile(Path.Combine(_root, PluginId, "plugin.json"));

        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        var manifest = parsed.Manifest!;
        Assert.Equal(PluginId, manifest.Id);
        Assert.Equal(PluginSdk.CurrentVersionString, manifest.SdkVersion);
        Assert.Equal(["events", "state", "storage"], manifest.Capabilities.Order(StringComparer.Ordinal));
        Assert.False(manifest.Declares(PluginCapabilities.UiDashboard), "the sample must not declare the unimplemented UI capabilities");
    }

    [Fact]
    public void LoadsAndLogsAGreetingForEachFsdJump()
    {
        using var rig = new Rig(_root);
        var report = rig.Host.LoadAll();

        var loaded = Assert.Single(report.Plugins);
        Assert.True(loaded.Status == PluginLoadStatus.Loaded, loaded.ToString());

        rig.Publish("""{"timestamp":"2026-01-01T12:00:02Z","event":"LoadGame","Commander":"Jameson","Ship":"python","Credits":5}""");
        rig.Publish("""{"timestamp":"2026-01-01T12:00:03Z","event":"Location","StarSystem":"Sol"}""");
        rig.Publish("""{"timestamp":"2026-01-01T12:05:00Z","event":"FSDJump","StarSystem":"Alpha Centauri"}""");
        rig.Publish("""{"timestamp":"2026-01-01T12:05:30Z","event":"Docked","StationName":"Not a jump"}""");
        rig.Publish("""{"timestamp":"2026-01-01T12:06:00Z","event":"FSDJump","StarSystem":"Wolf 359"}""");

        rig.WaitForLine(l => l.Contains("2 jumps"));
        var greetings = rig.Lines.Where(l => l.StartsWith("CMDR ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, greetings.Count);
        Assert.StartsWith("CMDR Jameson - 1 jumps, now in Alpha Centauri", greetings[0]);
        Assert.StartsWith("CMDR Jameson - 2 jumps, now in Wolf 359", greetings[1]);
        Assert.Empty(rig.Errors);
    }

    [Fact]
    public void LiveJumpsAreSavedAndSurviveAReload_ButReplayedOnesAreNot()
    {
        using (var rig = new Rig(_root))
        {
            rig.Host.LoadAll();
            // Replayed (historical) jumps count for the session but must not touch the saved total.
            rig.Publish("""{"timestamp":"2026-01-01T12:05:00Z","event":"FSDJump","StarSystem":"Old One"}""", historical: true);
            rig.Publish("""{"timestamp":"2026-01-01T12:06:00Z","event":"FSDJump","StarSystem":"Live One"}""");
            rig.Publish("""{"timestamp":"2026-01-01T12:07:00Z","event":"FSDJump","StarSystem":"Live Two"}""");
            rig.WaitForLine(l => l.Contains("3 jumps, now in Live Two"));
            Assert.Contains(rig.Lines, l => l.Contains("3 jumps, now in Live Two") && l.EndsWith("(lifetime 2)"));
        }

        using var second = new Rig(_root);
        second.Host.LoadAll();
        // Logged from Initialize, so it is already there once LoadAll returns.
        Assert.Contains(second.Lines, l => l.Contains("lifetime total so far: 2"));
    }

    [Fact]
    public void WithoutTheStorageGrant_StillWorksAndSaysWhyNothingIsSaved()
    {
        using var rig = new Rig(_root, grant: [PluginCapabilities.Events, PluginCapabilities.State]);
        rig.Host.LoadAll();
        rig.Publish("""{"timestamp":"2026-01-01T12:05:00Z","event":"FSDJump","StarSystem":"Alpha Centauri"}""");

        rig.WaitForLine(l => l.StartsWith("CMDR ", StringComparison.Ordinal));
        Assert.Single(rig.Lines, l => l.StartsWith("Storage unavailable", StringComparison.Ordinal));
        Assert.Contains(rig.Lines, l => l.StartsWith("CMDR ", StringComparison.Ordinal) && l.Contains("1 jumps"));
        Assert.Empty(rig.Errors);
        Assert.False(Directory.Exists(Path.Combine(_root, PluginId, "data")));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "EDNexus.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("EDNexus.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>An engine (bus + state), a bridge over it, and a host that builds contexts from bridge sessions.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly JournalEventBus _bus = new();
        private readonly PluginBridge _bridge;
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly ConcurrentQueue<string> _errors = new();
        // One release per log line, so WaitForLine blocks until the plugin's thread has logged instead of polling.
        // Never disposed: a late log call from an unwinding plugin thread must not throw.
        private readonly SemaphoreSlim _lineLogged = new(0);

        public Rig(string root, string[]? grant = null)
        {
            var state = new CommanderState();
            _ = new StateTracker(_bus, state);
            _bridge = new PluginBridge(_bus, state, new PluginBridgeOptions
            {
                PluginsRoot = root,
                HandlerError = e => _errors.Enqueue($"{e.EventName}: {e.ExceptionType}: {e.Message}"),
            });
            Host = new PluginHost(root, SemanticVersion.Parse("1.0.0"),
                request => grant ?? (IReadOnlyCollection<string>)request.Manifest.Capabilities,
                (manifest, granted) => new Context(manifest, _bridge.Attach(manifest, granted), new Log(line => { _lines.Enqueue(line); _lineLogged.Release(); })));
        }

        public PluginHost Host { get; }

        public IReadOnlyCollection<string> Lines => _lines.ToArray();

        public IReadOnlyCollection<string> Errors => _errors.ToArray();

        public void Publish(string json, bool historical = false)
        {
            Assert.True(JournalEntry.TryParse(json, historical, out var entry));
            _bus.Publish(entry);
        }

        /// <summary>
        /// Handlers run on the plugin's own thread: block until a log line matches. Each logged line
        /// releases <see cref="_lineLogged"/>, so a line logged before the wait starts is never missed.
        /// The 10 s bound only fails a run in which the line never comes.
        /// </summary>
        public void WaitForLine(Func<string, bool> match)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var limit = TimeSpan.FromSeconds(10);
            while (!_lines.Any(match) && clock.Elapsed < limit)
                _lineLogged.Wait(limit - clock.Elapsed);
            Assert.True(_lines.Any(match), $"expected log line not seen, got: {string.Join(" | ", _lines)}");
        }

        public void Dispose()
        {
            Host.Dispose();
            _bridge.Dispose();
        }
    }

    private sealed class Context(PluginManifest manifest, PluginBridgeSession session, IPluginLog log)
        : IPluginContext, IDisposable, IPluginSessionControl
    {
        public IReadOnlyCommanderState State => session.State;
        public IPluginEvents Events => session.Events;
        public IPluginLog Log => log;
        public IPluginStorage Storage => session.Storage;
        public IUiRegistry Ui => session.Ui;
        public PluginManifest Manifest => manifest;
        public void Dispose() => session.Dispose();
        public bool WaitForExit(TimeSpan timeout) => session.WaitForExit(timeout);
    }

    private sealed class Log(Action<string> sink) : IPluginLog
    {
        public void Debug(string message) { }
        public void Info(string message) => sink(message);
        public void Warn(string message) => sink(message);
        public void Error(string message, Exception? exception = null) => sink("ERROR " + message);
    }
}
