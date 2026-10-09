using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EliteDangerous.Eddn;
using EliteDangerous.Inara;

namespace EDNexus.Core.Reporting;

/// <summary>
/// Owns the outbound reporting stack: a shared <see cref="HttpClient"/> plus the EDDN and Inara
/// bridges. Constructed by <see cref="EngineHost"/> when settings are available. Both reporters are
/// always wired to the bus but gated on their per-service opt-in flag, read live from settings, so
/// toggling a service in the UI takes effect without a restart.
/// </summary>
public sealed class ReporterHost : IAsyncDisposable
{
    private const string AppName = "EDNexus";

    // A bounded timeout (the default is 100 s) so one hung request can't hold the upload queues, and an
    // identifying User-Agent so the services can attribute (and contact) us.
    private readonly HttpClient _http;
    private readonly EddnBridge _eddn;
    private readonly InaraBridge _inara;

    /// <param name="isSuppressed">
    /// When it returns true, both reporters go silent — used to pause all outbound streaming while
    /// developer mode is fabricating events, so synthetic data never reaches EDDN or Inara.
    /// </param>
    public ReporterHost(JournalEventBus bus, AppSettings settings, string appVersion, bool isBeingDeveloped,
        Func<bool>? isSuppressed = null, IReportingLog? log = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppName}/{SafeProductVersion(appVersion)}");

        var eddnOptions = new EddnClientOptions { SoftwareName = AppName, SoftwareVersion = appVersion };
        _eddn = new EddnBridge(bus, settings, new EddnUploader(eddnOptions, _http), new EddnJournalTransformer(eddnOptions), isSuppressed, log);

        var inaraOptions = new InaraClientOptions { AppName = AppName, AppVersion = appVersion, IsBeingDeveloped = isBeingDeveloped };
        _inara = new InaraBridge(bus, settings, new InaraClient(inaraOptions, _http), isSuppressed, log);
    }

    // A User-Agent product version must be a single token; fall back rather than throw on an odd build string.
    private static string SafeProductVersion(string version)
    {
        var token = new string((version ?? "").Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '+' or '_').ToArray());
        return token.Length == 0 ? "0" : token;
    }

    public async ValueTask DisposeAsync()
    {
        await _eddn.DisposeAsync().ConfigureAwait(false);
        await _inara.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}
