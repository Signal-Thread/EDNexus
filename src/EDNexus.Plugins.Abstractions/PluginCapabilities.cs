namespace EDNexus.Plugins.Abstractions;

/// <summary>
/// The capability strings a plugin may declare in the <c>capabilities</c> array of its
/// <c>plugin.json</c>. A manifest that declares anything not in <see cref="All"/> is rejected,
/// so a typo can never silently lose (or gain) a permission.
/// </summary>
public static class PluginCapabilities
{
    /// <summary>
    /// Subscribe to the journal event feed (<see cref="IPluginContext.Events"/>). Data-equivalent
    /// to <see cref="State"/>: the feed carries everything the commander state is derived from.
    /// </summary>
    public const string Events = "events";

    /// <summary>
    /// Read the commander state (<see cref="IPluginContext.State"/>). This is not live: it is the
    /// state as of the last completed journal event.
    /// </summary>
    public const string State = "state";

    /// <summary>Contribute widgets to the dashboard (<see cref="IPluginContext.Ui"/>).</summary>
    public const string UiDashboard = "ui.dashboard";

    /// <summary>Contribute panels to the in-game overlay (<see cref="IPluginContext.Ui"/>).</summary>
    public const string UiOverlay = "ui.overlay";

    /// <summary>Persist data in the plugin's scoped storage (<see cref="IPluginContext.Storage"/>).</summary>
    public const string Storage = "storage";

    /// <summary>
    /// Make network calls. Not enforceable in-process, but plugins that phone home must declare it
    /// so the user is warned before enabling them.
    /// </summary>
    public const string Network = "network";

    /// <summary>Every capability this SDK version understands.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Events, State, UiDashboard, UiOverlay, Storage, Network,
    };

    /// <summary>Whether <paramref name="capability"/> is a known capability (exact, case-sensitive).</summary>
    public static bool IsKnown(string? capability) => capability is not null && All.Contains(capability);
}
