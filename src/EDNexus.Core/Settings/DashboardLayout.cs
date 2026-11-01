namespace EDNexus.Core.Settings;

/// <summary>
/// The saved arrangement of one dashboard card. Absent from the settings file until the commander
/// changes something, so an untouched install keeps whatever order the app ships with.
/// </summary>
/// <param name="Id">Card key (e.g. "market"), matching <c>CardViewModel.Id</c>.</param>
public sealed class CardLayout
{
    public string Id { get; set; } = "";

    /// <summary>Position in the dashboard flow, ascending. Within a column, this is the stacking order.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Dashboard column the commander dropped the card into, or <see cref="AutoColumn"/> for a card
    /// they have never moved — those are still packed automatically into whichever column is
    /// shortest. Kept even when the window is too narrow to show that many columns, so widening the
    /// window restores the arrangement rather than flattening it.
    /// </summary>
    public int Column { get; set; } = AutoColumn;

    /// <summary>Sentinel for "not placed by hand — pack this one automatically".</summary>
    public const int AutoColumn = -1;

    /// <summary>False hides the card entirely.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Card width in pixels. 0 means "use the card's own default".</summary>
    public double Width { get; set; }

    /// <summary>Whether the card is collapsed to its header.</summary>
    public bool Collapsed { get; set; }
}

/// <summary>The commander's dashboard arrangement, persisted through the settings store.</summary>
public sealed class DashboardSettings
{
    /// <summary>
    /// Per-card arrangement from before workspaces existed. Read once to seed the first workspace
    /// (see <see cref="WorkspaceLayout.Resolve"/>) and thereafter kept as a mirror of the first
    /// workspace, so the file stays readable by an older build.
    /// </summary>
    public List<CardLayout> Cards { get; set; } = new();

    /// <summary>
    /// The commander's workspaces (pages), in tab order. Empty until the layout is first saved by a
    /// build that knows about workspaces; <see cref="WorkspaceLayout.Resolve"/> then synthesises a
    /// single default workspace from <see cref="Cards"/>.
    /// </summary>
    public List<Workspace> Workspaces { get; set; } = new();

    /// <summary>Id of the workspace shown on startup. Falls back to the first when missing or stale.</summary>
    public string? ActiveWorkspaceId { get; set; }
}

/// <summary>A card as the app defines it, before any saved layout is applied.</summary>
/// <param name="DefaultWidth">The width the card ships with, restored by "reset layout".</param>
public readonly record struct CardDefaults(string Id, double DefaultWidth);

/// <summary>
/// Reconciles a saved dashboard layout with the cards the app actually has.
/// </summary>
/// <remarks>
/// The two drift apart across releases — cards get added and removed — so this is deliberately
/// tolerant in both directions: a saved entry for a card that no longer exists is dropped, and a
/// card with no saved entry keeps its shipped defaults and lands after everything already
/// arranged. That way upgrading never loses a commander's arrangement, and never hides a new card
/// they have not seen yet.
/// </remarks>
public static class DashboardLayout
{
    /// <summary>
    /// Produce the effective layout for <paramref name="known"/>, applying <paramref name="saved"/>
    /// where it still applies. Returns one entry per known card, in display order.
    /// </summary>
    public static IReadOnlyList<CardLayout> Merge(IEnumerable<CardDefaults> known, IEnumerable<CardLayout>? saved)
    {
        var savedById = new Dictionary<string, CardLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in saved ?? Enumerable.Empty<CardLayout>())
            if (entry.Id is { Length: > 0 }) savedById[entry.Id] = entry;

        // New cards sort after every arranged one, in the order the app declared them.
        var nextOrder = savedById.Count == 0 ? 0 : savedById.Values.Max(c => c.Order) + 1;

        var merged = new List<CardLayout>();
        foreach (var card in known)
        {
            if (savedById.TryGetValue(card.Id, out var entry))
            {
                merged.Add(new CardLayout
                {
                    Id = card.Id,
                    Order = entry.Order,
                    Visible = entry.Visible,
                    Width = entry.Width > 0 ? entry.Width : card.DefaultWidth,
                    Collapsed = entry.Collapsed,
                    Column = entry.Column,
                });
            }
            else
            {
                merged.Add(new CardLayout
                {
                    Id = card.Id,
                    Order = nextOrder++,
                    Visible = true,
                    Width = card.DefaultWidth,
                    Collapsed = false,
                    Column = CardLayout.AutoColumn,
                });
            }
        }

        return Renumber(merged.OrderBy(c => c.Order).ToList());
    }

    /// <summary>The shipped arrangement — used by "reset layout to default".</summary>
    public static IReadOnlyList<CardLayout> Defaults(IEnumerable<CardDefaults> known)
        => Renumber(known.Select((c, i) => new CardLayout
        {
            Id = c.Id,
            Order = i,
            Visible = true,
            Width = c.DefaultWidth,
            Collapsed = false,
            Column = CardLayout.AutoColumn,
        }).ToList());

    /// <summary>
    /// Move the card with <paramref name="id"/> one place towards the front or back, and return the
    /// re-ordered layout. A card already at the end it is moving towards is returned unchanged.
    /// </summary>
    /// <remarks>
    /// The step is within the card's own column: once cards are placed by hand, the neighbours a
    /// commander sees above and below a card are the ones sharing its column, not the ones adjacent
    /// in the flat list. Cards left on automatic placement all share the one auto "column", so for
    /// an untouched dashboard this is still a plain move through the flow.
    /// </remarks>
    public static IReadOnlyList<CardLayout> Move(IEnumerable<CardLayout> layout, string id, int delta)
    {
        var list = layout.OrderBy(c => c.Order).ToList();
        var index = list.FindIndex(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || delta == 0) return Renumber(list);

        var moved = list[index];

        // Walk out to the |delta|'th card sharing this column, and land on its side of it.
        var target = index;
        for (var step = 0; step < Math.Abs(delta); step++)
        {
            var next = NeighbourInColumn(list, target, Math.Sign(delta), moved.Column);
            if (next < 0) break;
            target = next;
        }

        if (target == index) return Renumber(list);

        list.RemoveAt(index);
        list.Insert(target, moved);
        return Renumber(list);
    }

    /// <summary>
    /// Drop <paramref name="id"/> into <paramref name="column"/>, immediately above
    /// <paramref name="beforeId"/> — or at the bottom of that column when it is null, which is what
    /// a drop into the open space below a column means.
    /// </summary>
    public static IReadOnlyList<CardLayout> MoveTo(
        IEnumerable<CardLayout> layout, string id, int column, string? beforeId)
    {
        var list = layout.OrderBy(c => c.Order).ToList();
        var from = list.FindIndex(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || column < 0) return Renumber(list);

        // Dropped on itself — a click on the title that travelled a pixel. Take the column but hold
        // the position, or removing and re-appending would fling the card to the end of the column.
        if (string.Equals(beforeId, id, StringComparison.OrdinalIgnoreCase))
        {
            list[from].Column = column;
            return Renumber(list);
        }

        var moved = list[from];
        list.RemoveAt(from);

        // Stacking within a column follows the flat order, so landing directly before the card the
        // drop pointed at puts it in the right place whatever the other columns hold.
        var insertAt = beforeId is null
            ? list.Count
            : list.FindIndex(c => string.Equals(c.Id, beforeId, StringComparison.OrdinalIgnoreCase));
        if (insertAt < 0) insertAt = list.Count;

        moved.Column = column;
        list.Insert(insertAt, moved);
        return Renumber(list);
    }

    /// <summary>
    /// Fix cards that are still on automatic placement into the columns they are currently drawn in.
    /// </summary>
    /// <remarks>
    /// Applied on the first hand-placement: once a commander starts arranging the dashboard, the
    /// cards they have not touched should stay where they already are rather than re-flowing around
    /// each new drop. Cards missing from <paramref name="renderedColumns"/> — hidden ones — are left
    /// automatic, so unhiding one still finds it a place.
    /// </remarks>
    public static IReadOnlyList<CardLayout> Pin(
        IEnumerable<CardLayout> layout, IReadOnlyDictionary<string, int> renderedColumns)
    {
        var list = layout.OrderBy(c => c.Order).ToList();
        foreach (var card in list)
        {
            if (card.Column != CardLayout.AutoColumn) continue;
            if (renderedColumns.TryGetValue(card.Id, out var column) && column >= 0) card.Column = column;
        }

        return Renumber(list);
    }

    /// <summary>Index of the next card on <paramref name="direction"/> that shares <paramref name="column"/>.</summary>
    private static int NeighbourInColumn(List<CardLayout> list, int from, int direction, int column)
    {
        for (var i = from + direction; i >= 0 && i < list.Count; i += direction)
        {
            if (list[i].Column == column) return i;
        }

        return -1;
    }

    /// <summary>Rewrite Order to a dense 0..n-1 sequence matching list position.</summary>
    private static IReadOnlyList<CardLayout> Renumber(List<CardLayout> list)
    {
        for (var i = 0; i < list.Count; i++) list[i].Order = i;
        return list;
    }
}
