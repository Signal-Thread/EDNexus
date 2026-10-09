using System.Reflection;
using EDNexus.Plugins.Abstractions;

[assembly: PluginSdkVersion(PluginSdk.CurrentVersionString)]

namespace EDNexus.Plugins.Abstractions;

/// <summary>
/// Entry point for reasoning about the plugin SDK's own version — the contract surface
/// defined by this assembly, independent of any particular plugin or host build.
/// </summary>
public static class PluginSdk
{
    /// <summary>
    /// The SDK contract version this build of <c>EDNexus.Plugins.Abstractions</c> implements,
    /// as <c>"major.minor"</c>. Bump the major component for breaking contract changes.
    /// </summary>
    /// <remarks>
    /// 2.0: <c>IJournalEvent.Deserialize&lt;T&gt;()</c> was removed from the 1.0 contract (binding a
    /// plugin-defined type pinned the plugin's load context forever; use <c>IJournalEvent.Payload</c>).
    /// A removed member is a breaking change, so the major was bumped and the host rejects plugins
    /// built against 1.x instead of letting them fail with a <c>MissingMethodException</c> in a handler.
    /// </remarks>
    public const string CurrentVersionString = "2.0";

    /// <summary>The parsed <see cref="CurrentVersionString"/>.</summary>
    public static Version CurrentVersion { get; } = Version.Parse(CurrentVersionString);

    /// <summary>
    /// Reads the <see cref="PluginSdkVersionAttribute"/> stamped on <paramref name="assembly"/>,
    /// if any. A plugin host uses this to discover which SDK version a plugin assembly (or the
    /// SDK assembly it references) was built against, without loading any of its types.
    /// </summary>
    /// <param name="assembly">The assembly to inspect.</param>
    /// <returns>The declared version, or <see langword="null"/> if the assembly carries none.</returns>
    public static Version? GetDeclaredVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var attribute = assembly.GetCustomAttribute<PluginSdkVersionAttribute>();
        return attribute is null ? null : ParseLenient(attribute.Version);
    }

    /// <summary>
    /// Whether a plugin declaring <paramref name="declaredVersion"/> is compatible with the host's
    /// <see cref="CurrentVersion"/>. Compatibility follows semver-for-contracts: the major version
    /// must match exactly (a breaking change), and the plugin's minor version must not exceed the
    /// host's (it must not depend on members the host's SDK build doesn't have yet).
    /// </summary>
    /// <param name="declaredVersion">The SDK version a plugin was built against.</param>
    /// <returns><see langword="true"/> when the host can safely load the plugin.</returns>
    public static bool IsCompatible(Version declaredVersion)
    {
        ArgumentNullException.ThrowIfNull(declaredVersion);
        return declaredVersion.Major == CurrentVersion.Major
            && declaredVersion.Minor <= CurrentVersion.Minor;
    }

    private static Version ParseLenient(string version)
    {
        // Version.Parse requires at least "major.minor"; tolerate a bare "1" just in case.
        var text = version.Contains('.') ? version : version + ".0";
        return Version.Parse(text);
    }
}
