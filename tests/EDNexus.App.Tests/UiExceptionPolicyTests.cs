using EDNexus.App;

namespace EDNexus.App.Tests;

public sealed class UiExceptionPolicyTests
{
    [Fact]
    public void OrdinaryExceptions_AreSwallowed()
    {
        var policy = new UiExceptionPolicy();
        Assert.True(policy.ShouldSwallow(new InvalidOperationException("odd state")));
        Assert.True(policy.ShouldSwallow(new NullReferenceException()));
    }

    [Fact]
    public void FatalExceptions_AreNeverSwallowed()
    {
        var policy = new UiExceptionPolicy();
        Assert.False(policy.ShouldSwallow(new OutOfMemoryException()));
        Assert.False(policy.ShouldSwallow(new AccessViolationException()));
        Assert.True(UiExceptionPolicy.IsFatal(new InsufficientExecutionStackException()));
    }

    [Fact]
    public void ARunawayFailure_StopsBeingSwallowed()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var policy = new UiExceptionPolicy(maxInWindow: 3, window: TimeSpan.FromSeconds(10), utcNow: () => now);

        Assert.True(policy.ShouldSwallow(new Exception()));
        Assert.True(policy.ShouldSwallow(new Exception()));
        Assert.True(policy.ShouldSwallow(new Exception()));
        Assert.False(policy.ShouldSwallow(new Exception()));   // too many, too fast: let it fail
    }

    [Fact]
    public void TheLimitIsASlidingWindow_SoOneOffsSpacedOutAreFine()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var policy = new UiExceptionPolicy(maxInWindow: 2, window: TimeSpan.FromSeconds(10), utcNow: () => now);

        Assert.True(policy.ShouldSwallow(new Exception()));
        Assert.True(policy.ShouldSwallow(new Exception()));
        now += TimeSpan.FromSeconds(11);
        Assert.True(policy.ShouldSwallow(new Exception()));
    }
}
