namespace EDNexus.Plugins.Abstractions;

/// <summary>
/// Marks the version of the plugin SDK contract that an assembly was built against.
/// The host stamps this attribute on <c>EDNexus.Plugins.Abstractions</c> itself
/// (see <see cref="PluginSdk.CurrentVersion"/>) so a plugin loader can read it via
/// reflection and refuse to load a plugin built against an incompatible major version,
/// without the host or the plugin needing a compile-time reference to each other.
/// </summary>
/// <param name="version">
/// The SDK contract version, formatted as <c>"major.minor"</c> (e.g. <c>"2.0"</c>).
/// </param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PluginSdkVersionAttribute(string version) : Attribute
{
    /// <summary>The SDK contract version this assembly declares, e.g. <c>"2.0"</c>.</summary>
    public string Version { get; } = version;
}
