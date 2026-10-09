using EDNexus.Core.Overlay;
using Xunit;

namespace EDNexus.Tests.Overlay;

/// <summary>
/// The overlay is a topmost window, so (#204) it must touch the desktop as little as possible: it is only
/// shown while Elite is in front, and an unchanged snapshot must be recognisable as unchanged.
/// </summary>
public class OverlayVisibilityTests
{
    private const string Own = "EDNexus.App";

    [Theory]
    [InlineData("EliteDangerous64", true)]
    [InlineData("elitedangerous64", true)]      // case-insensitive
    [InlineData("EliteDangerous64.exe", true)]  // a name with the extension still matches
    [InlineData("EDNexus.App", true)]           // EDNexus itself, so the Settings preview works
    [InlineData("EDCopilot", false)]
    [InlineData("chrome", false)]
    [InlineData("explorer", false)]
    public void Only_the_game_or_edNexus_in_front_shows_the_overlay(string foreground, bool expected)
        => Assert.Equal(expected, OverlayVisibilityPolicy.ShouldShow(true, true, foreground, Own));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void An_unknown_foreground_fails_open_so_a_detection_problem_never_hides_the_overlay_for_good(string? foreground)
        => Assert.True(OverlayVisibilityPolicy.ShouldShow(true, true, foreground, Own));

    [Fact]
    public void Switched_off_is_never_shown_whatever_is_in_front()
    {
        Assert.False(OverlayVisibilityPolicy.ShouldShow(false, true, "EliteDangerous64", Own));
        Assert.False(OverlayVisibilityPolicy.ShouldShow(false, false, "EliteDangerous64", Own));
    }

    [Fact]
    public void The_always_on_top_option_keeps_the_old_behaviour()
        => Assert.True(OverlayVisibilityPolicy.ShouldShow(true, false, "EDCopilot", Own));

    [Fact]
    public void Focus_only_is_the_default()
        => Assert.True(new EDNexus.Core.Settings.OverlaySettings().OnlyWhenGameFocused);

    // ---- unchanged content is recognised, so the overlay can skip updates ----

    private static OverlayContent Content(string system = "Sol", params (string Name, int Remaining)[] shortfalls)
        => new(system, "Alpha Centauri", 12.5, 32, "Sol 3 a", 2,
               shortfalls.Select(s => new OverlayShortfallLine(s.Name, s.Remaining)).ToList(), "detail");

    [Fact]
    public void Two_separately_built_identical_snapshots_are_the_same()
    {
        // The builder makes a new list every tick, so reference equality (the record's default) says "changed".
        var a = Content("Sol", ("Steel", 100), ("Aluminium", 40));
        var b = Content("Sol", ("Steel", 100), ("Aluminium", 40));

        Assert.NotSame(a.ColonisationShortfalls, b.ColonisationShortfalls);
        Assert.True(a.SameAs(b));
    }

    [Fact]
    public void Any_visible_change_makes_a_snapshot_different()
    {
        var baseline = Content("Sol", ("Steel", 100));

        Assert.False(baseline.SameAs(Content("Barnard's Star", ("Steel", 100))));       // a jump
        Assert.False(baseline.SameAs(Content("Sol", ("Steel", 99))));                    // a delivery
        Assert.False(baseline.SameAs(Content("Sol", ("Steel", 100), ("Copper", 5))));    // a new line
        Assert.False(baseline.SameAs(baseline with { FuelMain = 12.4 }));
        Assert.False(baseline.SameAs(baseline with { NextJumpSystem = null }));
        Assert.False(baseline.SameAs(baseline with { BioSignalDetail = "other" }));
        Assert.False(baseline.SameAs(null));
    }

    [Fact]
    public void A_snapshot_is_the_same_as_itself_and_the_empty_snapshot_is_stable()
    {
        var c = Content();
        Assert.True(c.SameAs(c));
        Assert.True(OverlayContent.Empty.SameAs(new OverlayContent(null, null, 0, 0, null, 0, Array.Empty<OverlayShortfallLine>(), null)));
    }
}
