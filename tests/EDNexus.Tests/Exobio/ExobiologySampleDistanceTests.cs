using System.Linq;
using EDNexus.Core.Exobio;
using Xunit;

namespace EDNexus.Tests.Exobio;

/// <summary>
/// Regression pins for the genus colony ranges. The numbers below were compared by hand on 2026-11-01
/// with the cited wiki page (see Exobio/Data/SOURCES.md); this test cannot re-check the source, it only
/// stops the catalog drifting from what was checked. Radicoida (100 m in the catalog) is intentionally
/// absent: the wiki says "?" for it, so that value has no source at all.
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
    public void Catalog_colony_ranges_still_equal_the_values_checked_against_the_wiki(string genus, int metres)
    {
        var g = ExobiologyCatalog.Default.Genera.Single(x => x.Name == genus);
        Assert.Equal(metres, g.SampleDistanceMeters);
    }
}
