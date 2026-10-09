namespace EDNexus.Core.Overlay;

/// <summary>
/// When the overlay should actually be on screen. The overlay is a topmost window, so left on all the
/// time it sits over every other app, including other overlays such as EDCopilot's. By default it is
/// shown only while Elite Dangerous (or EDNexus itself, so the Settings preview works) is the app in
/// front. Pure so the rule can be tested without a window.
/// </summary>
public static class OverlayVisibilityPolicy
{
    /// <summary>Process names (without <c>.exe</c>) of the game client.</summary>
    public static readonly IReadOnlyList<string> GameProcessNames = ["EliteDangerous64", "EliteDangerous"];

    /// <summary>Whether the overlay window should be visible right now.</summary>
    /// <param name="wanted">The commander has the overlay switched on.</param>
    /// <param name="onlyWhenGameFocused">The "only while Elite is in front" setting.</param>
    /// <param name="foregroundProcess">
    /// Name of the process that owns the foreground window, or null when it cannot be determined. Unknown
    /// fails open (visible), so a detection problem never leaves the overlay permanently hidden.
    /// </param>
    /// <param name="ownProcess">EDNexus's own process name.</param>
    public static bool ShouldShow(bool wanted, bool onlyWhenGameFocused, string? foregroundProcess, string? ownProcess)
    {
        if (!wanted) return false;
        if (!onlyWhenGameFocused) return true;
        if (string.IsNullOrWhiteSpace(foregroundProcess)) return true;

        var name = Normalise(foregroundProcess);
        if (ownProcess is not null && string.Equals(name, Normalise(ownProcess), StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var game in GameProcessNames)
        {
            if (string.Equals(name, game, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string Normalise(string processName)
        => processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
}
