using System.Collections.Concurrent;

namespace EDNexus.Ebs.Services;

/// <summary>
/// Serialises the operations that change one channel's published card: publishing a snapshot
/// (<c>POST /api/update-state</c>) and taking it down (<c>DELETE /api/update-state</c>,
/// <c>POST /oauth/revoke</c>). Without it an update that had already passed authentication could
/// store its snapshot AFTER a sign-out had cleared the channel, leaving a public card that no
/// credential could remove. Callers re-check their credential once they hold the lock.
/// </summary>
/// <remarks>
/// One semaphore per channel that has ever published or cleared on this process. Only authenticated
/// channels reach it, so the map is bounded by the number of broadcasters, not by anything a caller controls.
/// </remarks>
public sealed class ChannelLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>Waits for exclusive access to <paramref name="channelId"/>; dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(string channelId, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(channelId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
