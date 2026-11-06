namespace EDNexus.Plugins.Hosting;

/// <summary>
/// Locates the per-user plugins root. Plugins live beside the settings file, in an
/// <c>EDNexus/plugins</c> folder under whatever .NET reports as
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> (<c>%LOCALAPPDATA%\EDNexus\plugins</c>
/// on Windows, <c>~/.local/share/EDNexus/plugins</c> on Linux) — never in the install directory,
/// which stays read-only. Each installed plugin is a folder <c>&lt;root&gt;/&lt;id&gt;/</c>.
/// </summary>
/// <remarks>
/// <b>macOS is unverified (tracked in #259).</b> The code does not special-case it: the location is
/// whatever .NET returns there, which has not been checked on a Mac. It is expected, but not
/// confirmed, to be under <c>~/Library/Application Support</c>. Set <see cref="OverrideEnvVar"/>
/// rather than rely on it.
/// </remarks>
public static class PluginPaths
{
    /// <summary>
    /// Set this environment variable to an <em>absolute</em> path to use a different plugins
    /// folder (dev/testing). Relative values are ignored. (This project cannot see
    /// <c>EDNexus.Core.FeatureFlags</c>; a host that wants to restrict the override to developer
    /// builds should pass its own lookup to <see cref="Resolve(Func{string, string?})"/>.)
    /// </summary>
    public const string OverrideEnvVar = "EDNEXUS_PLUGINS_DIR";

    /// <summary>Name of the plugins folder under the EDNexus app-data folder.</summary>
    public const string FolderName = "plugins";

    /// <summary>File extension of a packaged plugin (a zip of the plugin folder).</summary>
    public const string PackageExtension = ".ednplugin";

    /// <summary>
    /// The plugins root: <see cref="OverrideEnvVar"/> when set (it need not exist yet), otherwise
    /// <see cref="DefaultRoot"/>. Always an absolute path, or <see langword="null"/> when no per-user
    /// data folder can be determined — the host then runs with no plugins rather than falling back
    /// to a shared location (such as the temp folder) that another user could write to.
    /// </summary>
    public static string? Resolve() => Resolve(Environment.GetEnvironmentVariable);

    /// <summary>As <see cref="Resolve()"/>, with an injectable environment lookup for tests.</summary>
    public static string? Resolve(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var overridden = getEnvironmentVariable(OverrideEnvVar)?.Trim();
        // Only an absolute override is honoured: a relative one would resolve against whatever the
        // current directory happens to be (possibly the install dir or a shared folder), so
        // plugins could silently load from an unexpected place. Anything unusable falls back to
        // the default rather than breaking startup.
        if (!string.IsNullOrEmpty(overridden) && Path.IsPathFullyQualified(overridden))
        {
            try
            {
                return Path.GetFullPath(overridden);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        return DefaultRoot();
    }

    /// <summary>
    /// The default plugins root under the per-user local app-data folder, alongside
    /// <c>settings.json</c> (see <c>EDNexus.Core.Settings.SettingsStore.DefaultPath</c>), or
    /// <see langword="null"/> when the OS reports no per-user data or home folder.
    /// </summary>
    public static string? DefaultRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            // Some minimal Linux environments have no XDG mapping; use ~/.local/share if HOME is known.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                return null;
            appData = Path.Combine(home, ".local", "share");
        }
        return Path.GetFullPath(Path.Combine(appData, "EDNexus", FolderName));
    }

    /// <summary>The install folder for plugin <paramref name="id"/> under <paramref name="root"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid plugin id.</exception>
    public static string PluginDirectory(string root, string id)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!PluginManifestParser.IsValidId(id))
            throw new ArgumentException($"'{id}' is not a valid plugin id.", nameof(id));
        return Path.Combine(Path.GetFullPath(root), id);
    }
}
