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
    /// to <see cref="State"/>: the feed carries everything the commander state is derived from,
    /// including the commander's name and credit balance (the <c>LoadGame</c>, <c>Commander</c> and
    /// <c>Statistics</c> events). Consent text must say so; see <see cref="State"/>.
    /// </summary>
    public const string Events = "events";

    /// <summary>
    /// Read the commander state (<see cref="IPluginContext.State"/>). This is not live: it is the
    /// state as of the last completed journal event. <b>It includes the commander's in-game name
    /// (<see cref="IReadOnlyCommanderState.Name"/>) and credit balance
    /// (<see cref="IReadOnlyCommanderState.Balance"/>)</b>, plus location, ship, cargo and
    /// materials; there is no narrower capability for identity or credits. A plugin that declares
    /// <c>state</c> (or <c>events</c>) together with <see cref="Network"/> can send all of it off
    /// the machine, so the user must be told exactly that before granting the pair.
    /// </summary>
    public const string State = "state";

    /// <summary>
    /// Contribute widgets to the dashboard (<see cref="IPluginContext.Ui"/>). The host has no UI
    /// contribution points yet: until it does, the bridge's UI registry refuses every call.
    /// </summary>
    public const string UiDashboard = "ui.dashboard";

    /// <summary>
    /// Contribute panels to the in-game overlay (<see cref="IPluginContext.Ui"/>). See
    /// <see cref="UiDashboard"/>: not implemented by the bridge yet.
    /// </summary>
    public const string UiOverlay = "ui.overlay";

    /// <summary>
    /// Persist data in the plugin's scoped storage (<see cref="IPluginContext.Storage"/>). The host
    /// has no storage backend yet: until it does, the bridge's storage refuses every call.
    /// </summary>
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
