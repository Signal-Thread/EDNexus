using System.Globalization;
using System.Text;
using EDNexus.Core.State;

namespace EDNexus.Core.Discord;

/// <summary>
/// Pure translation from the live <see cref="CommanderState"/> to a <see cref="DiscordPresencePayload"/>.
/// Kept free of any I/O or Discord client so it can be unit tested by constructing a
/// <see cref="CommanderState"/> and asserting on the mapped fields.
/// </summary>
public static class DiscordPresenceMapper
{
    /// <summary>The "Get EDNexus" button shown on every presence, per issue #49.</summary>
    public static readonly DiscordPresenceButton GetEdNexusButton =
        new("Get EDNexus", "https://github.com/Signal-Thread/EDNexus");

    private const string DefaultLargeImageKey = "ednexus_logo";
    private const string DefaultLargeImageText = "EDNexus";

    /// <summary>Shown in place of the system name when <see cref="DiscordPrivacyOptions.ShowSystem"/> is off.</summary>
    public const string HiddenSystemState = "In flight";

    /// <param name="state">The live commander picture. Read-only.</param>
    /// <param name="sessionStartedAt">When this play session began, for the elapsed-time fallback.</param>
    /// <param name="systemEnteredAt">
    /// When the commander arrived in <see cref="CommanderState.StarSystem"/>, so Discord can show
    /// elapsed time "in system" instead of elapsed time for the whole session.
    /// </param>
    /// <param name="privacy">
    /// The commander's privacy choices; <see cref="DiscordPrivacyOptions.Default"/> (everything
    /// visible) when omitted.
    /// </param>
    public static DiscordPresencePayload Map(
        CommanderState state, DateTimeOffset sessionStartedAt, DateTimeOffset? systemEnteredAt = null,
        DiscordPrivacyOptions? privacy = null)
    {
        var options = privacy ?? DiscordPrivacyOptions.Default;
        var docked = state.Docked;
        var station = state.StationDisplayName;
        var system = state.StarSystem;
        var body = state.Body;

        string stateText;
        if (!options.ShowSystem)
        {
            // A station or carrier name pins down the system just as surely as the system name does,
            // so hiding the system hides every location name.
            stateText = docked ? "Docked"
                : string.IsNullOrWhiteSpace(system) ? "In the black"
                : HiddenSystemState;
        }
        else
        {
            stateText = docked && !string.IsNullOrWhiteSpace(station)
                ? $"Docked at {station}"
                : BuildExploringState(system, body);
        }

        var detailsText = BuildDetails(state, options.ShowCommander);

        // Always the EDNexus logo. A per-ship key (e.g. "krait_mk_ii") only renders if an asset with exactly
        // that key was uploaded to the Discord application, and the journal's localised ship names are not
        // stable enough to pre-generate for every ship, so a missing one showed a broken image. The ship is
        // still named, in the hover text here and in the details line.
        var largeImageKey = DefaultLargeImageKey;
        var largeImageText = state.Ship ?? DefaultLargeImageText;

        var smallImageKey = docked ? "docked" : "cruising";
        var smallImageText = docked ? "Docked" : "In flight";

        var buttons = new List<DiscordPresenceButton> { GetEdNexusButton };
        // The Inara profile names the commander, and — because EDNexus's own Inara sync uploads
        // location on every jump and dock — also shows where they are. So it needs both allowed.
        if (options.ShowCommander && options.ShowSystem && !string.IsNullOrWhiteSpace(state.Name))
            buttons.Add(new DiscordPresenceButton(
                "View on Inara",
                $"https://inara.cz/elite/cmdrs/?search={Uri.EscapeDataString(state.Name!)}"));

        return new DiscordPresencePayload(
            State: stateText,
            Details: detailsText,
            LargeImageKey: largeImageKey,
            LargeImageText: largeImageText,
            SmallImageKey: smallImageKey,
            SmallImageText: smallImageText,
            // The "in system" timer restarting on every jump would leak jump timing while the system
            // itself is hidden, so fall back to the session-wide timer.
            StartedAt: options.ShowSystem ? systemEnteredAt ?? sessionStartedAt : sessionStartedAt,
            Buttons: buttons);
    }

    private static string BuildExploringState(string? system, string? body)
    {
        if (string.IsNullOrWhiteSpace(system)) return "In the black";
        return !string.IsNullOrWhiteSpace(body) && !string.Equals(body, system, StringComparison.OrdinalIgnoreCase)
            ? $"Exploring {system} / {body}"
            : $"Exploring {system}";
    }

    private static string? BuildDetails(CommanderState state, bool showCommander)
    {
        // A loaded hold is the more interesting "what are you doing" story than the ship name alone.
        if (state.CargoTons > 0)
            return $"Space Trucking: {state.CargoTons:0}t Cargo";

        if (string.IsNullOrWhiteSpace(state.Ship)) return null;

        // The ship ident is chosen by the commander and routinely tied to them, so it counts as
        // commander-identifying detail.
        return !showCommander || string.IsNullOrWhiteSpace(state.ShipIdent)
            ? $"Flying {state.Ship}"
            : $"Flying {state.Ship} ({state.ShipIdent})";
    }
}
