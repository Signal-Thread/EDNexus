namespace EDNexus.App;

/// <summary>
/// Decides which exceptions escaping onto the UI thread are survivable. A bad value in one card's
/// binding or a stray <c>NullReferenceException</c> in a click handler is not worth ending the
/// commander's session over; running out of memory or a corrupted-memory fault is, because nothing
/// after it can be trusted.
/// </summary>
/// <remarks>
/// A persistent fault would otherwise be swallowed forever — once per frame, with the UI limping on
/// in a broken state — so the policy also gives up when exceptions arrive faster than a one-off ever
/// would. At that point letting the process fail (and report) is the more honest outcome.
/// </remarks>
public sealed class UiExceptionPolicy
{
    private readonly int _maxInWindow;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _utcNow;
    private readonly Queue<DateTime> _recent = new();

    public UiExceptionPolicy(int maxInWindow = 25, TimeSpan? window = null, Func<DateTime>? utcNow = null)
    {
        _maxInWindow = maxInWindow;
        _window = window ?? TimeSpan.FromSeconds(10);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Exceptions that mean the process can no longer be trusted to carry on.</summary>
    public static bool IsFatal(Exception ex) => ex is
        OutOfMemoryException or
        AccessViolationException or
        InsufficientExecutionStackException;

    /// <summary>
    /// True when <paramref name="ex"/> should be logged and swallowed. Counts toward the rate limit
    /// whenever it is survivable, so call it exactly once per exception.
    /// </summary>
    public bool ShouldSwallow(Exception ex)
    {
        if (IsFatal(ex)) return false;

        lock (_recent)
        {
            var now = _utcNow();
            while (_recent.Count > 0 && now - _recent.Peek() > _window) _recent.Dequeue();
            if (_recent.Count >= _maxInWindow) return false;
            _recent.Enqueue(now);
            return true;
        }
    }
}
