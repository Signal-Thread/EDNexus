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

        // Also register global unhandled exception handlers so the local log file records crashes.
        RegisterGlobalHandlers();

        return true;
    }

    /// <summary>Stop reporting and flush. Called on opt-out and on shutdown.</summary>
    public void Stop()
    {
        // Unhook first, so an opt-out/opt-in cycle leaves exactly one set of handlers registered.
        UnregisterGlobalHandlers();
        _sentry?.Dispose();
        _sentry = null;
        IsActive = false;
    }

    // Named handlers (not lambdas) so they can be removed again; guarded so registering twice is a no-op.
    private bool _globalHandlersRegistered;
    private readonly UnhandledExceptionEventHandler _onUnhandled;
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _onUnobserved;

    // A handler that fails on every line of a journal replay would otherwise log, write a file and send
    // a Sentry event per line. One report per distinct failure per window is plenty.
    private readonly ErrorThrottle _throttle = new(TimeSpan.FromMinutes(5), maxKeys: 128);

    public CrashReporting()
    {
        _onUnhandled = (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CaptureCrash(ex, e.IsTerminating);
        };
        _onUnobserved = (_, e) =>
        {
            Capture(e.Exception);
            e.SetObserved();
        };
    }

    private void RegisterGlobalHandlers()
    {
        if (_globalHandlersRegistered) return;
        AppDomain.CurrentDomain.UnhandledException += _onUnhandled;
        TaskScheduler.UnobservedTaskException += _onUnobserved;
        _globalHandlersRegistered = true;
    }

    private void UnregisterGlobalHandlers()
    {
        if (!_globalHandlersRegistered) return;
        AppDomain.CurrentDomain.UnhandledException -= _onUnhandled;
        TaskScheduler.UnobservedTaskException -= _onUnobserved;
        _globalHandlersRegistered = false;
    }

    /// <summary>
    /// Report a handled (non-fatal) exception: logged locally and, when active, sent to Sentry.
    /// Repeats of the same failure within a few minutes are dropped. Does NOT write the crash marker.
    /// </summary>
    /// <param name="scope">Optional context that is part of the dedupe key, such as the journal event being handled.</param>
    public void Capture(Exception ex, string? scope = null)
    {
        if (!_throttle.ShouldReport(ErrorThrottle.KeyFor(ex, scope), out var suppressed)) return;

        try
        {
            var repeats = suppressed > 0 ? $" (+{suppressed} similar suppressed)" : "";
            System.Diagnostics.Trace.TraceError("Captured exception" + repeats + ": " + ex);
        }
        catch { }

        if (IsActive) SentrySdk.CaptureException(ex);
    }

    /// <summary>
    /// Report an unhandled exception. When the runtime is terminating because of it, also leaves a
    /// crash marker file so the UI can offer the logs after a real crash (and only then).
    /// </summary>
    private void CaptureCrash(Exception ex, bool terminating)
    {
        try
        {
            System.Diagnostics.Trace.TraceError("Unhandled exception: " + ex);
            if (terminating)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EDNexus");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "last_crash.txt"), DateTime.UtcNow.ToString("o") + "\n" + ex);
            }
        }
        catch { }

        if (IsActive) SentrySdk.CaptureException(ex);
    }

    private JournalEventBus? _attachedBus;
    private Action<JournalEntry, Exception>? _attachedHandler;

    /// <summary>
    /// Forward journal-event handler errors to the reporter, and redact <paramref name="commander"/>'s
    /// name from every report. Called for each engine the app builds; the most recent one wins.
    /// </summary>
    public void Attach(JournalEventBus bus, CommanderState commander)
    {
        if (_attachedBus is not null && _attachedHandler is not null)
            _attachedBus.HandlerError -= _attachedHandler;

        _attachedHandler = (entry, ex) => Capture(ex, entry.Event);
        _attachedBus = bus;
        bus.HandlerError += _attachedHandler;
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
