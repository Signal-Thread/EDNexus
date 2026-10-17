using System.Text.Json;
using System.Text.Json.Serialization;

namespace EDNexus.Core.Twitch;

/// <summary>
/// The wire contract between the desktop app and the Twitch extension frontend: one snapshot of
/// everything the commander has chosen to show viewers, as published through
/// <c>POST /api/update-state</c> and relayed to the extension over Extensions PubSub.
/// </summary>
/// <remarks>
/// A published contract: <c>extension/</c> parses exactly these names. Twitch caps a PubSub message
/// at 5 KiB — hence the short names, the optional sections, and the caps in
/// <see cref="StreamCardMapper"/>.
/// </remarks>
/// <param name="Version">Schema version, so the frontend can refuse a payload it doesn't understand.</param>
/// <param name="At">When this snapshot was taken (UTC), for the frontend's "stale data" indicator.</param>
/// <param name="CargoMore">
/// How many further lots the hold holds beyond those in <paramref name="Cargo"/>, so the card can
/// say "+7 more" instead of quietly presenting a truncated manifest as the whole hold. 0 when
/// nothing was left out.
/// </param>
public sealed record StreamCardSnapshot(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("headline")] string Headline,
    [property: JsonPropertyName("subline")] string? Subline = null,
    [property: JsonPropertyName("cmdr")] StreamCardCommander? Commander = null,
    [property: JsonPropertyName("ship")] StreamCardShip? Ship = null,
    [property: JsonPropertyName("loc")] StreamCardLocation? Location = null,
    [property: JsonPropertyName("carrier")] StreamCardCarrier? Carrier = null,
    [property: JsonPropertyName("exo")] StreamCardExobiology? Exobiology = null,
    [property: JsonPropertyName("mining")] StreamCardMining? Mining = null,
    [property: JsonPropertyName("missions")] StreamCardMissions? Missions = null,
    [property: JsonPropertyName("cargo")] IReadOnlyList<StreamCardCargoItem>? Cargo = null,
    [property: JsonPropertyName("cargoMore")] int CargoMore = 0)
{
    /// <summary>
    /// Null sections are dropped rather than sent as <c>null</c>, so "hidden by the broadcaster" and
    /// "not known yet" look identical to a viewer.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes this snapshot as the <c>state</c> body of a <c>POST /api/update-state</c> call.</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SerializerOptions);

    /// <summary>
    /// Everything a viewer would see, as a comparable string. <see cref="At"/> is excluded — it moves
    /// on every journal line. JSON rather than record equality, because the generated <c>Equals</c>
    /// compares the list-valued sections by reference.
    /// </summary>
    public string ContentFingerprint() =>
        JsonSerializer.Serialize(this with { At = default }, SerializerOptions);
}

/// <summary>Schema version constants for <see cref="StreamCardSnapshot"/>.</summary>
public static class StreamCardSchema
{
    /// <summary>Bump when a field changes meaning or is removed. The frontend refuses a higher one.</summary>
    public const int Version = 1;
}

/// <param name="Credits">Current balance, or null when the broadcaster hides their balance.</param>
public sealed record StreamCardCommander(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("credits")] long? Credits = null,
    [property: JsonPropertyName("ranks")] IReadOnlyList<StreamCardRank>? Ranks = null);

/// <param name="Percent">Progress towards the next tier, 0-100.</param>
public sealed record StreamCardRank(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pct")] int Percent,
    [property: JsonPropertyName("elite")] bool Elite = false);

/// <param name="JumpRange">Maximum jump range (ly) as the game reports it, when a Loadout has been seen.</param>
public sealed record StreamCardShip(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("ident")] string? Ident = null,
    [property: JsonPropertyName("fuel")] double? Fuel = null,
    [property: JsonPropertyName("fuelMax")] double? FuelCapacity = null,
    [property: JsonPropertyName("cargo")] double? CargoTons = null,
    [property: JsonPropertyName("jump")] double? JumpRange = null);

/// <param name="Station">Where they are docked, named the way a commander would name it.</param>
/// <param name="Mode">
/// What the commander is doing, as a stable lower-case token: <c>docked</c>, <c>landed</c>,
/// <c>space</c>, <c>surface</c> (flying low), <c>supercruise</c>, <c>hyperspace</c>, <c>srv</c> or
/// <c>foot</c>. Null until the game has said. Added after <see cref="Docked"/>, which stays for older
/// frontends; an older frontend ignores both new fields.
/// </param>
/// <param name="Status">The same thing in words, for a viewer ("In supercruise", "Jumping to Colonia").</param>
public sealed record StreamCardLocation(
    [property: JsonPropertyName("system")] string? System,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("docked")] bool Docked = false,
    [property: JsonPropertyName("station")] string? Station = null,
    [property: JsonPropertyName("stationType")] string? StationType = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("status")] string? Status = null);

/// <param name="JumpRange">Jump range (ly) at the current load.</param>
public sealed record StreamCardCarrier(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("callsign")] string? Callsign = null,
    [property: JsonPropertyName("fuel")] double? Fuel = null,
    [property: JsonPropertyName("jump")] double? JumpRange = null,
    [property: JsonPropertyName("pendingSystem")] string? PendingSystem = null,
    [property: JsonPropertyName("departsAt")] DateTimeOffset? DepartsAt = null);

public sealed record StreamCardExobiology(
    [property: JsonPropertyName("pendingValue")] long PendingValue = 0,
    [property: JsonPropertyName("pendingCount")] int PendingCount = 0,
    [property: JsonPropertyName("soldValue")] long SoldValue = 0,
    [property: JsonPropertyName("soldCount")] int SoldCount = 0,
    [property: JsonPropertyName("firsts")] int FirstDiscoveries = 0,
    [property: JsonPropertyName("genus")] string? ActiveGenus = null,
    [property: JsonPropertyName("species")] string? ActiveSpecies = null,
    [property: JsonPropertyName("samples")] int SamplesTaken = 0,
    [property: JsonPropertyName("body")] string? BodyName = null,
    [property: JsonPropertyName("signals")] int BodySignals = 0);

public sealed record StreamCardMining(
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("motherlode")] string? Motherlode = null,
    [property: JsonPropertyName("remaining")] double? Remaining = null,
    [property: JsonPropertyName("materials")] IReadOnlyList<StreamCardMiningMaterial>? Materials = null,
    [property: JsonPropertyName("prospected")] int Prospected = 0,
    [property: JsonPropertyName("refined")] int Refined = 0);

/// <param name="Percent">Its proportion of the rock.</param>
public sealed record StreamCardMiningMaterial(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pct")] double Percent);

public sealed record StreamCardMissions(
    [property: JsonPropertyName("active")] int Active = 0,
    [property: JsonPropertyName("cap")] int Cap = 0,
    [property: JsonPropertyName("reward")] long Reward = 0,
    [property: JsonPropertyName("stacks")] IReadOnlyList<StreamCardMissionStack>? Stacks = null);

/// <param name="Kills">Kills needed to clear the stack - the largest single mission, not the sum.</param>
public sealed record StreamCardMissionStack(
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("faction")] string? Faction,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("kills")] int Kills,
    [property: JsonPropertyName("reward")] long Reward);

public sealed record StreamCardCargoItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("t")] int Tons);
