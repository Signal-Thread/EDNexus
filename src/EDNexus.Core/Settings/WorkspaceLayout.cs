namespace EDNexus.Core.Settings;

/// <summary>
/// Pure operations over the workspace model: migrating and sanitising what was saved, and editing
/// the workspace list. The per-page card arithmetic (merge, move, drop) stays in
/// <see cref="DashboardLayout"/>, which is applied to one workspace's cards at a time.
/// </summary>
/// <remarks>
/// Everything here is tolerant of damaged input: a hand-edited or truncated settings file must never
/// stop the app starting, so the worst outcome of bad data is falling back to the default page.
/// </remarks>
public static class WorkspaceLayout
{
    /// <summary>Longest workspace name kept; longer ones are cut (tabs have limited room).</summary>
    public const int MaxNameLength = 40;

    private static string CleanName(string? name)
    {
        var t = (name ?? "").Trim();
        return t.Length > MaxNameLength ? t[..MaxNameLength].TrimEnd() : t;
    }

    /// <summary>
    /// Produce the effective workspaces for <paramref name="known"/> from <paramref name="saved"/>.
    /// Always returns at least one workspace.
    /// </summary>
    /// <remarks>
    /// With no saved workspaces (a pre-workspace settings file) the legacy <c>Cards</c> become one
    /// default workspace, so the commander sees exactly what they saw before. Entries with no id are
    /// dropped and duplicate ids keep their first occurrence. Unknown cards are dropped from every
    /// workspace. A card that appears on no workspace (new in this release) is added to the first
    /// workspace with its shipped defaults; cards deliberately left off other pages are never
    /// auto-added there. A card on no page at all (removed from every one) is indistinguishable from
    /// a new card and so comes back on the first page; the UI should not offer removing a card from
    /// its last page. Names are trimmed and capped at <see cref="MaxNameLength"/>.
    /// </remarks>
    public static IReadOnlyList<Workspace> Resolve(IEnumerable<CardDefaults> known, DashboardSettings? saved)
    {
        var knownList = known.ToList();
        var knownIds = new HashSet<string>(knownList.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

        var source = new List<Workspace>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in saved?.Workspaces ?? new List<Workspace>())
        {
            if (w is null || string.IsNullOrWhiteSpace(w.Id) || !seenIds.Add(w.Id)) continue;
            source.Add(w);
        }

        if (source.Count == 0)
        {
            source.Add(new Workspace
            {
                Id = Workspace.DefaultId,
                Name = Workspace.DefaultName,
                Cards = saved?.Cards ?? new List<CardLayout>(),
            });
        }

        // Which known cards are already placed on some page.
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in source)
            foreach (var c in w.Cards ?? new List<CardLayout>())
                if (c is not null && !string.IsNullOrEmpty(c.Id) && knownIds.Contains(c.Id)) placed.Add(c.Id);

        var result = new List<Workspace>();
        for (var i = 0; i < source.Count; i++)
        {
            var w = source[i];
            var entries = (w.Cards ?? new List<CardLayout>())
                .Where(c => c is not null && !string.IsNullOrEmpty(c.Id))
                .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var members = new HashSet<string>(entries.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

            // The first page also takes every card that is on no page; later pages only their own.
            var pageKnown = knownList.Where(c => members.Contains(c.Id) || (i == 0 && !placed.Contains(c.Id)));
            result.Add(new Workspace
            {
                Id = w.Id,
                Name = CleanName(w.Name) is { Length: > 0 } clean
                    ? clean
                    : (i == 0 ? Workspace.DefaultName : $"Workspace {i + 1}"),
                Cards = DashboardLayout.Merge(pageKnown, entries).ToList(),
            });
        }

        return result;
    }

    /// <summary>
    /// The workspace to show: the saved active one if it still exists, otherwise the first. Null only
    /// for an empty list (<see cref="Resolve"/> never returns one).
    /// </summary>
    public static Workspace? Active(IReadOnlyList<Workspace> workspaces, string? activeId)
        => workspaces.FirstOrDefault(w => string.Equals(w.Id, activeId, StringComparison.OrdinalIgnoreCase))
           ?? workspaces.FirstOrDefault();

    private static bool NameTaken(IEnumerable<Workspace> workspaces, string name, Workspace? except = null)
        => workspaces.Any(w => !ReferenceEquals(w, except) && string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The next free order value on a page: one past the highest, so removals can't cause a clash.</summary>
    private static int NextOrder(Workspace w) => w.Cards.Count == 0 ? 0 : w.Cards.Max(c => c.Order) + 1;

    /// <summary>
    /// Add a new, empty workspace and return it. The id is generated, never derived from the name,
    /// so renaming can't collide with another page.
    /// </summary>
    public static Workspace Create(List<Workspace> workspaces, string? name)
    {
        string id;
        do id = Guid.NewGuid().ToString("N")[..8];
        while (workspaces.Any(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase)));

        var workspace = new Workspace
        {
            Id = id,
            Name = UniqueName(workspaces, CleanName(name)),
        };
        workspaces.Add(workspace);
        return workspace;
    }

    /// <summary>A name not used by another page: blank becomes "Workspace n", a clash gets " 2", " 3"…</summary>
    private static string UniqueName(List<Workspace> workspaces, string name)
    {
        if (name.Length == 0)
        {
            for (var n = workspaces.Count + 1; ; n++)
                if (!NameTaken(workspaces, $"Workspace {n}")) return $"Workspace {n}";
        }

        if (!NameTaken(workspaces, name)) return name;
        for (var n = 2; ; n++)
        {
            var suffix = $" {n}";
            var candidate = (name.Length + suffix.Length > MaxNameLength ? name[..(MaxNameLength - suffix.Length)] : name) + suffix;
            if (!NameTaken(workspaces, candidate)) return candidate;
        }
    }

    /// <summary>Rename a workspace. Blank names and names another page already uses are refused (returns false); long names are cut.</summary>
    public static bool Rename(IEnumerable<Workspace> workspaces, string id, string? name)
    {
        var clean = CleanName(name);
        if (clean.Length == 0) return false;
        var list = workspaces.ToList();
        var w = list.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (w is null || NameTaken(list, clean, w)) return false;
        w.Name = clean;
        return true;
    }

    /// <summary>
    /// Delete a workspace. The last one can't be deleted, and a card that would be left on no page
    /// moves to the first remaining workspace so it is never lost.
    /// </summary>
    public static bool Delete(List<Workspace> workspaces, string id)
    {
        var index = workspaces.FindIndex(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || workspaces.Count <= 1) return false;

        var removed = workspaces[index];
        workspaces.RemoveAt(index);

        var target = workspaces[0];
        foreach (var card in removed.Cards)
        {
            if (workspaces.Any(w => w.Cards.Any(c => string.Equals(c.Id, card.Id, StringComparison.OrdinalIgnoreCase))))
                continue;
            target.Cards.Add(new CardLayout
            {
                Id = card.Id,
                Order = NextOrder(target),
                Visible = card.Visible,
                Width = card.Width,
                Collapsed = card.Collapsed,
                Column = CardLayout.AutoColumn,
            });
        }

        return true;
    }

    /// <summary>
    /// Put a card on a workspace (appended after the existing cards, on automatic placement) or take
    /// it off. Returns false when nothing changed.
    /// </summary>
    public static bool SetMembership(Workspace workspace, CardDefaults card, bool member)
    {
        var existing = workspace.Cards.FindIndex(c => string.Equals(c.Id, card.Id, StringComparison.OrdinalIgnoreCase));
        if (member)
        {
            if (existing >= 0) return false;
            workspace.Cards.Add(new CardLayout
            {
                Id = card.Id,
                Order = NextOrder(workspace),
                Width = card.DefaultWidth,
            });
            return true;
        }

        if (existing < 0) return false;
        workspace.Cards.RemoveAt(existing);
        return true;
    }

    /// <summary>Move a workspace to <paramref name="newIndex"/> in the tab order (clamped).</summary>
    public static bool MoveWorkspace(List<Workspace> workspaces, string id, int newIndex)
    {
        var from = workspaces.FindIndex(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return false;
        var to = Math.Clamp(newIndex, 0, workspaces.Count - 1);
        if (to == from) return false;
        var w = workspaces[from];
        workspaces.RemoveAt(from);
        workspaces.Insert(to, w);
        return true;
    }
}
