using EDNexus.Core.Discord;
using EDNexus.Core.Settings;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.Discord;

/// <summary>Which presence fields are emitted for each combination of the #50 privacy settings.</summary>
public class DiscordPrivacyMapperTests
{
    private static readonly DateTimeOffset SessionStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SystemEntered = SessionStart.AddHours(1);

    private static readonly DiscordPrivacyOptions HideSystem = new(ShowSystem: false, ShowCommander: true);
    private static readonly DiscordPrivacyOptions HideCommander = new(ShowSystem: true, ShowCommander: false);
    private static readonly DiscordPrivacyOptions HideBoth = new(ShowSystem: false, ShowCommander: false);

    private static CommanderState Flying() => new()
    {
        Name = "Jameson",
        StarSystem = "Colonia",
        Body = "Colonia 2 a",
        Ship = "Anaconda",
        ShipIdent = "JM-01A",
    };

    private static CommanderState DockedAtStation() => new()
    {
        Name = "Jameson",
        StarSystem = "Shinrarta Dezhra",
        Docked = true,
        StationName = "Jameson Memorial",
        Ship = "Anaconda",
    };

    private static IEnumerable<string?> AllText(DiscordPresencePayload p) =>
        new[] { p.State, p.Details, p.LargeImageText, p.SmallImageText }
            .Concat(p.Buttons.Select(b => b.Label))
            .Concat(p.Buttons.Select(b => b.Url));

    [Fact]
    public void Defaults_show_everything()
    {
        var payload = DiscordPresenceMapper.Map(Flying(), SessionStart, SystemEntered, DiscordPrivacyOptions.Default);

        Assert.Equal("Exploring Colonia / Colonia 2 a", payload.State);
        Assert.Equal("Flying Anaconda (JM-01A)", payload.Details);
        Assert.Contains(payload.Buttons, b => b.Label == "View on Inara");
        Assert.Equal(SystemEntered, payload.StartedAt);
    }

    [Fact]
    public void Omitting_privacy_is_the_same_as_the_defaults()
    {
        var state = Flying();

        Assert.Equal(
            DiscordPresenceMapper.Map(state, SessionStart, SystemEntered, DiscordPrivacyOptions.Default),
            DiscordPresenceMapper.Map(state, SessionStart, SystemEntered));
    }

    [Fact]
    public void Hidden_system_replaces_the_system_and_body_with_a_neutral_in_flight()
    {
        var payload = DiscordPresenceMapper.Map(Flying(), SessionStart, SystemEntered, HideSystem);

        Assert.Equal("In flight", payload.State);
        Assert.DoesNotContain(AllText(payload), t => t?.Contains("Colonia", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Hidden_system_also_hides_the_station_name_when_docked()
    {
        var payload = DiscordPresenceMapper.Map(DockedAtStation(), SessionStart, SystemEntered, HideSystem);

        Assert.Equal("Docked", payload.State);
        Assert.DoesNotContain(AllText(payload), t => t?.Contains("Jameson Memorial") == true);
        Assert.DoesNotContain(AllText(payload), t => t?.Contains("Shinrarta") == true);
    }

    [Fact]
    public void Hidden_system_also_hides_the_fleet_carrier_name()
    {
        var state = new CommanderState
        {
            Docked = true, StationName = "K7Q-B3L", CarrierName = "Nomad's Reach", CarrierCallsign = "K7Q-B3L",
        };

        var payload = DiscordPresenceMapper.Map(state, SessionStart, SystemEntered, HideSystem);

        Assert.Equal("Docked", payload.State);
        Assert.DoesNotContain(AllText(payload), t => t?.Contains("Nomad") == true || t?.Contains("K7Q") == true);
    }

    [Fact]
    public void Hidden_system_with_no_known_system_stays_in_the_black()
    {
        var payload = DiscordPresenceMapper.Map(new CommanderState(), SessionStart, SystemEntered, HideSystem);

        Assert.Equal("In the black", payload.State);
    }

    [Fact]
    public void Hidden_system_times_from_session_start_so_jumps_are_not_revealed()
    {
        var payload = DiscordPresenceMapper.Map(Flying(), SessionStart, SystemEntered, HideSystem);

        Assert.Equal(SessionStart, payload.StartedAt);
    }

    [Fact]
    public void Hidden_system_keeps_the_ship_ident()
    {
        var payload = DiscordPresenceMapper.Map(Flying(), SessionStart, SystemEntered, HideSystem);

        Assert.Equal("Flying Anaconda (JM-01A)", payload.Details);
    }

    [Fact]
    public void Hidden_system_drops_the_inara_button_because_inara_shows_location()
    {
        var payload = DiscordPresenceMapper.Map(Flying(), SessionStart, SystemEntered, HideSystem);

        Assert.Equal(DiscordPresenceMapper.GetEdNexusButton, Assert.Single(payload.Buttons));
        Assert.DoesNotContain(payload.Buttons, b => b.Url.Contains("inara", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Hidden_commander_drops_the_inara_button_and_ship_ident()
    {
        var payload = DiscordPresenceMapper.Map(Flying(), SessionStart, SystemEntered, HideCommander);

        Assert.Equal("Flying Anaconda", payload.Details);
        Assert.Equal(DiscordPresenceMapper.GetEdNexusButton, Assert.Single(payload.Buttons));
        Assert.DoesNotContain(AllText(payload), t => t?.Contains("Jameson") == true || t?.Contains("JM-01A") == true);
    }

    [Fact]
    public void Hidden_commander_keeps_the_location()
    {
        var payload = DiscordPresenceMapper.Map(Flying(), SessionStart, SystemEntered, HideCommander);

        Assert.Equal("Exploring Colonia / Colonia 2 a", payload.State);
        Assert.Equal(SystemEntered, payload.StartedAt);
    }

    [Fact]
    public void Hiding_both_leaves_nothing_identifying()
    {
        var payload = DiscordPresenceMapper.Map(Flying(), SessionStart, SystemEntered, HideBoth);

        Assert.Equal(DiscordPresenceMapper.HiddenSystemState, payload.State);
        Assert.Equal("Flying Anaconda", payload.Details);
        Assert.Single(payload.Buttons);
        Assert.DoesNotContain(AllText(payload), t =>
            t?.Contains("Colonia") == true || t?.Contains("Jameson") == true || t?.Contains("JM-01A") == true);
    }

    [Fact]
    public void Cargo_details_are_unaffected_by_privacy()
    {
        var state = Flying();
        state.CargoTons = 256;

        var payload = DiscordPresenceMapper.Map(state, SessionStart, SystemEntered, HideBoth);

        Assert.Equal("Space Trucking: 256t Cargo", payload.Details);
    }

    [Fact]
    public void Options_snapshot_the_persisted_settings()
    {
        var options = DiscordPrivacyOptions.From(new DiscordSettings { ShowSystem = false, ShowCommander = true });

        Assert.Equal(HideSystem, options);
    }
}

/// <summary>The live service re-pushes on privacy changes (#50) without waiting out the throttle.</summary>
public class DiscordPresenceServicePrivacyTests
{
    [Fact]
    public void Initial_privacy_is_applied_to_the_very_first_push()
    {
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(
            new CommanderState { StarSystem = "Sol" }, client, TimeSpan.FromSeconds(15),
            privacy: new DiscordPrivacyOptions(ShowSystem: false, ShowCommander: true));

        Assert.Equal(DiscordPresenceMapper.HiddenSystemState, Assert.Single(client.Sent).State);
    }

    [Fact]
    public void Hiding_the_system_pushes_immediately_even_inside_the_throttle_window()
    {
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(
            new CommanderState { StarSystem = "Sol" }, client, TimeSpan.FromMinutes(5));
        Assert.Equal("Exploring Sol", Assert.Single(client.Sent).State);

        service.UpdatePrivacy(new DiscordPrivacyOptions(ShowSystem: false, ShowCommander: true));

        Assert.Equal(2, client.Sent.Count);
        Assert.Equal(DiscordPresenceMapper.HiddenSystemState, client.Sent[^1].State);
    }

    [Fact]
    public void Re_applying_the_same_privacy_sends_nothing()
    {
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(
            new CommanderState { StarSystem = "Sol" }, client, TimeSpan.FromMilliseconds(1));

        service.UpdatePrivacy(DiscordPrivacyOptions.Default);

        Assert.Single(client.Sent);
    }

    [Fact]
    public void Later_state_changes_keep_honouring_the_updated_privacy()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var state = new CommanderState { StarSystem = "Sol" };
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(
            state, client, TimeSpan.FromSeconds(15), clock: () => now);

        service.UpdatePrivacy(new DiscordPrivacyOptions(ShowSystem: false, ShowCommander: true));
        now = now.AddMinutes(1);   // past the throttle window, so the next change sends synchronously
        state.StationName = "Jameson Memorial";
        state.Docked = true;

        Assert.Equal(3, client.Sent.Count);   // initial, privacy change, docking
        Assert.DoesNotContain(client.Sent.Skip(1), p => p.State?.Contains("Jameson") == true);
        Assert.Equal("Docked", client.Sent[^1].State);
    }

    [Fact]
    public async Task A_system_change_queued_in_the_throttle_window_never_leaks_after_hiding_the_system()
    {
        // A manual clock guarantees the system change lands inside the throttle window however slow
        // the machine is, so it is always queued rather than sent.
        var time = new ManualTimeProvider();
        var state = new CommanderState { StarSystem = "Sol" };
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(
            state, client, TimeSpan.FromMilliseconds(50), time: time);

        var scheduled = time.NextTimerCreated;
        state.StarSystem = "Colonia";
        Assert.DoesNotContain(client.Sent, p => p.State?.Contains("Colonia") == true);   // queued, not sent
        var queued = service.PendingSend;
        await scheduled.WaitAsync(TimeSpan.FromSeconds(10));   // its wait has started

        service.UpdatePrivacy(new DiscordPrivacyOptions(ShowSystem: false, ShowCommander: true));

        // Open the throttle and let the queued send's wait run out, so it would go through if it hadn't
        // been superseded, then wait for it to finish either way.
        time.Advance(TimeSpan.FromMinutes(1));
        await queued.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(client.Sent, p =>
            p.State?.Contains("Colonia") == true || p.Buttons.Any(b => b.Url.Contains("Colonia")));
        Assert.Equal(DiscordPresenceMapper.HiddenSystemState, client.Sent[^1].State);
    }

    [Fact]
    public void A_suppressed_service_clears_presence_on_a_privacy_change_and_sends_nothing()
    {
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(
            new CommanderState { StarSystem = "Sol" }, client, TimeSpan.FromMilliseconds(1), isSuppressed: () => true);
        var clearsBefore = client.ClearCalls;

        service.UpdatePrivacy(new DiscordPrivacyOptions(ShowSystem: false, ShowCommander: false));

        Assert.Empty(client.Sent);
        Assert.Equal(clearsBefore + 1, client.ClearCalls);
    }

    [Fact]
    public void Entering_suppression_clears_the_last_real_presence()
    {
        var suppressed = false;
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(
            new CommanderState { StarSystem = "Sol" }, client, TimeSpan.FromMilliseconds(1), isSuppressed: () => suppressed);
        Assert.Single(client.Sent);
        Assert.Equal(0, client.ClearCalls);

        suppressed = true;   // developer mode switched on
        service.Refresh();

        Assert.Equal(1, client.ClearCalls);
        Assert.Single(client.Sent);
    }

    [Fact]
    public void Fabricated_state_while_suppressed_clears_once_and_never_sends()
    {
        var suppressed = false;
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var state = new CommanderState { StarSystem = "Sol" };
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(
            state, client, TimeSpan.FromSeconds(15), isSuppressed: () => suppressed, clock: () => now);

        suppressed = true;
        now = now.AddMinutes(1);
        state.StarSystem = "Fabricated 1";
        state.StarSystem = "Fabricated 2";

        Assert.Equal(1, client.ClearCalls);
        Assert.DoesNotContain(client.Sent, p => p.State?.Contains("Fabricated") == true);
    }

    [Fact]
    public void Leaving_suppression_re_pushes_the_current_presence()
    {
        var suppressed = true;
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var client = new FakeDiscordRpcClient();
        using var service = new DiscordPresenceService(
            new CommanderState { StarSystem = "Sol" }, client, TimeSpan.FromSeconds(15),
            isSuppressed: () => suppressed, clock: () => now);
        Assert.Empty(client.Sent);

        suppressed = false;
        service.Refresh();

        Assert.Equal("Exploring Sol", Assert.Single(client.Sent).State);
    }
}

/// <summary>Turning presence on/off from Settings connects/disconnects live (#50).</summary>
public class DiscordPresenceControllerTests
{
    private sealed class Harness : IDisposable
    {
        public CommanderState State { get; } = new() { StarSystem = "Sol", Name = "Jameson" };
        public List<FakeDiscordRpcClient> Clients { get; } = new();
        public DiscordPresenceController Controller { get; }

        public Harness()
        {
            Controller = new DiscordPresenceController(State, () =>
            {
                var client = new FakeDiscordRpcClient();
                Clients.Add(client);
                return client;
            }, isSuppressed: null, TimeSpan.FromMinutes(5));
        }

        public void Dispose() => Controller.Dispose();
    }

    [Fact]
    public void Disabled_settings_never_create_a_client()
    {
        using var h = new Harness();

        h.Controller.Apply(new DiscordSettings { Enabled = false });

        Assert.False(h.Controller.IsActive);
        Assert.Empty(h.Clients);
    }

    [Fact]
    public void Enabled_settings_connect_and_push_with_the_chosen_privacy()
    {
        using var h = new Harness();

        h.Controller.Apply(new DiscordSettings { Enabled = true, ShowSystem = false });

        Assert.True(h.Controller.IsActive);
        var client = Assert.Single(h.Clients);
        Assert.Equal(1, client.InitializeCalls);
        Assert.Equal(DiscordPresenceMapper.HiddenSystemState, Assert.Single(client.Sent).State);
    }

    [Fact]
    public void Toggling_off_clears_presence_and_disconnects()
    {
        using var h = new Harness();
        h.Controller.Apply(new DiscordSettings { Enabled = true });

        h.Controller.Apply(new DiscordSettings { Enabled = false });

        Assert.False(h.Controller.IsActive);
        var client = Assert.Single(h.Clients);
        Assert.Equal(1, client.ClearCalls);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public void Toggling_back_on_reconnects_with_a_fresh_client()
    {
        using var h = new Harness();
        h.Controller.Apply(new DiscordSettings { Enabled = true });
        h.Controller.Apply(new DiscordSettings { Enabled = false });

        h.Controller.Apply(new DiscordSettings { Enabled = true });

        Assert.True(h.Controller.IsActive);
        Assert.Equal(2, h.Clients.Count);
        Assert.Equal(1, h.Clients[1].InitializeCalls);
        Assert.Single(h.Clients[1].Sent);
    }

    [Fact]
    public void Privacy_changes_while_on_reuse_the_connection_and_push_immediately()
    {
        using var h = new Harness();
        h.Controller.Apply(new DiscordSettings { Enabled = true });

        h.Controller.Apply(new DiscordSettings { Enabled = true, ShowSystem = false, ShowCommander = false });

        var client = Assert.Single(h.Clients);
        Assert.Equal(2, client.Sent.Count);
        Assert.Equal(DiscordPresenceMapper.HiddenSystemState, client.Sent[^1].State);
        Assert.Single(client.Sent[^1].Buttons);
        Assert.Equal(new DiscordPrivacyOptions(false, false), h.Controller.Privacy);
    }

    [Fact]
    public void Changes_to_state_after_being_turned_off_send_nothing()
    {
        using var h = new Harness();
        h.Controller.Apply(new DiscordSettings { Enabled = true });
        h.Controller.Apply(new DiscordSettings { Enabled = false });
        var sent = h.Clients[0].Sent.Count;

        h.State.StarSystem = "Colonia";

        Assert.Equal(sent, h.Clients[0].Sent.Count);
    }

    [Fact]
    public void Refresh_after_entering_suppression_clears_the_running_presence()
    {
        var suppressed = false;
        var clients = new List<FakeDiscordRpcClient>();
        using var controller = new DiscordPresenceController(new CommanderState { StarSystem = "Sol" }, () =>
        {
            var c = new FakeDiscordRpcClient();
            clients.Add(c);
            return c;
        }, () => suppressed, TimeSpan.FromMinutes(5));
        controller.Apply(new DiscordSettings { Enabled = true });

        suppressed = true;
        controller.Refresh();

        Assert.Equal(1, Assert.Single(clients).ClearCalls);
        Assert.True(controller.IsActive);
    }

    [Fact]
    public void A_throwing_client_factory_falls_back_to_a_no_op()
    {
        using var controller = new DiscordPresenceController(
            new CommanderState(), () => throw new PlatformNotSupportedException());

        var ex = Record.Exception(() => controller.Apply(new DiscordSettings { Enabled = true }));

        Assert.Null(ex);
        Assert.True(controller.IsActive);
    }

    [Fact]
    public void Apply_after_dispose_is_ignored()
    {
        var h = new Harness();
        h.Dispose();

        h.Controller.Apply(new DiscordSettings { Enabled = true });

        Assert.False(h.Controller.IsActive);
        Assert.Empty(h.Clients);
    }
}

/// <summary>The privacy preferences persist in settings.json and survive a restart (#50).</summary>
public class DiscordSettingsPersistenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ednexus-discord-settings-").FullName;
    private string Path => System.IO.Path.Combine(_root, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_fresh_settings_file_defaults_to_everything_on()
    {
        var discord = new SettingsStore(Path).Load().Discord;

        Assert.True(discord.Enabled);
        Assert.True(discord.ShowSystem);
        Assert.True(discord.ShowCommander);
    }

    [Fact]
    public void Privacy_choices_round_trip_through_disk()
    {
        var store = new SettingsStore(Path);
        var settings = store.Load();
        settings.Discord.Enabled = false;
        settings.Discord.ShowSystem = false;
        settings.Discord.ShowCommander = false;
        store.Save(settings);

        var reloaded = new SettingsStore(Path).Load().Discord;

        Assert.False(reloaded.Enabled);
        Assert.False(reloaded.ShowSystem);
        Assert.False(reloaded.ShowCommander);
    }

    [Fact]
    public void A_settings_file_from_before_the_privacy_options_keeps_them_visible()
    {
        File.WriteAllText(Path, """{ "InstallId": "abc", "Discord": { "Enabled": false } }""");

        var discord = new SettingsStore(Path).Load().Discord;

        Assert.False(discord.Enabled);
        Assert.True(discord.ShowSystem);
        Assert.True(discord.ShowCommander);
    }
}
