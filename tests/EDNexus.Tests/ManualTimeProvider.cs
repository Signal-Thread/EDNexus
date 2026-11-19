namespace EDNexus.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test calls <see cref="Advance"/>.
/// Timers fire on the advancing thread, in due-time order, so "let the backoff elapse" is one call
/// instead of a sleep, and "an hour passed" costs nothing.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;
    private TaskCompletionSource _timerCreated = NewSignal();

    public ManualTimeProvider(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _now.UtcTicks;
    }

    /// <summary>Completes the next time any code creates a timer on this provider.</summary>
    public Task NextTimerCreated
    {
        get { lock (_gate) return _timerCreated.Task; }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);

        TaskCompletionSource created;
        lock (_gate)
        {
            created = _timerCreated;
            _timerCreated = NewSignal();
        }
        created.TrySetResult();
        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that falls due on the way.</summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(by));

        DateTimeOffset target;
        lock (_gate) target = _now + by;

        while (true)
        {
            ManualTimer? next = null;
            lock (_gate)
            {
                foreach (var t in _timers)
                    if (t.Due <= target && (next is null || t.Due < next.Due)) next = t;

                if (next is null)
                {
                    _now = target;
                    return;
                }

                if (next.Due > _now) _now = next.Due;
                if (next.Period > TimeSpan.Zero) next.Due = _now + next.Period;
                else _timers.Remove(next);
            }

            next.Fire();   // outside the lock: the callback may create or change timers
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; set; }
        public TimeSpan Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;

                Due = owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                owner._timers.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate) owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
