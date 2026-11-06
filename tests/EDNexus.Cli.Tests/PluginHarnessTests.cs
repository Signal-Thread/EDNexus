using EDNexus.Cli;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Hosting;
using EDNexus.Plugins.Hosting.Tests;

namespace EDNexus.Cli.Tests;

public sealed class PluginHarnessTests : IDisposable
{
    private static readonly SemanticVersion App = SemanticVersion.Parse("1.0.0");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-cli-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JournalEventBus _bus = new();

    public PluginHarnessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PluginHarness Harness(string? root = null, string? single = null)
        => new(_bus, new CommanderState(), root, single, App);

    private static string Summary(PluginHarness harness)
    {
        var w = new StringWriter();
        harness.PrintSummary(w);
        return w.ToString();
    }

    /// <summary>Installs a compiled plugin <paramref name="id"/> in folder <paramref name="id"/> under <paramref name="root"/>.</summary>
    private static string Install(string root, string id, string ns, string capabilities = "\"events\"", string initBody = "", string extraMembers = "")
        => TestPlugins.WriteFolder(root, id,
            TestPlugins.Manifest(id, ns + ".dll", ns + ".Plugin", capabilities: capabilities),
            (ns + ".dll", TestPlugins.Compile(ns, TestPlugins.PluginSource(ns, initBody: initBody, extraMembers: extraMembers))));

    private void PublishEvents(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(JournalEntry.TryParse($$"""{"timestamp":"2026-09-26T10:00:0{{i % 10}}Z","event":"Music","MusicTrack":"T{{i}}"}""", false, out var entry));
            _bus.Publish(entry);
        }
    }

    [Fact]
    public void Empty_plugins_root_reports_no_plugins()
    {
        using var harness = Harness(_root);
        harness.Load();
        Assert.Contains("No plugins found.", Summary(harness));
        Assert.Empty(harness.Problems);
    }

    [Fact]
    public void Broken_plugin_folder_is_reported_as_a_problem()
    {
        Directory.CreateDirectory(Path.Combine(_root, "com.example.broken"));
        using var harness = Harness(_root);
        harness.Load();
        Assert.Contains("com.example.broken", Summary(harness));
        Assert.Contains(harness.Problems, p => p.Contains("com.example.broken") && p.Contains(nameof(PluginLoadStatus.Rejected)));
    }

    [Fact]
    public void Single_plugin_folder_that_does_not_exist_throws_clearly()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Harness(single: Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Single_plugin_folder_rejects_a_filesystem_root_and_leaves_no_scratch_dir()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "ednexus-cli-plugin-*").Length;
        Assert.Throws<ArgumentException>(() => Harness(single: Path.GetPathRoot(_root)));
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "ednexus-cli-plugin-*").Length);
    }

    [Fact]
    public void Loaded_plugin_counts_events_and_reports_clean()
    {
        Install(_root, "com.example.counter", "CounterPlugin", initBody: "context.Events.SubscribeAny(e => { });");
        using var harness = Harness(_root);
        var report = harness.Load();
        Assert.Equal(PluginLoadStatus.Loaded, Assert.Single(report!.Plugins).Status);

        PublishEvents(5);
        Assert.True(harness.Drain(TimeSpan.FromSeconds(10)));

        Assert.Contains("5 seen", Summary(harness));
        Assert.Empty(harness.Problems);
    }

    [Fact]
    public void Capabilities_are_granted_as_declared_and_storage_is_the_real_sandboxed_backend()
    {
        Install(_root, "com.example.granted", "GrantedPlugin", "\"events\",\"storage\"",
            initBody: "context.Storage.SetString(\"k\", \"v\"); context.Storage.SetString(\"gone\", \"x\"); context.Storage.Remove(\"gone\");");
        // Declares only events: touching storage must be refused, so the plugin fails to initialise.
        Install(_root, "com.example.denied", "DeniedPlugin", "\"events\"",
            initBody: "context.Storage.SetString(\"k\", \"v\");");
        // The real backend's key rules apply (the old in-memory stub would have accepted this key).
        Install(_root, "com.example.badkey", "BadKeyPlugin", "\"events\",\"storage\"",
            initBody: "context.Storage.SetString(\"not a valid key!\", \"v\");");
        using var harness = Harness(_root);
        var report = harness.Load()!;

        Assert.Equal(PluginLoadStatus.Loaded, report.Plugins.Single(p => p.FolderName == "com.example.granted").Status);
        Assert.Equal(PluginLoadStatus.Failed, report.Plugins.Single(p => p.FolderName == "com.example.denied").Status);
        Assert.Equal(PluginLoadStatus.Failed, report.Plugins.Single(p => p.FolderName == "com.example.badkey").Status);
        var text = Summary(harness);
        Assert.Contains("2 write(s), 1 remove(s) (real sandboxed storage", text);
        Assert.DoesNotContain("in-memory stub", text);
        Assert.Contains(harness.Problems, p => p.Contains("com.example.denied"));
        // The value really reached the plugin's data folder under the plugins root.
        var data = Path.Combine(_root, "com.example.granted", "data");
        Assert.True(Directory.Exists(data));
        Assert.NotEmpty(Directory.GetFiles(data, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Single_plugin_storage_goes_to_the_scratch_copy_and_never_to_the_source_folder()
    {
        Install(_root, "com.example.solo", "SoloPlugin", "\"events\",\"storage\"",
            initBody: "context.Storage.SetString(\"k\", \"v\");");
        var harness = Harness(single: Path.Combine(_root, "com.example.solo"));
        var scratch = harness.ScratchRoot!;
        harness.Load();

        Assert.True(Directory.Exists(Path.Combine(scratch, "com.example.solo", "data")));
        Assert.False(Directory.Exists(Path.Combine(_root, "com.example.solo", "data")));
        Assert.Contains("scratch copy, removed on exit", Summary(harness));
        harness.Dispose();
        Assert.False(Directory.Exists(scratch));
    }

    [Fact]
    public void Handler_errors_are_reported_and_counted()
    {
        Install(_root, "com.example.thrower", "ThrowerPlugin",
            initBody: "context.Events.SubscribeAny(e => throw new System.InvalidOperationException(\"bad handler\"));");
        using var harness = Harness(_root);
        harness.Load();

        PublishEvents(3);
        Assert.True(harness.Drain(TimeSpan.FromSeconds(10)));

        var text = Summary(harness);
        Assert.Contains("errors  : 3", text);
        Assert.Contains("bad handler", text);
        Assert.Contains(harness.Problems, p => p.Contains("com.example.thrower") && p.Contains("3 error(s)"));
    }

    [Fact]
    public void Initialize_failure_is_reported_as_failed()
    {
        Install(_root, "com.example.boom", "BoomPlugin", initBody: "throw new System.InvalidOperationException(\"init exploded\");");
        using var harness = Harness(_root);
        var report = harness.Load()!;

        Assert.Equal(PluginLoadStatus.Failed, Assert.Single(report.Plugins).Status);
        Assert.Contains("init exploded", Summary(harness));
        Assert.Contains(harness.Problems, p => p.Contains("Failed"));
    }

    [Fact]
    public void Drain_returns_false_when_a_handler_outlasts_the_timeout()
    {
        Install(_root, "com.example.slow", "SlowPlugin",
            initBody: "context.Events.SubscribeAny(e => System.Threading.Thread.Sleep(400));");
        using var harness = Harness(_root);
        harness.Load();

        PublishEvents(6);
        Assert.False(harness.Drain(TimeSpan.FromMilliseconds(200)));
        Assert.Contains(harness.Problems, p => p.Contains("drain timed out"));
    }

    [Fact]
    public void Single_plugin_folder_loads_only_that_plugin_and_never_scans_siblings()
    {
        Install(_root, "com.example.one", "OnePlugin", initBody: "context.Events.SubscribeAny(e => { });");
        Install(_root, "com.example.two", "TwoPlugin", initBody: "context.Events.SubscribeAny(e => { });");
        using var harness = Harness(single: Path.Combine(_root, "com.example.one"));
        var report = harness.Load()!;

        Assert.Equal("com.example.one", Assert.Single(report.Plugins).FolderName);
        PublishEvents(2);
        Assert.True(harness.Drain(TimeSpan.FromSeconds(10)));
        var text = Summary(harness);
        Assert.Contains("com.example.one", text);
        Assert.DoesNotContain("com.example.two", text);
        Assert.Contains("2 seen", text);
    }

    [Fact]
    public void Dispose_removes_the_scratch_copy_and_is_idempotent()
    {
        Install(_root, "com.example.one", "OnePlugin");
        var harness = Harness(single: Path.Combine(_root, "com.example.one"));
        var scratch = harness.ScratchRoot!;
        harness.Load();
        Assert.True(Directory.Exists(scratch));

        harness.Dispose();
        harness.Dispose();

        Assert.False(Directory.Exists(scratch));
        Assert.True(Directory.Exists(Path.Combine(_root, "com.example.one")));
    }

    [Fact]
    public void Plugin_text_is_stripped_of_control_characters_and_capped()
    {
        Assert.Equal("a  b", PluginHarness.SanitizeText("a\u001b\nb"));
        var capped = PluginHarness.SanitizeText(new string('x', 5000));
        Assert.True(capped.Length < 600);
        Assert.EndsWith("...", capped);
    }
}
