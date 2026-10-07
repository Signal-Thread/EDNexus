using EDNexus.Ebs.Options;
using Microsoft.Extensions.Options;

namespace EDNexus.Ebs.Services;

/// <summary>
/// Deletes channel snapshots older than <see cref="EbsOptions.ChannelStateMaxAgeHours"/> on a timer.
/// Reads already hide an expired snapshot, so this only reclaims space; keeping it off the request
/// path is what lets <c>GET /api/initial-state</c> and publishes stay free of a table-wide DELETE
/// under the SQLite writer lock.
/// </summary>
public sealed class ChannelStatePruneBackgroundService : BackgroundService
{
    private readonly IChannelStateStore _store;
    private readonly EbsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelStatePruneBackgroundService> _logger;

    public ChannelStatePruneBackgroundService(
        IChannelStateStore store,
        IOptions<EbsOptions> options,
        TimeProvider timeProvider,
        ILogger<ChannelStatePruneBackgroundService> logger)
    {
        _store = store;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.ChannelStatePruneIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            RunOnce();

            try { await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One prune pass. Never throws: a failed prune is logged and retried next interval.</summary>
    internal void RunOnce()
    {
        try
        {
            var removed = _store.PruneExpired();
            if (removed > 0)
                _logger.LogInformation("Pruned {Count} expired channel snapshot(s).", removed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prune expired channel snapshots; will retry next interval.");
        }
    }
}
