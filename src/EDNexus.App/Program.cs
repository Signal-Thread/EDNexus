using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using EDNexus.App.Telemetry;
using EDNexus.Core.Settings;

namespace EDNexus.App;

internal static class Program
{
    /// <summary>Process-wide services, created before the UI so startup crashes can be reported.</summary>
    public static Bootstrap Services { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        // Initialize file-based tracing to a per-user local appdata logs folder so runtime logs are available locally.
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EDNexus", "logs");
            Directory.CreateDirectory(logDir);
            var file = Path.Combine(logDir, $"ednexus-{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
            var tw = new StreamWriter(File.Open(file, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Trace.Listeners.Add(new TextWriterTraceListener(tw));
            Trace.AutoFlush = true;
            Trace.TraceInformation($"EDNexus starting, log file: {file}");

            // One log per launch: keep the newest few so the folder doesn't grow without bound.
            LogRetention.Prune(logDir);
        }
        catch (Exception ex)
        {
            // Best-effort file logging — don't block startup on failure.
            Console.WriteLine($"Failed to initialise file logging: {ex}");
        }

        var store = new SettingsStore();
        var settings = store.Load();

        // Dispose flushes any pending Sentry events on exit. TryStart is a no-op unless the user
        // has previously opted in AND a DSN is present in this build.
        using var crash = new CrashReporting();
        crash.TryStart(settings);

        Services = new Bootstrap(store, settings, crash);

        // Start a non-blocking background auto-update check only if the user opted in.
        if (settings.AutoDownloadUpdates)
        {
            System.Diagnostics.Trace.TraceInformation("Program: starting background auto-update check");
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var res = await EDNexus.App.Services.AutoUpdateService.CheckForUpdatesAsync().ConfigureAwait(false);
                    System.Diagnostics.Trace.TraceInformation($"Program: background auto-update finished Found={res.Found}, Message={res.Message}, Verified={res.Verified}");
                }
                catch (Exception ex)
                {
                    // Fire-and-forget: observe the failure here rather than leave it unobserved.
                    System.Diagnostics.Trace.TraceWarning($"Program: background auto-update check failed: {ex.Message}");
                }
            });
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
