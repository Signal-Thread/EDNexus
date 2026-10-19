using EDNexus.Core.Twitch;
using Xunit;

namespace EDNexus.Tests.Twitch;

public class StreamCardWarningTests
{
    private static StreamStatePublishResult Result(StreamStatePublishStatus status) => new(status, "x");

    [Fact]
    public void Twitch_refusing_the_backends_credentials_raises_a_warning_that_says_it_is_not_the_commanders_fault()
    {
        var warning = StreamCardWarning.For(Result(StreamStatePublishStatus.BackendRejectedByTwitch));

        Assert.NotNull(warning);
        Assert.Contains("credentials", warning);
        Assert.Contains("live updates", warning);
        Assert.Contains("not with your sign-in", warning);
    }

    [Theory]
    [InlineData(StreamStatePublishStatus.Published)]
    [InlineData(StreamStatePublishStatus.Cleared)]
    [InlineData(StreamStatePublishStatus.Unauthorized)]        // has its own, different, prompt
    [InlineData(StreamStatePublishStatus.TooLarge)]
    [InlineData(StreamStatePublishStatus.RateLimited)]
    [InlineData(StreamStatePublishStatus.Failed)]               // a dropped connection is not a banner
    [InlineData(StreamStatePublishStatus.ClearedNotDelivered)]
    public void Nothing_else_raises_one(StreamStatePublishStatus status)
        => Assert.Null(StreamCardWarning.For(Result(status)));

    [Theory]
    [InlineData(StreamStatePublishStatus.Published, true)]
    [InlineData(StreamStatePublishStatus.Cleared, true)]
    [InlineData(StreamStatePublishStatus.Failed, false)]               // proves nothing either way
    [InlineData(StreamStatePublishStatus.RateLimited, false)]
    [InlineData(StreamStatePublishStatus.BackendRejectedByTwitch, false)]
    public void Only_a_success_clears_a_shown_warning(StreamStatePublishStatus status, bool clears)
        => Assert.Equal(clears, StreamCardWarning.Clears(Result(status)));
}
