using EDNexus.Core.Discord;
using EDNexus.Core.Settings;
using Xunit;

namespace EDNexus.Tests.Discord;

/// <summary>
/// Rich Presence only works under a real Discord application id. Earlier builds shipped an all-zeros
/// placeholder and saved it into every settings file, so the default is not enough: the old value has to
/// be recognised and replaced, while a commander's own real override is honoured.
/// </summary>
public class DiscordApplicationIdTests
{
    [Fact]
    public void The_default_is_the_real_application_and_not_the_placeholder()
    {
        Assert.Equal("1557985945393827911", DiscordPresenceService.DefaultApplicationId);
        Assert.NotEqual(DiscordPresenceService.PlaceholderApplicationId, DiscordPresenceService.DefaultApplicationId);
        Assert.Equal(DiscordPresenceService.DefaultApplicationId, new DiscordSettings().ApplicationId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0000000000000000000")]      // saved by earlier builds
    [InlineData("  0000000000000000000  ")]
    public void A_missing_or_placeholder_id_resolves_to_the_default(string? configured)
        => Assert.Equal(DiscordPresenceService.DefaultApplicationId, DiscordPresenceService.ResolveApplicationId(configured));

    [Fact]
    public void A_real_override_is_honoured()
    {
        Assert.Equal("123456789012345678", DiscordPresenceService.ResolveApplicationId("123456789012345678"));
        Assert.Equal("123456789012345678", DiscordPresenceService.ResolveApplicationId(" 123456789012345678 "));
    }
}
