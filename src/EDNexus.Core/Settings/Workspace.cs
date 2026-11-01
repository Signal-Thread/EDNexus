namespace EDNexus.Core.Settings;

/// <summary>
/// A named dashboard page. A card belongs to a workspace exactly when the workspace holds a
/// <see cref="CardLayout"/> for it; that entry carries the card's order, column, width, collapse and
/// visibility within this workspace, so the same card can be arranged differently on each page.
/// </summary>
public sealed class Workspace
{
    /// <summary>Id of the workspace every install starts with (and that a pre-workspace layout migrates into).</summary>
    public const string DefaultId = "main";

    /// <summary>Display name of the default workspace.</summary>
    public const string DefaultName = "Main";

    /// <summary>Stable identifier, unique within the layout. Not shown to the commander.</summary>
    public string Id { get; set; } = "";

    /// <summary>Name shown on the tab.</summary>
    public string Name { get; set; } = "";

    /// <summary>Per-card arrangement for the cards on this page.</summary>
    public List<CardLayout> Cards { get; set; } = new();
}
