using EDNexus.Core.Telemetry;
using Xunit;

namespace EDNexus.Tests.Telemetry;

public class ErrorThrottleTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    private static (ErrorThrottle Throttle, Clock Clock) New(int maxKeys = 128)
    {
        var clock = new Clock();
        return (new ErrorThrottle(TimeSpan.FromMinutes(5), maxKeys, clock.Read), clock);
    }

    [Fact]
    public void The_first_occurrence_is_reported_and_repeats_inside_the_window_are_not()
    {
        var (throttle, _) = New();

        Assert.True(throttle.ShouldReport("k"));
        for (var i = 0; i < 1000; i++) Assert.False(throttle.ShouldReport("k"));
    }

    [Fact]
    public void Different_keys_are_independent()
    {
        var (throttle, _) = New();

        Assert.True(throttle.ShouldReport("a"));
        Assert.True(throttle.ShouldReport("b"));
        Assert.False(throttle.ShouldReport("a"));
    }

    [Fact]
    public void After_the_window_the_key_is_reported_again_with_the_suppressed_count()
    {
        var (throttle, clock) = New();
        throttle.ShouldReport("k");
        for (var i = 0; i < 7; i++) throttle.ShouldReport("k");

        clock.Now += TimeSpan.FromMinutes(5);

        Assert.True(throttle.ShouldReport("k", out var suppressed));
        Assert.Equal(7, suppressed);
        Assert.False(throttle.ShouldReport("k"));
    }

    [Fact]
    public void The_table_stays_bounded_under_a_stream_of_distinct_errors()
    {
        var (throttle, _) = New(maxKeys: 8);

        for (var i = 0; i < 10_000; i++) Assert.True(throttle.ShouldReport("k" + i));

        // Still functional afterwards: a fresh key reports once, then is suppressed.
        Assert.True(throttle.ShouldReport("fresh"));
        Assert.False(throttle.ShouldReport("fresh"));
    }

    [Fact]
    public void KeyFor_groups_the_same_failure_regardless_of_message_and_separates_types_and_scopes()
    {
        static Exception Thrown(Func<Exception> make)
        {
            try { throw make(); } catch (Exception ex) { return ex; }
        }
        Exception A(string msg) => Thrown(() => new InvalidOperationException(msg));

        // Same throw site (the lambda in A) and type, different message: one group.
        Assert.Equal(ErrorThrottle.KeyFor(A("line 1")), ErrorThrottle.KeyFor(A("line 2")));
        Assert.NotEqual(ErrorThrottle.KeyFor(A("x")), ErrorThrottle.KeyFor(Thrown(() => new ArgumentException("x"))));
        Assert.NotEqual(ErrorThrottle.KeyFor(A("x"), "Docked"), ErrorThrottle.KeyFor(A("x"), "Scan"));
    }

    [Fact]
    public void A_repeating_handler_failure_yields_one_report_across_a_replay_sized_burst()
    {
        var (throttle, _) = New();
        var reported = 0;

        for (var line = 0; line < 5000; line++)
        {
            Exception ex;
            try { throw new InvalidOperationException($"bad line {line}"); } catch (Exception e) { ex = e; }
            if (throttle.ShouldReport(ErrorThrottle.KeyFor(ex, "Scan"))) reported++;
        }

        Assert.Equal(1, reported);
    }
}
