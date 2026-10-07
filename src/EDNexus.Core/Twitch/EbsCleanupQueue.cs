using EDNexus.Core.Settings;

namespace EDNexus.Core.Twitch;

/// <summary>What a <see cref="PendingEbsCleanup"/> asks the EBS to do.</summary>
public enum EbsCleanupKind
{
    /// <summary><c>DELETE /api/update-state</c>: forget the channel's card and tell viewers it is offline.</summary>
    ClearCard,

    /// <summary><c>POST /oauth/revoke</c>: revoke the token, which also clears the card.</summary>
    Revoke,
}

/// <summary>
/// A request the EBS has not yet acknowledged, persisted in <see cref="TwitchSettings.PendingCleanups"/>
/// so that neither a network failure nor an app restart leaves a card public after the broadcaster
/// switched it off or signed out.
/// </summary>
public sealed class PendingEbsCleanup
{
    public EbsCleanupKind Kind { get; set; }

    /// <summary>Full URL to call: the update-state or revoke endpoint of the EBS the card was on.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The EBS token the card was published with. Already on disk as <see cref="TwitchSettings.Token"/> until now.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>When the request was first queued.</summary>
    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>
    /// When the EBS confirmed the snapshot was removed but could not tell viewers already watching,
    /// for <see cref="EbsCleanupQueue.MaxOfflineNotifyRetry"/>. Null until then.
    /// </summary>
    public DateTimeOffset? SnapshotRemovedAt { get; set; }
}

/// <summary>
/// Retries card clears and token revokes until the EBS acknowledges them. The EBS keeps snapshots
/// durably, so a clear that fails once would otherwise leave the card served to new viewers until
/// the EBS's own age limit expires it.
/// </summary>
/// <remarks>
/// Entries survive restarts via <see cref="TwitchSettings.PendingCleanups"/>. The list is replaced
/// rather than mutated, so another thread serializing the settings never sees it change mid-write.
/// The retry loop saves from a worker thread while the UI thread edits other parts of the same
/// settings object, so a save can fail transiently; it is retried on the spot, and again on every
/// later round while a write is still owed.
/// </remarks>
public sealed class EbsCleanupQueue : IDisposable
{
    /// <summary>Waits between retry rounds while something is pending; the last value repeats.</summary>
    public static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
    ];

    /// <summary>
    /// How long a clear keeps retrying once the EBS has confirmed the snapshot is gone and only the
    /// offline broadcast to viewers already watching is failing. Until that confirmation a clear is
    /// retried indefinitely: the app cannot know whether the EBS expires snapshots on its own
    /// (<c>Ebs:ChannelStateMaxAgeHours</c> may be 0). Revokes are never dropped either.
    /// </summary>
    public static readonly TimeSpan MaxOfflineNotifyRetry = TimeSpan.FromHours(24);

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly IStreamStateApiClient _stateClient;
    private readonly IEbsAuthApiClient _authClient;
    private readonly TimeProvider _time;
    private readonly bool _ownsStateClient;
    private readonly bool _ownsAuthClient;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _enqueued = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Immediate attempts at one save before it is left for the next retry round.</summary>
    private const int SaveAttempts = 3;

    /// <summary>True while the on-disk queue is behind the in-memory one because every save attempt failed.</summary>
    private bool _saveOwed;

    public EbsCleanupQueue(
        AppSettings settings,
        SettingsStore store,
        IStreamStateApiClient? stateClient = null,
        IEbsAuthApiClient? authClient = null,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _settings = settings;
        _store = store;
        _ownsStateClient = stateClient is null;
        _ownsAuthClient = authClient is null;
        _stateClient = stateClient ?? new StreamStateApiClient();
        _authClient = authClient ?? new EbsAuthApiClient();
    }

    /// <summary>Requests not yet acknowledged by the EBS.</summary>
    public IReadOnlyList<PendingEbsCleanup> Pending
    {
        get { lock (_gate) return _settings.Twitch.PendingCleanups.ToArray(); }
    }

    /// <summary>
    /// Starts the background retry loop. Anything left over from a previous run is retried straight
    /// away, since the app may have been closed before the EBS acknowledged it.
    /// </summary>
    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Records a request and persists it before anything is sent, so a crash cannot lose it.</summary>
    public PendingEbsCleanup Enqueue(EbsCleanupKind kind, string endpoint, string token)
    {
        PendingEbsCleanup entry;
        lock (_gate)
        {
            var existing = _settings.Twitch.PendingCleanups
                .FirstOrDefault(p => p.Kind == kind && p.Endpoint == endpoint && p.Token == token);
            if (existing is not null) return existing;

            entry = new PendingEbsCleanup { Kind = kind, Endpoint = endpoint, Token = token, QueuedAt = _time.GetUtcNow() };
            _settings.Twitch.PendingCleanups = [.. _settings.Twitch.PendingCleanups, entry];
            Persist();
        }

        try { _enqueued.Release(); }
        catch (SemaphoreFullException) { /* the loop is already due to look */ }
        catch (ObjectDisposedException) { /* shutting down; the entry is on disk for next launch */ }
        return entry;
    }

    /// <summary>
    /// Drops every pending clear for this EBS and token. Called when the card is put back on the air
    /// on purpose, since a clear that is still waiting to be retried would otherwise take the new card
    /// down. Revokes are never dropped this way.
    /// </summary>
    public void Discard(EbsCleanupKind kind, string endpoint, string token)
    {
        if (kind == EbsCleanupKind.Revoke) return;

        lock (_gate)
        {
            var remaining = _settings.Twitch.PendingCleanups
                .Where(p => !(p.Kind == kind && p.Endpoint == endpoint && p.Token == token))
                .ToList();
            if (remaining.Count == _settings.Twitch.PendingCleanups.Count) return;
            _settings.Twitch.PendingCleanups = remaining;
            Persist();
        }
    }

    /// <summary>
    /// Drops an entry the caller has already seen acknowledged, e.g. by a clear sent directly
    /// through the card service. Accepts the result so the caller need not know what counts as done.
    /// </summary>
    public void Complete(PendingEbsCleanup entry, StreamStatePublishResult result)
    {
        if (Record(entry, result)) Remove(entry);
    }

    /// <summary>Sends every pending request once. Returns how many are still pending afterwards.</summary>
    public async Task<int> RetryPendingAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_saveOwed) Persist();
        }

        foreach (var entry in Pending)
        {
            ct.ThrowIfCancellationRequested();
            if (await SendAsync(entry, ct).ConfigureAwait(false)) Remove(entry);
        }

        return Pending.Count;
    }

    /// <summary>
    /// True once retrying a clear can achieve nothing more: the EBS acknowledged it, or rejected the
    /// token, meaning it was revoked (which clears the card) or never issued by this EBS.
    /// </summary>
    public static bool IsSettled(StreamStatePublishResult result) => result.IsSuccess || result.RequiresReauth;

    private async Task<bool> SendAsync(PendingEbsCleanup entry, CancellationToken ct)
    {
        // Never send the token in cleartext. Publishing refuses the same addresses, so a card was
        // never put on the air there and there is nothing to clear.
        if (!TwitchOAuthOptions.IsSecureEbsUrl(entry.Endpoint)) return true;

        if (entry.SnapshotRemovedAt is { } removedAt && _time.GetUtcNow() - removedAt > MaxOfflineNotifyRetry) return true;

        try
        {
            switch (entry.Kind)
            {
                case EbsCleanupKind.ClearCard:
                    return Record(entry, await _stateClient.ClearAsync(entry.Endpoint, entry.Token, ct).ConfigureAwait(false));

                case EbsCleanupKind.Revoke:
                    // Throws when the EBS is unreachable or failed; answers 200 even for a token it
                    // no longer knows, so returning means there is nothing left to do.
                    await _authClient.RevokeAsync(entry.Endpoint, entry.Token, ct).ConfigureAwait(false);
                    return true;

                default:
                    return true; // unknown kind from a newer build: drop rather than retry forever
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return false;
        }
    }

    /// <summary>Notes a confirmed removal on the entry; returns whether the clear is settled.</summary>
    private bool Record(PendingEbsCleanup entry, StreamStatePublishResult result)
    {
        if (IsSettled(result)) return true;

        if (result.Status == StreamStatePublishStatus.ClearedNotDelivered && entry.SnapshotRemovedAt is null)
        {
            lock (_gate)
            {
                entry.SnapshotRemovedAt = _time.GetUtcNow();
                Persist();
            }
        }

        return false;
    }

    private void Remove(PendingEbsCleanup entry)
    {
        lock (_gate)
        {
            var remaining = _settings.Twitch.PendingCleanups.Where(p => !ReferenceEquals(p, entry)).ToList();
            if (remaining.Count == _settings.Twitch.PendingCleanups.Count) return;
            _settings.Twitch.PendingCleanups = remaining;
            Persist();
        }
    }

    /// <summary>
    /// Writes the settings file, trying again if the write fails — most likely because the UI thread
    /// changed a collection under the serializer. Call with <see cref="_gate"/> held. A write that
    /// still fails is logged and retried at the start of the next retry round.
    /// </summary>
    private void Persist()
    {
        for (var attempt = 1; attempt <= SaveAttempts; attempt++)
        {
            if (_store.TrySave(_settings))
            {
                _saveOwed = false;
                return;
            }

            if (attempt < SaveAttempts) Thread.Sleep(15);
        }

        _saveOwed = true;
        System.Diagnostics.Trace.TraceWarning("Twitch: could not save the pending EBS cleanup queue; will retry.");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            var attempt = 0;
            if (Pending.Count > 0 && await RetryPendingAsync(ct).ConfigureAwait(false) > 0) attempt = 1;

            while (!ct.IsCancellationRequested)
            {
                var wait = Pending.Count > 0
                    ? RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)]
                    : Timeout.InfiniteTimeSpan;

                // A new entry restarts the backoff but is not sent at once: the caller usually sends
                // it directly first, and a second request racing that one gains nothing.
                if (await _enqueued.WaitAsync(wait, ct).ConfigureAwait(false))
                {
                    attempt = 0;
                    continue;
                }

                attempt = await RetryPendingAsync(ct).ConfigureAwait(false) > 0 ? attempt + 1 : 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* cancellation */ }

        if (_ownsStateClient) (_stateClient as IDisposable)?.Dispose();
        if (_ownsAuthClient) (_authClient as IDisposable)?.Dispose();
        _cts.Dispose();
    }
}
