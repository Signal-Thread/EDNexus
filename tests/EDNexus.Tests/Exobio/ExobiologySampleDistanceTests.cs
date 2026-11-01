using System.Linq;
using EDNexus.Core.Exobio;
using Xunit;

namespace EDNexus.Tests.Exobio;

/// <summary>
/// Pins the genus colony ranges that were checked against a cited source (see Exobio/Data/SOURCES.md).
/// Radicoida is intentionally absent: no source confirms its range yet.
/// </summary>
public class ExobiologySampleDistanceTests
{
    [Theory]
    [InlineData("Aleoida", 150)]
    [InlineData("Amphora Plant", 100)]
    [InlineData("Anemone", 100)]
    [InlineData("Bacterium", 500)]
    [InlineData("Bark Mound", 100)]
    [InlineData("Brain Tree", 100)]
    [InlineData("Cactoida", 300)]
    [InlineData("Clypeus", 150)]
    [InlineData("Concha", 150)]
    [InlineData("Crystalline Shards", 100)]
    [InlineData("Electricae", 1000)]
    [InlineData("Fonticulua", 500)]
    [InlineData("Frutexa", 150)]
    [InlineData("Fumerola", 100)]
    [InlineData("Fungoida", 300)]
    [InlineData("Osseus", 800)]
    [InlineData("Recepta", 150)]
    [InlineData("Sinuous Tubers", 100)]
    [InlineData("Stratum", 500)]
    [InlineData("Tubus", 800)]
    [InlineData("Tussock", 200)]
    public void Verified_genus_colony_ranges_match_the_cited_source(string genus, int metres)
    {
        var g = ExobiologyCatalog.Default.Genera.Single(x => x.Name == genus);
        Assert.Equal(metres, g.SampleDistanceMeters);
    }
}
