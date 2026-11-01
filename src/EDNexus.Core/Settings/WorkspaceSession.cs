namespace EDNexus.Core.Settings;

/// <summary>
/// The live workspace list plus which page is active, and the rules for keeping them in step with
/// what is on screen. Pure (no UI types) so the ordering that matters - sync the page being left,
/// swap, show the new page, then save - can be tested without a window.
/// </summary>
/// <remarks>
/// The owner (the dashboard view model) passes its current on-screen arrangement into
/// <see cref="Select"/> and <see cref="WriteTo"/>; the session never reads the UI itself. Not
/// thread-safe: use it from the UI thread only, like the card collection it mirrors.
/// </remarks>
public sealed class WorkspaceSession
{
    private readonly List<Workspace> _workspaces;

    /// <summary>Resolve the saved layout against the cards the app has, and start on the saved active page.</summary>
    public WorkspaceSession(IEnumerable<CardDefaults> known, DashboardSettings? saved)
    {
        _workspaces = WorkspaceLayout.Resolve(known, saved).ToList();
        ActiveId = WorkspaceLayout.Active(_workspaces, saved?.ActiveWorkspaceId)!.Id;
    }

    /// <summary>The workspaces in tab order. Never empty. Edit through the session so persistence stays in step.</summary>
    public IReadOnlyList<Workspace> Workspaces => _workspaces;

    /// <summary>Id of the page being shown.</summary>
    public string ActiveId { get; private set; }

    /// <summary>The page being shown.</summary>
    public Workspace Active => WorkspaceLayout.Active(_workspaces, ActiveId)!;

    /// <summary>Record the on-screen arrangement as the active page's cards.</summary>
    public void SyncActive(IEnumerable<CardLayout> onScreen) => Active.Cards = onScreen.ToList();

    /// <summary>
    /// Leave the active page for <paramref name="id"/>: the page being left first takes
    /// <paramref name="onScreen"/>, then the active id moves. Returns the arrangement to show, or null
    /// when the id is unknown or already active (nothing changed).
    /// </summary>
    public IReadOnlyList<CardLayout>? Select(string? id, IEnumerable<CardLayout> onScreen)
    {
        var target = _workspaces.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
        if (target is null || string.Equals(target.Id, ActiveId, StringComparison.OrdinalIgnoreCase)) return null;

        SyncActive(onScreen);
        ActiveId = target.Id;
        return target.Cards;
    }

    /// <summary>
    /// The page that holds <paramref name="cardId"/>, preferring the active one. Null when no page does.
    /// </summary>
    public Workspace? FindContaining(string cardId)
    {
        bool Has(Workspace w) => w.Cards.Any(c => string.Equals(c.Id, cardId, StringComparison.OrdinalIgnoreCase));
        return Has(Active) ? Active : _workspaces.FirstOrDefault(Has);
    }

    /// <summary>
    /// Write the session into <paramref name="dashboard"/>: the (synced) workspaces, the active id,
    /// and a copy of the first page into the legacy <see cref="DashboardSettings.Cards"/> list so an
    /// older build still finds a layout. It mirrors the first page, not the active one, so the legacy
    /// list does not jump around as the commander switches pages.
    /// </summary>
    public void WriteTo(DashboardSettings dashboard, IEnumerable<CardLayout> onScreen)
    {
        SyncActive(onScreen);
        dashboard.Workspaces = _workspaces.ToList();
        dashboard.ActiveWorkspaceId = ActiveId;
        dashboard.Cards = _workspaces[0].Cards.ToList();
    }
}
