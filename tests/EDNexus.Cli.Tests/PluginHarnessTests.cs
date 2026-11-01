using EDNexus.Cli;
using EDNexus.Core.Journal;
using EDNexus.Core.State;

namespace EDNexus.Cli.Tests;

public sealed class PluginHarnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-cli-tests-" + Guid.NewGuid().ToString("N"));

    public PluginHarnessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string Summary(PluginHarness harness)
    {
        var w = new StringWriter();
        harness.PrintSummary(w);
        return w.ToString();
    }

    [Fact]
    public void Empty_plugins_root_reports_no_plugins()
    {
        using var harness = new PluginHarness(new JournalEventBus(), new CommanderState(), _root, null);
        harness.Load();
        Assert.Contains("No plugins found.", Summary(harness));
    }

    [Fact]
    public void Broken_plugin_folder_is_reported_and_does_not_throw()
    {
        Directory.CreateDirectory(Path.Combine(_root, "com.example.broken"));
        using var harness = new PluginHarness(new JournalEventBus(), new CommanderState(), _root, null);
        harness.Load();
        var text = Summary(harness);
        Assert.Contains("com.example.broken", text);
        Assert.Contains("Rejected", text);
    }

    [Fact]
    public void Single_plugin_folder_that_does_not_exist_throws_clearly()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            new PluginHarness(new JournalEventBus(), new CommanderState(), null, Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Single_plugin_folder_loads_only_that_folder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "com.example.one"));
        Directory.CreateDirectory(Path.Combine(_root, "com.example.two"));
        using var harness = new PluginHarness(new JournalEventBus(), new CommanderState(), null, Path.Combine(_root, "com.example.one"));
        harness.Load();
        var text = Summary(harness);
        Assert.Contains("com.example.one", text);
        Assert.DoesNotContain("com.example.two", text);
    }
}
