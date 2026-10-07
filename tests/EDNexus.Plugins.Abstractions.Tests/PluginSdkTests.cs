using System.Reflection;

namespace EDNexus.Plugins.Abstractions.Tests;

public class PluginSdkTests
{
    [Fact]
    public void CurrentVersion_MatchesCurrentVersionString()
    {
        Assert.Equal(new Version(1, 0), PluginSdk.CurrentVersion);
    }

    [Fact]
    public void AssemblyIsStampedWithSdkVersionAttribute()
    {
        var version = PluginSdk.GetDeclaredVersion(typeof(PluginSdk).Assembly);

        Assert.Equal(PluginSdk.CurrentVersion, version);
    }

    [Fact]
    public void AssemblyVersion_TracksTheSdkVersion()
    {
        // The host reads the version of a plugin's EDNexus.Plugins.Abstractions reference from its
        // metadata and treats major.minor as the SDK version it was built against.
        var assemblyVersion = typeof(PluginSdk).Assembly.GetName().Version!;

        Assert.Equal(PluginSdk.CurrentVersion.Major, assemblyVersion.Major);
        Assert.Equal(PluginSdk.CurrentVersion.Minor, assemblyVersion.Minor);
    }

    [Fact]
    public void GetDeclaredVersion_ReturnsNull_WhenAssemblyHasNoAttribute()
    {
        // This test assembly itself doesn't carry a PluginSdkVersionAttribute.
        var version = PluginSdk.GetDeclaredVersion(Assembly.GetExecutingAssembly());

        Assert.Null(version);
    }

    [Fact]
    public void GetDeclaredVersion_ThrowsOnNullAssembly()
    {
        Assert.Throws<ArgumentNullException>(() => PluginSdk.GetDeclaredVersion(null!));
    }

    [Theory]
    [InlineData(1, 0, true)] // exact match
    [InlineData(1, 1, false)] // plugin built against a newer minor than the host implements
    [InlineData(2, 0, false)] // breaking major bump
    [InlineData(0, 9, false)] // older major is a mismatch too — majors must match exactly
    public void IsCompatible_ChecksMajorExactAndMinorNotNewer(int major, int minor, bool expected)
    {
        Assert.Equal(expected, PluginSdk.IsCompatible(new Version(major, minor)));
    }

    [Fact]
    public void IsCompatible_ThrowsOnNullVersion()
    {
        Assert.Throws<ArgumentNullException>(() => PluginSdk.IsCompatible(null!));
    }
}
