using System.Collections.Concurrent;
using System.Text.Json;

namespace EDNexus.Ebs.Services;

/// <summary>
/// Process-local, in-memory implementation of <see cref="IChannelStateStore"/>, for tests and
/// throwaway local runs (<c>Ebs:StorageProvider = InMemory</c>): the cache is lost on restart.
/// Production uses <see cref="SqliteChannelStateStore"/>.
/// </summary>
public sealed class InMemoryChannelStateStore : IChannelStateStore
{
    private readonly ConcurrentDictionary<string, (JsonElement State, DateTimeOffset UpdatedAt)> _state = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan? _maxAge;

    /// <param name="maxAge">Oldest snapshot served; null keeps snapshots until they are removed.</param>
    public InMemoryChannelStateStore(TimeProvider? timeProvider = null, TimeSpan? maxAge = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxAge = maxAge;
    }

    /// <inheritdoc />
    public void Set(string channelId, JsonElement state) =>
        _state[channelId] = (state.Clone(), _timeProvider.GetUtcNow());

    /// <inheritdoc />
    public bool TryGet(string channelId, out JsonElement state)
    {
        if (_state.TryGetValue(channelId, out var entry))
        {
            if (_maxAge is not { } maxAge || _timeProvider.GetUtcNow() - entry.UpdatedAt <= maxAge)
            {
                state = entry.State;
                return true;
            }

            _state.TryRemove(new KeyValuePair<string, (JsonElement, DateTimeOffset)>(channelId, entry));
        }

        state = default;
        return false;
    }

    /// <inheritdoc />
    public void Remove(string channelId) => _state.TryRemove(channelId, out _);

    /// <inheritdoc />
    public int PruneExpired()
    {
        if (_maxAge is not { } maxAge) return 0;

        var removed = 0;
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _state)
        {
            if (now - pair.Value.UpdatedAt > maxAge
                && _state.TryRemove(new KeyValuePair<string, (JsonElement, DateTimeOffset)>(pair.Key, pair.Value)))
                removed++;
        }

        return removed;
    }
}
