using System.Reflection;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using EDNexus.Core.Telemetry;
using Sentry;

namespace EDNexus.App.Telemetry;

/// <summary>
/// Opt-in, anonymized crash &amp; error reporting via Sentry. Nothing is sent unless BOTH are true:
/// the user has consented (<see cref="AppSettings.CrashReportingEnabled"/>), and a DSN was baked in
/// at release build time (or supplied via the dev env var). Every outgoing event is run through a
/// <see cref="PiiScrubber"/> and stripped of user/host identity.
/// </summary>
public sealed class CrashReporting : IDisposable
{
    private IDisposable? _sentry;
    private PiiScrubber _scrubber = new();
    private string _installId = "";

    // Where the scrubber reads the Inara API key and CMDR name from each time it runs. Set on the UI
    // thread but read on whichever thread Sentry scrubs from, hence volatile.
    private volatile AppSettings? _settings;
    private volatile CommanderState? _commander;

    public bool IsActive { get; private set; }

    /// <summary>
    /// Start reporting if consent is granted and a DSN is available; otherwise no-op and return false.
    /// The Inara API key is read from <paramref name="settings"/> whenever a report is scrubbed, so a
    /// key saved after reporting starts is still redacted.
    /// </summary>
    public bool TryStart(AppSettings settings)
    {
        if (IsActive) return true;
        if (settings.CrashReportingEnabled != true) return false;

        var dsn = SentryConfig.ResolveDsn();
        if (string.IsNullOrWhiteSpace(dsn)) return false;

        _installId = settings.InstallId;
        _settings = settings;
        _scrubber = BuildScrubber();

        _sentry = SentrySdk.Init(o =>
        {
            o.Dsn = dsn;
            o.SendDefaultPii = false;         // no IP address, OS user, or machine name
            o.AttachStacktrace = true;
            o.AutoSessionTracking = true;     // release health — aggregate only, no PII
            o.Release = AppVersion();
            o.Environment = "production";
            o.MaxBreadcrumbs = 50;
            o.SetBeforeSend(ScrubEvent);
            o.SetBeforeBreadcrumb(ScrubBreadcrumb);
        });

        SentrySdk.ConfigureScope(s => s.User = new SentryUser { Id = _installId });
        IsActive = true;

        // Also register global unhandled exception handlers so the local log file records crashes
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Capture(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Capture(e.Exception);
            e.SetObserved();
        };

        return true;
    }

    /// <summary>Stop reporting and flush. Called on opt-out and on shutdown.</summary>
    public void Stop()
    {
        _sentry?.Dispose();
        _sentry = null;
        IsActive = false;
    }

    /// <summary>Report a handled exception (only when active).</summary>
    public void Capture(Exception ex)
    {
        // Always write crash details to the local log file for diagnostics.
        try
        {
            System.Diagnostics.Trace.TraceError("Captured exception: " + ex);
            // Write a simple crash marker so the UI can expose logs only after a crash.
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EDNexus");
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, "last_crash.txt");
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o") + "\n" + ex.ToString());
        }
        catch { }

        if (IsActive) SentrySdk.CaptureException(ex);
    }

    /// <summary>
    /// Forward journal-event handler errors to the reporter, and redact <paramref name="commander"/>'s
    /// name from every report. Called for each engine the app builds; the most recent one wins.
    /// </summary>
    public void Attach(JournalEventBus bus, CommanderState commander)
    {
        bus.HandlerError += (_, ex) => Capture(ex);
        _commander = commander;
    }

    public void Dispose() => Stop();

    private PiiScrubber BuildScrubber()
    {
        var sensitive = new List<string>
        {
            Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            JournalPaths.Resolve() ?? "",
        };
        return new PiiScrubber(sensitive, CurrentSensitive);
    }

    /// <summary>
    /// Values that change or only become known while the app runs. Re-read on every scrub, so it
    /// doesn't matter whether the key is saved before or after consent, or when the commander loads.
    /// </summary>
    private IEnumerable<string?> CurrentSensitive() => new[]
    {
        // The Inara key could ride out inside an HTTP-failure message; never let it.
        _settings?.Reporting.Inara.ApiKey,
        _commander?.Name,
    };

    private SentryEvent ScrubEvent(SentryEvent e, SentryHint hint)
    {
        e.ServerName = null; // never send the hostname

        if (e.Message is { } m)
            e.Message = new SentryMessage { Message = _scrubber.Scrub(m.Message), Formatted = _scrubber.Scrub(m.Formatted) };

        if (e.SentryExceptions is { } exceptions)
        {
            foreach (var ex in exceptions)
            {
                ex.Value = _scrubber.Scrub(ex.Value);
                var frames = ex.Stacktrace?.Frames;
                if (frames is null) continue;
                foreach (var frame in frames)
                {
                    frame.FileName = _scrubber.Scrub(frame.FileName);
                    frame.AbsolutePath = _scrubber.Scrub(frame.AbsolutePath);
                }
            }
        }

        foreach (var (key, value) in e.Tags.ToArray())
            e.SetTag(key, _scrubber.Scrub(value) ?? string.Empty);

        // Extras can hold any object; only text values can be scrubbed in place.
        foreach (var (key, value) in e.Extra.ToArray())
            if (value is string text) e.SetExtra(key, _scrubber.Scrub(text));

        // Keep only the anonymous correlation id on the user.
        e.User.Id = _installId;
        e.User.Username = null;
        e.User.Email = null;
        e.User.IpAddress = null;
        return e;
    }

    /// <summary>
    /// Runs as each breadcrumb is recorded (an event's breadcrumbs are read-only by the time it's sent),
    /// so it redacts whatever is sensitive at that moment.
    /// </summary>
    private Breadcrumb ScrubBreadcrumb(Breadcrumb b, SentryHint hint)
    {
        var message = _scrubber.Scrub(b.Message);
        var data = b.Data?.ToDictionary(kv => kv.Key, kv => _scrubber.Scrub(kv.Value) ?? string.Empty);
        var dataChanged = b.Data is not null && b.Data.Any(kv => data![kv.Key] != kv.Value);
        if (message == b.Message && !dataChanged) return b; // common case: nothing sensitive, keep as-is
        // The timestamped constructor is internal to Sentry; this hook runs as the breadcrumb is recorded,
        // so the new breadcrumb's default "now" timestamp matches the original.
        return new Breadcrumb(message ?? string.Empty, b.Type ?? string.Empty, data, b.Category, b.Level);
    }

    private static string AppVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
