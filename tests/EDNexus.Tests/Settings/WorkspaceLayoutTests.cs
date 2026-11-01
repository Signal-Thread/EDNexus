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
            """{"Cards":null,"Workspaces":null,"ActiveWorkspaceId":42}""".Replace("42", "null"))!;

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

        Assert.Equal("w2", WorkspaceLayout.Active(resolved, back.ActiveWorkspaceId).Id);
        Assert.Equal(300, resolved[0].Cards[0].Width);
        Assert.Equal(700, resolved[1].Cards[0].Width);
        Assert.Equal(2, resolved[1].Cards[0].Column);
    }

    [Fact]
    public void A_stale_active_id_falls_back_to_the_first_workspace()
    {
        var resolved = WorkspaceLayout.Resolve(Known("a"), null);

        Assert.Equal(resolved[0].Id, WorkspaceLayout.Active(resolved, "deleted").Id);
        Assert.Equal(resolved[0].Id, WorkspaceLayout.Active(resolved, null).Id);
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
    }
}
