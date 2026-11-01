using EDNexus.Core.Settings;

namespace EDNexus.Plugins.Hosting;

/// <summary>Where a plugin is in its lifecycle, as shown to the commander.</summary>
public enum PluginState
{
    /// <summary>Loaded, initialised and receiving events.</summary>
    Enabled,

    /// <summary>Switched off by the commander; none of its code is loaded. The choice persists.</summary>
    Disabled,

    /// <summary>
    /// Unloaded automatically because calls into its code kept failing
    /// (see <see cref="PluginFaultPolicy"/>). Lasts until the commander enables or reloads it, or
    /// the app restarts; it is not persisted.
    /// </summary>
    Quarantined,

    /// <summary>It could not be loaded (bad manifest, incompatible, denied, <c>Initialize</c> threw, ...).</summary>
    Failed,
}

/// <summary>
/// How many failed calls into a plugin's code are tolerated before it is quarantined: more than
/// <see cref="Threshold"/> failures inside the rolling <see cref="Window"/>.
/// </summary>
public sealed record PluginFaultPolicy
{
    /// <summary>The number of failures within <see cref="Window"/> that is tolerated; one more quarantines.</summary>
    public int Threshold { get; init; } = 10;

    /// <summary>The rolling window failures are counted over.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>A point-in-time view of one plugin for the management UI.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="Name">The manifest's display name, when the manifest was readable.</param>
/// <param name="State">Where the plugin is in its lifecycle.</param>
/// <param name="LastError">The most recent failure (text only, never an exception object), or null.</param>
/// <param name="LastErrorAt">When <paramref name="LastError"/> happened.</param>
/// <param name="TotalFaults">Failed calls into the plugin since it was last (re)loaded.</param>
public sealed record PluginStatus(
    string Id,
    string? Name,
    PluginState State,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    long TotalFaults);

/// <summary>Persists which plugins the commander has disabled.</summary>
public interface IPluginEnablementStore
{
    /// <summary>Whether the commander has disabled <paramref name="pluginId"/>.</summary>
    bool IsDisabled(string pluginId);

    /// <summary>Records the commander's choice.</summary>
    void SetDisabled(string pluginId, bool disabled);
}

/// <summary>An in-memory store (tests, and hosts that don't persist).</summary>
public sealed class InMemoryPluginEnablementStore : IPluginEnablementStore
{
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);

    public bool IsDisabled(string pluginId) { lock (_disabled) return _disabled.Contains(pluginId); }

    public void SetDisabled(string pluginId, bool disabled)
    {
        lock (_disabled)
        {
            if (disabled) _disabled.Add(pluginId); else _disabled.Remove(pluginId);
        }
    }
}

/// <summary>
/// Persists the choice in <see cref="AppSettings.Plugins"/> and asks <paramref name="save"/> (e.g.
/// <c>SettingsStore.TrySave</c>) to write it, so it survives a restart.
/// </summary>
public sealed class AppSettingsPluginEnablementStore(AppSettings settings, Action<AppSettings> save) : IPluginEnablementStore
{
    private readonly object _gate = new();

    public bool IsDisabled(string pluginId)
    {
        lock (_gate) return settings.Plugins.DisabledIds.Contains(pluginId);
    }

    public void SetDisabled(string pluginId, bool disabled)
    {
        lock (_gate)
        {
            var changed = disabled ? settings.Plugins.DisabledIds.Add(pluginId) : settings.Plugins.DisabledIds.Remove(pluginId);
            if (changed)
                save(settings);
        }
    }
}
