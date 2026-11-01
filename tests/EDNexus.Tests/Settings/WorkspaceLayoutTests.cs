using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using EDNexus.Core.Settings;
using Xunit;

namespace EDNexus.Tests.Settings;

public class WorkspaceLayoutTests
{
    private static CardDefaults[] Known(params string[] ids)
        => ids.Select(id => new CardDefaults(id, 452)).ToArray();

    private static CardLayout Card(string id, int order, double width = 452, bool collapsed = false, int column = CardLayout.AutoColumn, bool visible = true)
        => new() { Id = id, Order = order, Width = width, Collapsed = collapsed, Column = column, Visible = visible };

    [Fact]
    public void A_pre_workspace_layout_becomes_one_default_workspace_with_identical_cards()
    {
        var saved = new DashboardSettings
        {
            Cards = { Card("ship", 0, 600, collapsed: true, column: 1), Card("market", 1, visible: false), Card("location", 2) },
        };

        var resolved = WorkspaceLayout.Resolve(Known("location", "ship", "market"), saved);

        var only = Assert.Single(resolved);
        Assert.Equal(Workspace.DefaultId, only.Id);
        var expected = DashboardLayout.Merge(Known("location", "ship", "market"), saved.Cards);
        Assert.Equal(expected.Select(c => (c.Id, c.Order, c.Width, c.Collapsed, c.Column, c.Visible)),
                     only.Cards.Select(c => (c.Id, c.Order, c.Width, c.Collapsed, c.Column, c.Visible)));
    }

    [Fact]
    public void Nothing_saved_gives_one_workspace_with_every_card_in_shipped_order()
    {
        var resolved = WorkspaceLayout.Resolve(Known("a", "b", "c"), null);

        var only = Assert.Single(resolved);
        Assert.Equal(new[] { "a", "b", "c" }, only.Cards.Select(c => c.Id));
    }

    [Fact]
    public void Order_and_width_persist_independently_per_workspace()
    {
        var saved = new DashboardSettings
        {
            Workspaces =
            {
                new Workspace { Id = "w1", Name = "One", Cards = { Card("a", 0, 300), Card("b", 1, 400) } },
                new Workspace { Id = "w2", Name = "Two", Cards = { Card("b", 0, 700, collapsed: true), Card("a", 1, 500) } },
            },
        };

        var resolved = WorkspaceLayout.Resolve(Known("a", "b"), saved);

        Assert.Equal(new[] { "a", "b" }, resolved[0].Cards.Select(c => c.Id));
        Assert.Equal(new[] { 300.0, 400.0 }, resolved[0].Cards.Select(c => c.Width));
        Assert.Equal(new[] { "b", "a" }, resolved[1].Cards.Select(c => c.Id));
        Assert.Equal(new[] { 700.0, 500.0 }, resolved[1].Cards.Select(c => c.Width));
        Assert.True(resolved[1].Cards[0].Collapsed);
        Assert.False(resolved[0].Cards[1].Collapsed);
    }

    [Fact]
    public void A_brand_new_card_goes_to_the_first_workspace_only()
    {
        var saved = new DashboardSettings
        {
            Workspaces =
            {
                new Workspace { Id = "w1", Name = "One", Cards = { Card("a", 0) } },
                new Workspace { Id = "w2", Name = "Two", Cards = { Card("a", 0) } },
            },
        };

        var resolved = WorkspaceLayout.Resolve(Known("a", "new"), saved);

        Assert.Equal(new[] { "a", "new" }, resolved[0].Cards.Select(c => c.Id));
        Assert.Equal(new[] { "a" }, resolved[1].Cards.Select(c => c.Id));
    }

    [Fact]
    public void A_card_deliberately_left_off_the_first_page_is_not_pulled_back()
    {
        var saved = new DashboardSettings
        {
            Workspaces =
            {
                new Workspace { Id = "w1", Name = "One", Cards = { Card("a", 0) } },
                new Workspace { Id = "w2", Name = "Two", Cards = { Card("b", 0) } },
            },
        };

        var resolved = WorkspaceLayout.Resolve(Known("a", "b"), saved);

        Assert.Equal(new[] { "a" }, resolved[0].Cards.Select(c => c.Id));
        Assert.Equal(new[] { "b" }, resolved[1].Cards.Select(c => c.Id));
    }

    [Fact]
    public void Unknown_cards_are_dropped_from_every_workspace()
    {
        var saved = new DashboardSettings
        {
            Workspaces =
            {
                new Workspace { Id = "w1", Name = "One", Cards = { Card("gone", 0), Card("a", 1) } },
                new Workspace { Id = "w2", Name = "Two", Cards = { Card("gone", 0), Card("a", 1) } },
            },
        };

        var resolved = WorkspaceLayout.Resolve(Known("a"), saved);

        Assert.All(resolved, w => Assert.Equal(new[] { "a" }, w.Cards.Select(c => c.Id)));
    }

    [Fact]
    public void Corrupt_workspace_data_never_throws_and_still_yields_a_usable_page()
    {
        var saved = new DashboardSettings
        {
            Workspaces =
            {
                new Workspace { Id = "", Name = "no id", Cards = { Card("a", 0) } },
                new Workspace { Id = "dup", Name = "", Cards = { Card("a", 0), Card("a", 1, 999), Card("", 2) } },
                new Workspace { Id = "DUP", Name = "second dup" },
            },
        };
        saved.Workspaces.Add(null!);
        saved.Workspaces.Add(new Workspace { Id = "nullcards", Name = "x", Cards = null! });

        var resolved = WorkspaceLayout.Resolve(Known("a", "b"), saved);

        Assert.Equal(new[] { "dup", "nullcards" }, resolved.Select(w => w.Id));
        Assert.False(string.IsNullOrWhiteSpace(resolved[0].Name));
        Assert.Equal(new[] { "a", "b" }, resolved[0].Cards.Select(c => c.Id));
        Assert.Empty(resolved[1].Cards);
    }

    [Fact]
    public void Every_workspace_with_unusable_ids_falls_back_to_the_default()
    {
        var saved = new DashboardSettings { Workspaces = { new Workspace { Id = " " } } };

        var resolved = WorkspaceLayout.Resolve(Known("a"), saved);

        Assert.Equal(Workspace.DefaultId, Assert.Single(resolved).Id);
    }

    [Fact]
    public void A_settings_file_with_null_collections_deserialises_and_resolves()
    {
        var saved = JsonSerializer.Deserialize<DashboardSettings>(
            """{"Cards":null,"Workspaces":null,"ActiveWorkspaceId":null}""")!;

        var resolved = WorkspaceLayout.Resolve(Known("a"), saved);

        Assert.Single(resolved);
    }

    [Fact]
    public void Workspaces_round_trip_through_json()
    {
        var saved = new DashboardSettings
        {
            ActiveWorkspaceId = "w2",
            Workspaces =
            {
                new Workspace { Id = "w1", Name = "One", Cards = { Card("a", 0, 300) } },
                new Workspace { Id = "w2", Name = "Two", Cards = { Card("a", 0, 700, column: 2) } },
            },
        };

        var back = JsonSerializer.Deserialize<DashboardSettings>(JsonSerializer.Serialize(saved))!;
        var resolved = WorkspaceLayout.Resolve(Known("a"), back);

        Assert.Equal("w2", WorkspaceLayout.Active(resolved, back.ActiveWorkspaceId)!.Id);
        Assert.Equal(300, resolved[0].Cards[0].Width);
        Assert.Equal(700, resolved[1].Cards[0].Width);
        Assert.Equal(2, resolved[1].Cards[0].Column);
    }

    [Fact]
    public void A_stale_active_id_falls_back_to_the_first_workspace()
    {
        var resolved = WorkspaceLayout.Resolve(Known("a"), null);

        Assert.Equal(resolved[0].Id, WorkspaceLayout.Active(resolved, "deleted")!.Id);
        Assert.Equal(resolved[0].Id, WorkspaceLayout.Active(resolved, null)!.Id);
    }

    // --- Editing. ---

    [Fact]
    public void Create_rename_and_move_edit_the_list()
    {
        var list = WorkspaceLayout.Resolve(Known("a"), null).ToList();
        var made = WorkspaceLayout.Create(list, "  Exploration ");

        Assert.Equal("Exploration", made.Name);
        Assert.Empty(made.Cards);
        Assert.False(WorkspaceLayout.Rename(list, made.Id, "  "));
        Assert.True(WorkspaceLayout.Rename(list, made.Id, "Trade"));
        Assert.Equal("Trade", list[1].Name);

        Assert.True(WorkspaceLayout.MoveWorkspace(list, made.Id, 0));
        Assert.Equal("Trade", list[0].Name);
        Assert.False(WorkspaceLayout.MoveWorkspace(list, made.Id, -5));
    }

    [Fact]
    public void The_last_workspace_cannot_be_deleted()
    {
        var list = WorkspaceLayout.Resolve(Known("a"), null).ToList();

        Assert.False(WorkspaceLayout.Delete(list, list[0].Id));
        Assert.Single(list);
    }

    [Fact]
    public void Deleting_a_workspace_rehomes_cards_that_would_be_orphaned()
    {
        var list = WorkspaceLayout.Resolve(Known("a", "b"), null).ToList();
        var second = WorkspaceLayout.Create(list, "Two");
        WorkspaceLayout.SetMembership(second, new CardDefaults("a", 452), true);   // also on page one
        WorkspaceLayout.SetMembership(list[0], new CardDefaults("b", 452), false); // b only on page two
        WorkspaceLayout.SetMembership(second, new CardDefaults("b", 452), true);

        Assert.True(WorkspaceLayout.Delete(list, second.Id));

        Assert.Equal(new[] { "a", "b" }, list[0].Cards.Select(c => c.Id).OrderBy(x => x));
    }

    [Fact]
    public void Membership_can_be_added_and_removed_without_touching_other_workspaces()
    {
        var list = WorkspaceLayout.Resolve(Known("a", "b"), null).ToList();
        var second = WorkspaceLayout.Create(list, "Two");

        Assert.True(WorkspaceLayout.SetMembership(second, new CardDefaults("b", 500), true));
        Assert.False(WorkspaceLayout.SetMembership(second, new CardDefaults("b", 500), true));
        Assert.Equal(500, second.Cards[0].Width);
        Assert.Equal(2, list[0].Cards.Count);

        Assert.True(WorkspaceLayout.SetMembership(second, new CardDefaults("b", 500), false));
        Assert.Empty(second.Cards);
        Assert.False(WorkspaceLayout.SetMembership(second, new CardDefaults("b", 500), false));
    }

    [Fact]
    public void Resolving_twice_is_stable()
    {
        var saved = new DashboardSettings
        {
            Workspaces = { new Workspace { Id = "w1", Name = "One", Cards = { Card("b", 0, 300, column: 1), Card("a", 1) } } },
        };

        var once = WorkspaceLayout.Resolve(Known("a", "b"), saved);
        var twice = WorkspaceLayout.Resolve(Known("a", "b"), new DashboardSettings { Workspaces = once.ToList() });

        Assert.Equal(once[0].Cards.Select(c => (c.Id, c.Order, c.Width, c.Column)),
                     twice[0].Cards.Select(c => (c.Id, c.Order, c.Width, c.Column)));
        Assert.Equal(once.Select(w => (w.Id, w.Name)), twice.Select(w => (w.Id, w.Name)));
    }

    [Fact]
    public void A_null_Dashboard_in_the_settings_file_reads_as_defaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Dashboard":null}""")!;

        Assert.NotNull(settings.Dashboard);
        new WorkspaceSession(Known("a"), settings.Dashboard).WriteTo(settings.Dashboard, new[] { Card("a", 0) });
        Assert.Single(settings.Dashboard.Workspaces);
    }

    [Fact]
    public void A_file_rewritten_by_an_older_build_reseeds_one_workspace_from_Cards()
    {
        // An older build keeps only Cards (it has never heard of Workspaces), so the extra pages go.
        var rewritten = new DashboardSettings { Cards = { Card("a", 0, 300) } };

        var resolved = WorkspaceLayout.Resolve(Known("a", "b"), rewritten);

        var only = Assert.Single(resolved);
        Assert.Equal(Workspace.DefaultId, only.Id);
        Assert.Equal(300, only.Cards.Single(c => c.Id == "a").Width);
    }

    [Fact]
    public void Names_are_trimmed_capped_and_unique()
    {
        var list = WorkspaceLayout.Resolve(Known("a"), null).ToList();

        var long1 = WorkspaceLayout.Create(list, new string('x', 100));
        Assert.Equal(WorkspaceLayout.MaxNameLength, long1.Name.Length);

        var dup = WorkspaceLayout.Create(list, " main ");
        Assert.Equal("main 2", dup.Name);

        Assert.False(WorkspaceLayout.Rename(list, dup.Id, "MAIN"));
        Assert.True(WorkspaceLayout.Rename(list, dup.Id, "main 2"));   // its own name is fine

        var fromFile = WorkspaceLayout.Resolve(Known("a"), new DashboardSettings
        {
            Workspaces = { new Workspace { Id = "w", Name = "  " + new string('y', 80) + " " } },
        });
        Assert.Equal(WorkspaceLayout.MaxNameLength, fromFile[0].Name.Length);
    }

    [Fact]
    public void Blank_names_after_a_delete_do_not_collide()
    {
        var list = WorkspaceLayout.Resolve(Known("a"), null).ToList();
        var two = WorkspaceLayout.Create(list, null);   // Workspace 2
        var three = WorkspaceLayout.Create(list, null); // Workspace 3
        WorkspaceLayout.Delete(list, two.Id);

        var next = WorkspaceLayout.Create(list, null);

        Assert.NotEqual(three.Name, next.Name);
    }

    [Fact]
    public void Active_on_an_empty_list_is_null_not_a_crash()
        => Assert.Null(WorkspaceLayout.Active(new List<Workspace>(), "x"));

    [Fact]
    public void Delete_appends_after_the_highest_order_on_the_target()
    {
        var list = WorkspaceLayout.Resolve(Known("a", "b", "c"), null).ToList();
        var other = WorkspaceLayout.Create(list, "Two");
        WorkspaceLayout.SetMembership(other, new CardDefaults("x", 452), true);   // only on page two
        WorkspaceLayout.SetMembership(other, new CardDefaults("y", 452), true);
        list[0].Cards.RemoveAt(0);                                                // orders on page one: 1, 2

        WorkspaceLayout.Delete(list, other.Id);

        var orders = list[0].Cards.Select(c => c.Order).ToList();
        Assert.Equal(orders.Count, orders.Distinct().Count());
    }

    // --- The session: switching pages must never copy one page's layout onto another. ---

    private static WorkspaceSession TwoPages()
    {
        var saved = new DashboardSettings
        {
            Workspaces =
            {
                new Workspace { Id = "w1", Name = "One", Cards = { Card("a", 0, 300), Card("b", 1, 400) } },
                new Workspace { Id = "w2", Name = "Two", Cards = { Card("b", 0, 700) } },
            },
            ActiveWorkspaceId = "w1",
        };
        return new WorkspaceSession(Known("a", "b"), saved);
    }

    [Fact]
    public void Switching_pages_keeps_each_pages_own_layout()
    {
        var session = TwoPages();
        var dashboard = new DashboardSettings();

        // The commander widens "a" on page one, then switches.
        var shown = session.Select("w2", new[] { Card("a", 0, 555), Card("b", 1, 400) });

        Assert.NotNull(shown);
        Assert.Equal(new[] { "b" }, shown!.Select(c => c.Id));
        Assert.Equal(555, session.Workspaces[0].Cards[0].Width);   // page one was synced before the swap

        // The view now shows page two; saving must record that, not overwrite page one with it.
        session.WriteTo(dashboard, shown);
        Assert.Equal("w2", dashboard.ActiveWorkspaceId);
        Assert.Equal(new[] { "a", "b" }, dashboard.Workspaces[0].Cards.Select(c => c.Id));
        Assert.Equal(555, dashboard.Workspaces[0].Cards[0].Width);
        Assert.Equal(new[] { "b" }, dashboard.Workspaces[1].Cards.Select(c => c.Id));
    }

    [Fact]
    public void The_legacy_Cards_mirror_follows_the_first_page_even_when_another_is_active()
    {
        var session = TwoPages();
        var dashboard = new DashboardSettings();
        session.Select("w2", new[] { Card("a", 0, 300), Card("b", 1, 400) });

        session.WriteTo(dashboard, new[] { Card("b", 0, 900) });   // an edit made on page two

        Assert.Equal(new[] { "a", "b" }, dashboard.Cards.Select(c => c.Id));
        Assert.Equal(900, dashboard.Workspaces[1].Cards[0].Width);
        Assert.Equal(300, dashboard.Cards[0].Width);
    }

    [Fact]
    public void Selecting_an_unknown_or_already_active_page_changes_nothing()
    {
        var session = TwoPages();

        Assert.Null(session.Select("nope", new[] { Card("a", 0, 1) }));
        Assert.Null(session.Select("W1", new[] { Card("a", 0, 1) }));
        Assert.Equal("w1", session.ActiveId);
        Assert.Equal(300, session.Workspaces[0].Cards[0].Width);   // not synced on a no-op
    }

    [Fact]
    public void FindContaining_prefers_the_active_page_and_reports_none()
    {
        var session = TwoPages();

        Assert.Equal("w1", session.FindContaining("b")!.Id);
        session.Select("w2", session.Active.Cards);
        Assert.Equal("w2", session.FindContaining("b")!.Id);
        Assert.Equal("w1", session.FindContaining("a")!.Id);
        Assert.Null(session.FindContaining("zzz"));
    }
}
