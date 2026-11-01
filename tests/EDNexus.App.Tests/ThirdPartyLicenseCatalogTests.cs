using System.Text;
using System.Xml.Linq;
using EDNexus.App.Services;

namespace EDNexus.App.Tests;

public class ThirdPartyLicenseCatalogTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EDNexus.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("EDNexus.slnx not found above the test output.");
    }

    private static IReadOnlyList<ThirdPartyLicense> LoadShipped()
    {
        using var s = File.OpenRead(Path.Combine(RepoRoot(), "src", "EDNexus.App", "Assets", "third-party-licenses.json"));
        return ThirdPartyLicenseCatalog.Parse(s);
    }

    [Fact]
    public void Every_shipped_entry_has_a_real_licence()
    {
        var all = LoadShipped();
        Assert.NotEmpty(all);
        Assert.DoesNotContain(all, l => l.License.StartsWith("UNKNOWN", StringComparison.OrdinalIgnoreCase)
                                     || l.License.StartsWith("SEE FILE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_PackageReference_of_the_shipped_projects_is_listed()
    {
        // Fails when a dependency is added without re-running tools/Update-ThirdPartyLicenses.ps1.
        var listed = LoadShipped().Where(l => l.Kind == "package").Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var proj in new[] { "EDNexus.App", "EDNexus.Core" })
        {
            var csproj = XDocument.Load(Path.Combine(RepoRoot(), "src", proj, proj + ".csproj"));
            foreach (var p in csproj.Descendants("PackageReference"))
            {
                var id = (string?)p.Attribute("Include");
                // The macOS-only engine is restored only when building for macOS, so the Windows-generated list lacks it.
                if (id is not null && id != "VideoLAN.LibVLC.Mac" && !listed.Contains(id)) missing.Add(id);
            }
        }
        Assert.True(missing.Count == 0, "Not in third-party-licenses.json: " + string.Join(", ", missing));
    }

    [Fact]
    public void EDAstro_is_credited_and_not_presented_as_MIT()
    {
        var e = Assert.Single(LoadShipped(), l => l.Name == "EDAstro");
        Assert.Equal("data", e.Kind);
        Assert.StartsWith("CC-BY-NC-SA", e.License);
    }

    [Fact]
    public void Malformed_entries_are_skipped_not_fatal()
    {
        const string json = """
            { "entries": [ 42, { "name": "NoLicence" }, { "name": "Good", "license": "MIT", "url": "javascript:alert(1)" },
                           { "kind": "data", "name": "Svc", "license": "Terms", "url": "https://example.com/x" } ] }
            """;
        var list = ThirdPartyLicenseCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(2, list.Count);
        Assert.Null(list.Single(l => l.Name == "Good").SafeUrl);
        Assert.Equal("https://example.com/x", list.Single(l => l.Name == "Svc").SafeUrl);
    }

    [Fact]
    public void Garbage_root_yields_an_empty_list()
    {
        Assert.Empty(ThirdPartyLicenseCatalog.Parse(new MemoryStream("[]"u8.ToArray())));
    }
}
