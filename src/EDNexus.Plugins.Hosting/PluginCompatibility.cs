using System.Globalization;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting;

/// <summary>
/// Gates a validated <see cref="PluginManifest"/> against the running host: its declared
/// <see cref="PluginManifest.SdkVersion"/> must be compatible with the host's SDK contract
/// (<see cref="PluginSdk.IsCompatible"/>), and the app must be at least
/// <see cref="PluginManifest.MinAppVersion"/>.
/// </summary>
public static class PluginCompatibility
{
    /// <summary>
    /// Checks <paramref name="manifest"/> against <paramref name="appVersion"/> and the current
    /// SDK. Returns <see langword="null"/> when compatible, otherwise a human-readable reason.
    /// Never throws for malformed manifest versions (they are reported as the reason).
    /// </summary>
    /// <param name="manifest">A manifest produced by <see cref="PluginManifestParser"/>.</param>
    /// <param name="appVersion">The running EDNexus version.</param>
    public static string? Check(PluginManifest manifest, SemanticVersion appVersion)
        => Check(manifest, appVersion, PluginSdk.CurrentVersion);

    /// <summary>As <see cref="Check(PluginManifest, SemanticVersion)"/>, against an explicit host SDK version.</summary>
    public static string? Check(PluginManifest manifest, SemanticVersion appVersion, Version hostSdkVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(appVersion);
        ArgumentNullException.ThrowIfNull(hostSdkVersion);

        if (!TryParseSdkVersion(manifest.SdkVersion, out var declared))
            return $"plugin declares an unreadable SDK version \"{manifest.SdkVersion}\"";

        if (declared.Major != hostSdkVersion.Major || declared.Minor > hostSdkVersion.Minor)
            return $"plugin targets SDK {declared.Major}.{declared.Minor}, but this EDNexus provides SDK {hostSdkVersion.Major}.{hostSdkVersion.Minor}";

        if (manifest.MinAppVersion is { } min)
        {
            if (!SemanticVersion.TryParse(min, out var minVersion))
                return $"plugin declares an unreadable minimum app version \"{min}\"";
            if (appVersion < minVersion!)
                return $"plugin requires EDNexus {minVersion} or newer (this is {appVersion})";
        }

        return null;
    }

    /// <summary>
    /// Checks the SDK version the entry assembly was actually compiled against (the version of its
    /// <c>EDNexus.Plugins.Abstractions</c> reference, read from metadata) against the host's, so a
    /// manifest that understates its <c>sdkVersion</c> cannot get a plugin that needs newer members
    /// than the host has past <see cref="Check(PluginManifest, SemanticVersion, Version)"/>. Same rule
    /// as <see cref="PluginSdk.IsCompatible"/>: major equal, plugin's minor not above the host's.
    /// Returns <see langword="null"/> when compatible, otherwise a reason.
    /// </summary>
    public static string? CheckBuiltAgainst(Version referencedSdkAssemblyVersion, Version hostSdkVersion)
    {
        ArgumentNullException.ThrowIfNull(referencedSdkAssemblyVersion);
        ArgumentNullException.ThrowIfNull(hostSdkVersion);
        if (referencedSdkAssemblyVersion.Major == hostSdkVersion.Major && referencedSdkAssemblyVersion.Minor <= hostSdkVersion.Minor)
            return null;
        return $"the entry assembly was built against SDK {referencedSdkAssemblyVersion.Major}.{referencedSdkAssemblyVersion.Minor} "
            + $"(whatever its manifest declares), but this EDNexus provides SDK {hostSdkVersion.Major}.{hostSdkVersion.Minor}";
    }

    private static bool TryParseSdkVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
            return false;
        version = new Version(major, minor);
        return true;
    }
}
