using System.Diagnostics;
using EDNexus.Core.Settings;
using EDNexus.Plugins.Hosting;

namespace EDNexus.App.Services.Plugins;

/// <summary>
/// Drops the commander's persisted choices (consent grants, disable flags) for plugins that are no
/// longer installed. Nothing in the app calls <see cref="PluginRuntime.Uninstalled"/> when a folder
/// is deleted by hand, so without this a grant would outlive its plugin indefinitely.
/// </summary>
/// <remarks>
/// <para>
/// Grants are bound to the plugin's code, so a stale grant cannot be inherited by different code
/// anyway; pruning keeps <c>settings.json</c> honest and a reinstall starting from the default.
/// </para>
/// <para>
/// <b>Safety.</b> Pruning is destructive and the file system can lie transiently, so it does
/// <em>nothing</em> unless it positively saw a plugins root with at least one plugin folder in it:
/// a missing root (unplugged drive), a root that cannot be enumerated (permissions, network share)
/// and an empty root (an unmounted share leaves its empty mount point behind) are all
/// indistinguishable from "everything is gone" and are skipped, with a log line. The cost of
/// skipping is a stale entry that is harmlessly removed on a later run; the cost of a wrong prune
/// is the commander re-approving their plugins. Only folders directly under the root count as
/// "installed"; hidden installer work folders (<c>.staging-*</c>, <c>.replaced-*</c>) do not.
/// </para>
/// </remarks>
public static class PluginChoicePruner
{
    /// <summary>
    /// Removes the grants and disable flags of every plugin id with no folder under
    /// <paramref name="pluginsRoot"/>. Never throws. Returns the ids that were pruned.
    /// </summary>
    /// <param name="settings">Where the ids with saved choices are read from.</param>
    /// <param name="pluginsRoot">The plugins root.</param>
    /// <param name="grants">Where grants are forgotten.</param>
    /// <param name="enablement">Where disable flags are cleared.</param>
    /// <param name="enumerateDirectories">Lists the directories directly under a path (default <see cref="Directory.EnumerateDirectories(string)"/>); tests inject a failing one.</param>
    public static IReadOnlyList<string> Prune(
        AppSettings settings, string pluginsRoot, IPluginGrantStore grants, IPluginEnablementStore enablement,
        Func<string, IEnumerable<string>>? enumerateDirectories = null)
    {
        enumerateDirectories ??= static path => Directory.EnumerateDirectories(path);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(enablement);
        var pruned = new List<string>();
        try
        {
            var plugins = settings.Plugins;
            var known = (plugins?.Grants?.Keys ?? Enumerable.Empty<string>())
                .Concat(plugins?.DisabledIds ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (known.Count == 0)
                return pruned;

            if (!TryListInstalled(pluginsRoot, enumerateDirectories, out var installed))
                return pruned;

            foreach (var id in known)
            {
                if (installed.Contains(id))
                    continue;
                try
                {
                    grants.Remove(id);
                    enablement.SetDisabled(id, false);
                    pruned.Add(id);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Plugins: could not forget the choices for uninstalled plugin '{PluginLogText.Sanitise(id, 200)}': {PluginLogText.Sanitise(ex.Message)}");
                }
            }
            if (pruned.Count > 0)
                Trace.TraceInformation($"Plugins: forgot the saved choices of {pruned.Count} uninstalled plugin(s).");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Plugins: pruning saved plugin choices failed: {PluginLogText.Sanitise(ex.Message)}");
        }
        return pruned;
    }

    private static bool TryListInstalled(string pluginsRoot, Func<string, IEnumerable<string>> enumerateDirectories, out HashSet<string> installed)
    {
        installed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(pluginsRoot))
            {
                Trace.TraceInformation($"Plugins: '{PluginLogText.Sanitise(pluginsRoot)}' does not exist; saved plugin choices were left alone.");
                return false;
            }
            foreach (var dir in enumerateDirectories(pluginsRoot))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith('.'))
                    installed.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.TraceWarning($"Plugins: '{PluginLogText.Sanitise(pluginsRoot)}' could not be listed ({PluginLogText.Sanitise(ex.Message)}); saved plugin choices were left alone.");
            return false;
        }
        if (installed.Count == 0)
        {
            Trace.TraceInformation($"Plugins: '{PluginLogText.Sanitise(pluginsRoot)}' holds no plugin folders; saved plugin choices were left alone (it may be an unmounted drive).");
            return false;
        }
        return true;
    }
}
