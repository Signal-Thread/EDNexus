using EDNexus.Core.Discord;
using EDNexus.Core.State;
using Xunit;

namespace EDNexus.Tests.Discord;

public class DiscordPresenceMapperTests
{
    private static readonly DateTimeOffset SessionStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Docked_state_shows_the_station_display_name()
    {
        var state = new CommanderState
        {
            Docked = true,
            StationName = "Jameson Memorial",
            StarSystem = "Shinrarta Dezhra",
        };

        var payload = DiscordPresenceMapper.Map(state, SessionStart);

        Assert.Equal("Docked at Jameson Memorial", payload.State);
    }

    [Fact]
    public void Docked_at_own_carrier_shows_the_carrier_name_not_the_callsign()
    {
        var state = new CommanderState
        {
            Docked = true,
            StationName = "K7Q-B3L",
            CarrierName = "Nomad's Reach",
            CarrierCallsign = "K7Q-B3L",
        };

        var payload = DiscordPresenceMapper.Map(state, SessionStart);

        Assert.Equal("Docked at Nomad's Reach", payload.State);
    }

    [Fact]
    public void Undocked_with_a_distinct_body_shows_system_and_body()
    {
        var state = new CommanderState { StarSystem = "Colonia", Body = "Colonia 2 a" };

        var payload = DiscordPresenceMapper.Map(state, SessionStart);

        Assert.Equal("Exploring Colonia / Colonia 2 a", payload.State);
    }

    [Fact]
    public void Undocked_with_body_equal_to_system_only_shows_the_system()
    {
        var state = new CommanderState { StarSystem = "Sol", Body = "Sol" };

        var payload = DiscordPresenceMapper.Map(state, SessionStart);

        Assert.Equal("Exploring Sol", payload.State);
    }

    [Fact]
    public void No_known_location_falls_back_to_a_generic_state()
    {
        var payload = DiscordPresenceMapper.Map(new CommanderState(), SessionStart);

        Assert.Equal("In the black", payload.State);
    }

    [Fact]
    public void Cargo_in_the_hold_takes_priority_over_the_ship_name_in_details()
    {
        var state = new CommanderState { Ship = "Type9", ShipIdent = "CMDR1", CargoTons = 128 };

        var payload = DiscordPresenceMapper.Map(state, SessionStart);

        Assert.Equal("Space Trucking: 128t Cargo", payload.Details);
    }

    [Fact]
    public void Empty_hold_shows_ship_and_ident_in_details()
    {
        var state = new CommanderState { Ship = "Anaconda", ShipIdent = "CMDR1" };

        var payload = DiscordPresenceMapper.Map(state, SessionStart);

        Assert.Equal("Flying Anaconda (CMDR1)", payload.Details);
    }

    [Fact]
    public void Ship_without_an_ident_omits_the_parentheses()
    {
        var state = new CommanderState { Ship = "Anaconda" };

        var payload = DiscordPresenceMapper.Map(state, SessionStart);

        Assert.Equal("Flying Anaconda", payload.Details);
    }

    [Fact]
    public void No_ship_and_no_cargo_gives_null_details()
    {
        var payload = DiscordPresenceMapper.Map(new CommanderState(), SessionStart);

        Assert.Null(payload.Details);
    }

    [Fact]
    public void The_ship_is_named_in_the_hover_text_but_the_large_image_stays_the_logo()
    {
        // A per-ship asset key would render blank unless that exact asset was uploaded, so the large
        // image is always the logo that ships with the app; the ship is named in the text.
        var state = new CommanderState { Ship = "Federal Corvette" };

        var payload = DiscordPresenceMapper.Map(state, SessionStart);

        Assert.Equal("ednexus_logo", payload.LargeImageKey);
        Assert.Equal("Federal Corvette", payload.LargeImageText);
    }

    [Fact]
    public void No_ship_falls_back_to_the_ednexus_logo_asset()
    {
        var payload = DiscordPresenceMapper.Map(new CommanderState(), SessionStart);

        Assert.Equal("ednexus_logo", payload.LargeImageKey);
        Assert.Equal("EDNexus", payload.LargeImageText);
    }

    [Theory]
    [InlineData(true, "docked")]
    [InlineData(false, "cruising")]
    public void Small_image_key_reflects_docked_status(bool docked, string expectedKey)
    {
        var state = new CommanderState { Docked = docked };

        var payload = DiscordPresenceMapper.Map(state, SessionStart);

        Assert.Equal(expectedKey, payload.SmallImageKey);
    }

    [Fact]
    public void Get_ednexus_button_is_always_present_and_points_at_the_repo()
    {
        var payload = DiscordPresenceMapper.Map(new CommanderState(), SessionStart);

        Assert.Contains(payload.Buttons, b => b is { Label: "Get EDNexus", Url: "https://github.com/Signal-Thread/EDNexus" });
    }

    [Fact]
    public void Known_commander_name_adds_an_inara_button()
    {
        var state = new CommanderState { Name = "Jameson" };

        var payload = DiscordPresenceMapper.Map(state, SessionStart);

        Assert.Contains(payload.Buttons, b => b.Label == "View on Inara" && b.Url.Contains("Jameson"));
    }

    [Fact]
    public void Unknown_commander_name_omits_the_inara_button()
    {
        var payload = DiscordPresenceMapper.Map(new CommanderState(), SessionStart);

        Assert.Single(payload.Buttons);
    }

    [Fact]
    public void System_entry_time_is_used_for_the_start_timestamp_when_given()
    {
        var systemEnteredAt = SessionStart.AddHours(2);
        var payload = DiscordPresenceMapper.Map(new CommanderState { StarSystem = "Sol" }, SessionStart, systemEnteredAt);

        Assert.Equal(systemEnteredAt, payload.StartedAt);
    }

    [Fact]
    public void Session_start_is_used_for_the_start_timestamp_when_no_system_entry_time_is_given()
    {
        var payload = DiscordPresenceMapper.Map(new CommanderState(), SessionStart);

        Assert.Equal(SessionStart, payload.StartedAt);
    }

    [Fact]
    public void Payloads_that_only_differ_by_start_timestamp_are_equal()
    {
        var state = new CommanderState { Ship = "Anaconda" };
        var a = DiscordPresenceMapper.Map(state, SessionStart);
        var b = DiscordPresenceMapper.Map(state, SessionStart.AddMinutes(5));

        Assert.Equal(a, b);
    }
}
