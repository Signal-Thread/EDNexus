using System.Reflection;
using System.Reflection.Emit;
using EDNexus.App.Services.Plugins;
using EDNexus.Plugins.Hosting;

namespace EDNexus.App.Tests;

/// <summary>Runs alone: <see cref="PluginServiceAppVersionTests"/> swaps the process-wide entry assembly.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EntryAssemblyCollection
{
    public const string Name = "Process-wide entry assembly";
}

/// <summary>
/// <see cref="PluginService"/> gates plugins on the app's version, read from the entry assembly's
/// informational version minus any <c>+build</c> suffix. <c>ResolveAppVersion</c> is internal and
/// takes no input (the test host is the entry assembly here, and its version has no suffix), so
/// these tests call it by reflection and temporarily replace the runtime's entry assembly with a
/// dynamic one carrying the version under test. That relies on a private runtime field, so the
/// fixture fails loudly, not silently, if the runtime ever drops it; it runs alone in its own
/// collection and always restores the real entry assembly.
/// </summary>
[Collection(EntryAssemblyCollection.Name)]
public sealed class PluginServiceAppVersionTests
{
    private static SemanticVersion Resolve()
    {
        var method = typeof(PluginService).GetMethod("ResolveAppVersion", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PluginService.ResolveAppVersion was renamed or removed; update this test.");
        return (SemanticVersion)method.Invoke(null, null)!;
    }

    private static SemanticVersion ResolveAs(string? informationalVersion, Version? assemblyVersion = null)
    {
        var field = typeof(Assembly).GetField("s_overriddenEntryAssembly", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("This runtime has no Assembly.s_overriddenEntryAssembly; these tests cannot substitute the entry assembly.");
        var name = new AssemblyName("EntryAssemblyProbe-" + Guid.NewGuid().ToString("N")) { Version = assemblyVersion ?? new Version(0, 0, 0, 0) };
        var attributes = informationalVersion is null
            ? []
            : new[] { new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informationalVersion]) };
        var probe = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run, attributes);
        var original = field.GetValue(null);
        field.SetValue(null, probe);
        try
        {
            Assert.Same(probe, Assembly.GetEntryAssembly());   // the substitution took effect
            return Resolve();
        }
        finally
        {
            field.SetValue(null, original);
        }
    }

    [Fact]
    public void TheRealEntryAssembly_ResolvesToAVersionWithNoBuildMetadata()
    {
        var version = Resolve();

        Assert.Null(version.Build);
    }

    [Theory]
    [InlineData("1.2.3+abc123", 1, 2, 3, null)]
    [InlineData("1.2.3+abc.def-1", 1, 2, 3, null)]
    [InlineData("2.0.0-beta.1+sha.5114f85", 2, 0, 0, "beta.1")]
    [InlineData("4.5.6", 4, 5, 6, null)]
    public void TheBuildSuffixIsStripped_AndThePreReleaseIsKept(string informational, int major, int minor, int patch, string? preRelease)
    {
        var version = ResolveAs(informational);

        Assert.Equal((major, minor, patch, preRelease), (version.Major, version.Minor, version.Patch, version.PreRelease));
        Assert.Null(version.Build);
    }

    [Theory]
    [InlineData("not a version")]
    [InlineData("1.2")]
    [InlineData("+abc")]
    [InlineData("")]
    public void AnUnparseableVersion_FallsBackToZero(string informational)
    {
        var version = ResolveAs(informational);

        Assert.Equal((0, 0, 0), (version.Major, version.Minor, version.Patch));
        Assert.False(version.IsPreRelease);
    }

    [Fact]
    public void WithoutAnInformationalVersion_TheAssemblyVersionIsTried_ButAFourPartVersionIsNotSemVer_SoItFallsBackToZero()
    {
        // The assembly version always has four parts ("3.4.5.0" here), which strict SemVer rejects, so
        // in practice this fallback only ever yields 0.0.0; the informational version is what gates.
        var version = ResolveAs(null, new Version(3, 4, 5));

        Assert.Equal((0, 0, 0), (version.Major, version.Minor, version.Patch));
    }
}
