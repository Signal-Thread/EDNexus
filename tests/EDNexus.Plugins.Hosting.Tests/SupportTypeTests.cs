using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting.Tests;

public class SemanticVersionTests
{
    [Fact]
    public void Parse_ExposesComponents()
    {
        var v = SemanticVersion.Parse("1.2.3-beta.4+build.5");

        Assert.Equal(1, v.Major);
        Assert.Equal(2, v.Minor);
        Assert.Equal(3, v.Patch);
        Assert.Equal("beta.4", v.PreRelease);
        Assert.Equal("build.5", v.Build);
        Assert.True(v.IsPreRelease);
        Assert.Equal("1.2.3-beta.4+build.5", v.ToString());
    }

    [Fact]
    public void Parse_Invalid_Throws_TryParse_DoesNot()
    {
        Assert.Throws<FormatException>(() => SemanticVersion.Parse("1.0"));
        Assert.False(SemanticVersion.TryParse(null, out var v));
        Assert.Null(v);
        Assert.False(SemanticVersion.TryParse(new string('1', 500) + ".0.0", out _));
    }

    [Fact]
    public void Precedence_FollowsSemVerSpec()
    {
        // The example ordering from semver.org §11.
        string[] ordered =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
            "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0",
        ];

        for (var i = 1; i < ordered.Length; i++)
            Assert.True(SemanticVersion.Parse(ordered[i - 1]) < SemanticVersion.Parse(ordered[i]), $"{ordered[i - 1]} < {ordered[i]}");
    }

    [Theory]
    [InlineData("1.0.0\n")]
    [InlineData("1.0.0-beta\n")]
    [InlineData("1.0.0+build\n")]
    [InlineData("\u0661.0.0")]      // Arabic-Indic digit one: \d would match it
    [InlineData("1.\u0660.0")]
    [InlineData("1.0.0-\u0661")]
    public void TryParse_RejectsTrailingNewlineAndNonAsciiDigits(string text)
        => Assert.False(SemanticVersion.TryParse(text, out _));

    [Fact]
    public void Precedence_HugeNumericPreReleaseIds_CompareNumerically()
    {
        // Both overflow ulong; they must still compare as numbers (by length, then digits),
        // and still sort below alphanumeric identifiers.
        var smaller = SemanticVersion.Parse("1.0.0-99999999999999999999999");
        var larger = SemanticVersion.Parse("1.0.0-100000000000000000000000");

        Assert.True(smaller < larger);
        Assert.True(SemanticVersion.Parse("1.0.0-2") < smaller);
        Assert.True(larger < SemanticVersion.Parse("1.0.0-alpha"));
        Assert.Equal(SemanticVersion.Parse("1.0.0-123456789012345678901234567890"), SemanticVersion.Parse("1.0.0-123456789012345678901234567890"));
    }

    [Fact]
    public void BuildMetadata_IsIgnoredForEquality()
    {
        Assert.Equal(SemanticVersion.Parse("1.0.0+a"), SemanticVersion.Parse("1.0.0+b"));
        Assert.True(SemanticVersion.Parse("1.0.0+a") >= SemanticVersion.Parse("1.0.0"));
    }
}

public class PluginPathRulesTests
{
    [Theory]
    [InlineData("plugin.json")]
    [InlineData("lib/net10.0/Dep.dll")]
    [InlineData("runtimes/linux-x64/native/libfoo.so")]
    [InlineData(".hidden")]
    [InlineData("a b/c.txt")]
    public void SafePaths_AreAccepted(string path)
        => Assert.Null(PluginPathRules.CheckRelativePath(path));

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("//server/share/x")]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("a/./x")]
    [InlineData("a//x")]
    [InlineData("a\\x")]
    [InlineData("C:/x")]
    [InlineData("x:ads")]
    [InlineData("a/b.")]
    [InlineData("a/b ")]
    [InlineData(" a")]
    [InlineData("CON")]
    [InlineData("lib/nul.dll")]
    [InlineData("COM1.txt")]
    [InlineData("lpt9")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a|b")]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    [InlineData("evil\u202Elld.exe")]   // RTL override: displays as "evilexe.dll"
    [InlineData("a\u200Bb.dll")]         // zero-width space
    public void UnsafePaths_AreRejected(string path)
        => Assert.NotNull(PluginPathRules.CheckRelativePath(path));

    [Fact]
    public void UnpairedSurrogate_IsRejected()
    {
        // Built at runtime: attribute arguments are stored as UTF-8 and can't carry a lone surrogate.
        var path = "a" + (char)0xD800 + "b.dll";
        Assert.Contains("unpaired surrogate", PluginPathRules.CheckRelativePath(path));
    }

    [Fact]
    public void VisibleNonAsciiNames_AreAccepted()
        => Assert.Null(PluginPathRules.CheckRelativePath("lib/fr/Ressources.caf\u00E9.dll"));

    [Fact]
    public void TooLongOrTooDeep_IsRejected()
    {
        Assert.NotNull(PluginPathRules.CheckRelativePath(new string('a', PluginPathRules.MaxRelativePathLength + 1)));
        Assert.NotNull(PluginPathRules.CheckRelativePath(string.Join('/', Enumerable.Repeat("a", PluginPathRules.MaxDepth + 1))));
    }

    [Fact]
    public void TrailingSlash_OnlyAllowedWhenRequested()
    {
        Assert.Null(PluginPathRules.CheckRelativePath("lib/", allowTrailingSlash: true));
        Assert.NotNull(PluginPathRules.CheckRelativePath("lib/"));
        Assert.NotNull(PluginPathRules.CheckRelativePath("/", allowTrailingSlash: true));
    }

    [Fact]
    public void Normalize_TurnsBackslashTraversalIntoDetectableForm()
    {
        var normalized = PluginPathRules.Normalize("..\\..\\evil.dll");
        Assert.Equal("../../evil.dll", normalized);
        Assert.Contains("traversal", PluginPathRules.CheckRelativePath(normalized));
    }

    [Fact]
    public void ResolveInside_ContainsPathsToTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");

        Assert.Equal(Path.Combine(root, "a", "b.dll"), PluginPathRules.ResolveInside(root, "a/b.dll"));
        Assert.Null(PluginPathRules.ResolveInside(root, "../evil.dll"));
        Assert.Null(PluginPathRules.ResolveInside(root, "a/../../evil.dll"));
        Assert.Null(PluginPathRules.ResolveInside(root, ""));
        Assert.Null(PluginPathRules.ResolveInside(root, "../root-sibling/x.dll")); // prefix-match trap
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "x.dll");
        Assert.Null(PluginPathRules.ResolveInside(root, absolute));
    }
}

public class PluginCompatibilityTests
{
    private static PluginManifest Manifest(string sdk = PluginSdk.CurrentVersionString, string? minApp = null)
        => new("a.b", "N", "1.0.0", sdk) { MinAppVersion = minApp, EntryAssembly = "A.dll", EntryType = "A.P" };

    private static readonly SemanticVersion App = SemanticVersion.Parse("1.5.0");

    [Fact]
    public void CurrentSdk_IsCompatible()
        => Assert.Null(PluginCompatibility.Check(Manifest(PluginSdk.CurrentVersionString), App));

    [Theory]
    [InlineData("2.0", 2, 0, true)]
    [InlineData("2.0", 2, 3, true)]   // host has a newer minor — additive, fine
    [InlineData("2.4", 2, 3, false)]  // plugin needs members the host lacks
    [InlineData("3.0", 2, 9, false)]  // breaking major
    [InlineData("1.0", 2, 0, false)]  // a 1.x plugin: 2.0 removed IJournalEvent.Deserialize<T>
    [InlineData("0.9", 2, 0, false)]
    public void SdkVersion_IsGatedByMajorAndMinor(string declared, int hostMajor, int hostMinor, bool compatible)
    {
        var reason = PluginCompatibility.Check(Manifest(declared), App, new Version(hostMajor, hostMinor));
        Assert.Equal(compatible, reason is null);
        if (!compatible) Assert.Contains("SDK", reason);
    }

    [Theory]
    [InlineData("1.5.0", true)]
    [InlineData("1.4.9", true)]
    [InlineData("1.5.1", false)]
    [InlineData("1.5.0-rc.1", true)]
    [InlineData("2.0.0", false)]
    public void MinAppVersion_IsEnforced(string minApp, bool compatible)
    {
        var reason = PluginCompatibility.Check(Manifest(minApp: minApp), App);
        Assert.Equal(compatible, reason is null);
        if (!compatible) Assert.Contains("requires EDNexus", reason);
    }

    [Fact]
    public void UnreadableVersions_AreReportedNotThrown()
    {
        Assert.Contains("unreadable SDK", PluginCompatibility.Check(Manifest("banana"), App));
        Assert.Contains("unreadable minimum", PluginCompatibility.Check(Manifest(minApp: "soon"), App));
    }
}

public class PluginPathsTests
{
    [Fact]
    public void DefaultRoot_IsUnderUserAppData_NotTheInstallDirectory()
    {
        var root = PluginPaths.DefaultRoot();

        Assert.NotNull(root);
        Assert.True(Path.IsPathFullyQualified(root));
        Assert.Equal("plugins", Path.GetFileName(root));
        Assert.Equal("EDNexus", Path.GetFileName(Path.GetDirectoryName(root)));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(appData))
            Assert.StartsWith(Path.GetFullPath(appData), root, StringComparison.OrdinalIgnoreCase);
        Assert.False(root.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_UsesEnvOverride_EvenIfItDoesNotExistYet()
    {
        var custom = Path.Combine(Path.GetTempPath(), "ednexus-dev-plugins-" + Guid.NewGuid().ToString("N"));

        var root = PluginPaths.Resolve(name => name == PluginPaths.OverrideEnvVar ? custom : null);

        Assert.Equal(Path.GetFullPath(custom), root);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankOverride_FallsBackToDefault(string? value)
        => Assert.Equal(PluginPaths.DefaultRoot(), PluginPaths.Resolve(_ => value));

    [Theory]
    [InlineData("dev-plugins")]
    [InlineData("./plugins")]
    [InlineData("../plugins")]
    public void Resolve_RelativeOverride_IsIgnored(string value)
        => Assert.Equal(PluginPaths.DefaultRoot(), PluginPaths.Resolve(_ => value));

    [Fact]
    public void Resolve_OverrideWithSurroundingWhitespace_IsTrimmed()
    {
        var custom = Path.Combine(Path.GetTempPath(), "ednexus-dev-plugins");
        Assert.Equal(Path.GetFullPath(custom), PluginPaths.Resolve(_ => "  " + custom + "  "));
    }

    [Fact]
    public void PluginDirectory_IsRootPlusId_AndRejectsBadIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "plugins");

        Assert.Equal(Path.Combine(root, "com.acme.x"), PluginPaths.PluginDirectory(root, "com.acme.x"));
        Assert.Throws<ArgumentException>(() => PluginPaths.PluginDirectory(root, "../evil"));
        Assert.Throws<ArgumentException>(() => PluginPaths.PluginDirectory(root, "Com.Acme"));
    }
}
