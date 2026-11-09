using System.Collections.Concurrent;
using System.Diagnostics;
using EDNexus.App.Services.Plugins;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Plugins.Hosting;

namespace EDNexus.App.Tests;

/// <summary>
/// Names that come from the plugins folder must not be able to forge log lines. A folder name may
/// legally contain a newline on Linux/macOS, and U+2028 / U+2029 are not control characters, so the
/// load-pass log lines <see cref="PluginService.StartAsync"/> writes have to sanitise them.
/// </summary>
public sealed class PluginServiceLogInjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ednexus-app-loginject-" + Guid.NewGuid().ToString("N"));
    private readonly List<PluginService> _services = [];

    public PluginServiceLogInjectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class CapturingListener : TraceListener
    {
        public readonly ConcurrentQueue<string> Texts = new();
        public override void Write(string? message) { if (message is not null) Texts.Enqueue(message); }
        public override void WriteLine(string? message) { if (message is not null) Texts.Enqueue(message); }
    }

    [Fact]
    public async Task FolderNamesWithLineBreaks_CannotForgeLogLines()
    {
        if (OperatingSystem.IsWindows()) return;   // a Windows folder name cannot hold a newline or these separators

        var token = "tok" + Guid.NewGuid().ToString("N");
        var lineSeparator = ((char)0x2028).ToString();
        var paragraphSeparator = ((char)0x2029).ToString();
        // Looks like a second, genuine log line when echoed raw.
        var forged = $"{token}\nPlugins: com.evil.forged: Loaded\r\nPlugins: {token}{lineSeparator}two{paragraphSeparator}three";
        Directory.CreateDirectory(Path.Combine(_root, forged));
        var ordinary = Path.Combine(_root, "com.test.loginject.plain");
        Directory.CreateDirectory(ordinary);   // an ordinary non-plugin folder, for contrast

        var service = new PluginService(new AppSettings(), _ => { }, postToUi: _ => { }, isSimulated: () => false,
            appVersion: SemanticVersion.Parse("1.0.0"), pluginsRoot: _root);
        _services.Add(service);
        using var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            service.Attach(new JournalEventBus(), new CommanderState());
            await service.StartAsync();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        var mentioning = listener.Texts.Where(t => t.Contains(token)).ToList();
        Assert.NotEmpty(mentioning);   // the folder really was logged, so the checks below mean something
        Assert.All(mentioning, text =>
        {
            Assert.DoesNotContain('\n', text);
            Assert.DoesNotContain('\r', text);
            Assert.DoesNotContain(lineSeparator, text);
            Assert.DoesNotContain(paragraphSeparator, text);
        });
        Assert.DoesNotContain(listener.Texts, t => t.Contains("com.evil.forged") && !t.Contains(token));
    }
}
