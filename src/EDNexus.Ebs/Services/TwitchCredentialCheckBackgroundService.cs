namespace EDNexus.Ebs.Services;

/// <summary>
/// Checks, shortly after startup and then every few hours, that Twitch accepts the credentials the
/// EBS signs PubSub calls with, and says so loudly in the log when it does not.
/// </summary>
/// <remarks>
/// Without this a wrong <c>Twitch__ExtensionSecret</c> is silent: the service starts, stores every
/// card, answers <c>/healthz</c> with ok, and simply never delivers a live update, which nobody
/// notices until a viewer's card stops changing. It never throws or stops the host, and it only
/// reports: the real broadcasts keep the authoritative record (see <see cref="TwitchCredentialStatus"/>).
/// </remarks>
public sealed class TwitchCredentialCheckBackgroundService : BackgroundService
{
    /// <summary>Delay before the first check, so the host is listening and a restart loop does not spam Twitch.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    /// <summary>Between checks once running: often enough to notice a rotated secret, rare enough to be no load.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly ITwitchCredentialProbe _probe;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _time;
    private readonly ILogger<TwitchCredentialCheckBackgroundService> _logger;

    public TwitchCredentialCheckBackgroundService(
        ITwitchCredentialProbe probe,
        IHostEnvironment environment,
        TimeProvider time,
        ILogger<TwitchCredentialCheckBackgroundService> logger)
    {
        _probe = probe;
        _environment = environment;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Decided here, not when the service is registered: the test host applies its environment late,
        // and a check that reached out to Twitch from every long-running test would be a flaky one.
        if (_environment.IsEnvironment("Testing")) return;

        try
        {
            await Task.Delay(InitialDelay, _time, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One check, exposed so tests can drive it without waiting on the delays.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await _probe.CheckAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The probe already contains its own failures; this is the last line of defence for the host.
            _logger.LogError(ex, "Unhandled error in the Twitch credential check.");
        }
    }
}
