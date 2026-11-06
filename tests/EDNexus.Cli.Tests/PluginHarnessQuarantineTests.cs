using EDNexus.Cli;
using EDNexus.Core.Journal;
using EDNexus.Core.State;
using EDNexus.Plugins.Hosting;
using EDNexus.Plugins.Hosting.Tests;

namespace EDNexus.Cli.Tests;

/// <summary>
/// <c>--plugins-strict</c> exits 1 whenever <see cref="PluginHarness.Problems"/> is non-empty, so the
/// supervisor's quarantine of a plugin whose handlers keep failing must show up there: the harness
/// runs the same <see cref="PluginRuntime"/> (and fault policy) as the app.
/// </summary>
public sealed class PluginHarnessQuarantineTests : IDisposable
{
    /// <summary>The generous ceiling for the asynchronous quarantine: only reached when it never happens.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-cli-quarantine-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JournalEventBus _bus = new();

    public PluginHarnessQuarantineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void Install(string root, string id, string ns, string initBody)
        => TestPlugins.WriteFolder(root, id,
            TestPlugins.Manifest(id, ns + ".dll", ns + ".Plugin"),
            (ns + ".dll", TestPlugins.Compile(ns, TestPlugins.PluginSource(ns, initBody: initBody))));

    /// <summary>A synthetic journal: <paramref name="count"/> events, each of which the plugin below would see.</summary>
    private void PublishEvents(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(JournalEntry.TryParse($$"""{"timestamp":"2026-09-26T10:00:0{{i % 10}}Z","event":"Music","MusicTrack":"T{{i}}"}""", false, out var entry));
            _bus.Publish(entry);
        }
    }

    private PluginHarness Harness() => new(_bus, new CommanderState(), _root, null, SemanticVersion.Parse("1.0.0"));

    [Fact]
    public void A_plugin_the_supervisor_quarantines_is_reported_as_a_problem()
    {
        const string id = "com.example.qthrower";
        Install(_root, id, "QThrowerPlugin",
            "context.Events.SubscribeAny(e => throw new System.InvalidOperationException(\"always fails\"));");
        using var harness = Harness();
        Assert.Equal(PluginLoadStatus.Loaded, Assert.Single(harness.Load()!.Plugins).Status);

        // The default policy quarantines on more than 10 failures within a minute: 15 is clearly past it.
        PublishEvents(15);

        // The quarantine runs on the thread pool after the 11th failure; wait on the condition, not on time.
        Assert.True(
            SpinWait.SpinUntil(() => harness.Problems.Any(p => p.Contains(nameof(PluginState.Quarantined))), Ceiling),
            "the supervisor never quarantined a plugin whose every handler call throws");
        var quarantine = Assert.Single(harness.Problems, p => p.Contains(nameof(PluginState.Quarantined)));
        Assert.StartsWith(id + ": ", quarantine);
        Assert.Contains("always fails", quarantine);
        // The load itself succeeded: the quarantine entry is what makes a strict run fail for the reason it should.
        Assert.DoesNotContain(harness.Problems, p => p.Contains(nameof(PluginLoadStatus.Failed)));
    }

    [Fact]
    public void A_plugin_that_fails_less_often_than_the_policy_allows_is_not_quarantined()
    {
        const string id = "com.example.qoccasional";
        Install(_root, id, "QOccasionalPlugin",
            "context.Events.SubscribeAny(e => throw new System.InvalidOperationException(\"sometimes fails\"));");
        using var harness = Harness();
        harness.Load();

        PublishEvents(3);
        Assert.True(harness.Drain(TimeSpan.FromSeconds(10)));

        // Negative control: its errors are reported, but it was not quarantined.
        Assert.Contains(harness.Problems, p => p.StartsWith(id + ": 3 error(s)", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Problems, p => p.Contains(nameof(PluginState.Quarantined)));
    }
}
