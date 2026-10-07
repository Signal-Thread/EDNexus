using System.Globalization;
using EDNexus.Core.Settings;
using Xunit;

namespace EDNexus.Tests.Settings;

public class CreditsInputTests
{
    [Theory]
    [InlineData("50000", 50000)]
    [InlineData("  50000  ", 50000)]
    [InlineData("1,500,000", 1_500_000)]
    [InlineData("0", 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData(null, 0)]
    public void Accepts_whole_numbers_with_or_without_thousands_separators_and_blank(string? text, int expected)
    {
        using var _ = new CultureScope("en-US");

        Assert.True(CreditsInput.TryParse(text, out var credits));
        Assert.Equal(expected, credits);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("50k")]
    [InlineData("-100")]
    [InlineData("99999999999")]
    [InlineData("1 500 000")]
    public void Rejects_anything_that_is_not_a_non_negative_whole_number_that_fits(string text)
    {
        using var _ = new CultureScope("en-US");

        Assert.False(CreditsInput.TryParse(text, out var credits));
        Assert.Equal(0, credits);
    }

    [Fact]
    public void Understands_the_separator_of_the_current_culture()
    {
        using var _ = new CultureScope("de-DE");

        Assert.True(CreditsInput.TryParse("1.500.000", out var credits));
        Assert.Equal(1_500_000, credits);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
