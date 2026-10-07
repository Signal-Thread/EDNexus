namespace EDNexus.Core.Telemetry;

/// <summary>
/// Suppresses repeats of the same error within a time window, so a handler that fails on every line of
/// a long journal replay produces one report (and one log write) rather than thousands. Keys are
/// remembered in a bounded table, so a stream of distinct errors can't grow it without limit.
/// Thread-safe.
/// </summary>
public sealed class ErrorThrottle
{
    private readonly TimeSpan _window;
    private readonly int _maxKeys;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, Entry> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private struct Entry
    {
        public DateTimeOffset FirstSeen;
        public int Suppressed;
    }

    /// <param name="window">How long after a report the same key stays suppressed.</param>
    /// <param name="maxKeys">Upper bound on remembered keys.</param>
    /// <param name="clock">Time source; defaults to <see cref="DateTimeOffset.UtcNow"/> (injectable for tests).</param>
    public ErrorThrottle(TimeSpan window, int maxKeys = 128, Func<DateTimeOffset>? clock = null)
    {
        if (maxKeys < 1) throw new ArgumentOutOfRangeException(nameof(maxKeys));
        _window = window;
        _maxKeys = maxKeys;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// True when <paramref name="key"/> has not been reported in the current window (the caller should
    /// report it now); false when it is a repeat. <paramref name="suppressed"/> is how many repeats were
    /// dropped since the previous report of this key.
    /// </summary>
    public bool ShouldReport(string key, out int suppressed)
    {
        var now = _clock();
        lock (_gate)
        {
            if (_seen.TryGetValue(key, out var entry))
            {
                if (now - entry.FirstSeen < _window)
                {
                    entry.Suppressed++;
                    _seen[key] = entry;
                    suppressed = 0;
                    return false;
                }
                suppressed = entry.Suppressed;
            }
            else
            {
                suppressed = 0;
                if (_seen.Count >= _maxKeys) MakeRoom(now);
            }

            _seen[key] = new Entry { FirstSeen = now, Suppressed = 0 };
            return true;
        }
    }

    /// <inheritdoc cref="ShouldReport(string, out int)"/>
    public bool ShouldReport(string key) => ShouldReport(key, out _);

    /// <summary>
    /// A stable identity for "the same failure": the exception type plus where it was thrown, optionally
    /// qualified by a <paramref name="scope"/> (such as the journal event being handled). The message is
    /// left out on purpose, as it often embeds per-line data that would defeat the grouping.
    /// </summary>
    public static string KeyFor(Exception ex, string? scope = null)
    {
        var trace = ex.StackTrace;
        string? site = null;
        if (!string.IsNullOrEmpty(trace))
        {
            var end = trace.IndexOf('\n');
            site = (end < 0 ? trace : trace[..end]).Trim();
        }
        return $"{scope}|{ex.GetType().FullName}|{site}";
    }

    // Drop expired keys; if every key is still live, forget the oldest half rather than grow.
    private void MakeRoom(DateTimeOffset now)
    {
        foreach (var k in _seen.Where(kv => now - kv.Value.FirstSeen >= _window).Select(kv => kv.Key).ToList())
            _seen.Remove(k);
        if (_seen.Count < _maxKeys) return;

        foreach (var k in _seen.OrderBy(kv => kv.Value.FirstSeen).Take(Math.Max(1, _maxKeys / 2)).Select(kv => kv.Key).ToList())
            _seen.Remove(k);
    }
}
