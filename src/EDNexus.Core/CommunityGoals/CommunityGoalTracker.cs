using System.Globalization;
using System.Text.Json;
using EDNexus.Core.Journal;

namespace EDNexus.Core.CommunityGoals;

/// <summary>
/// Feature service that turns the Community Goal journal events into a live picture of every goal
/// this commander is near or has joined: tier reached, contribution, current bonus band and time
/// remaining. It owns its own derived state and never mutates <see cref="State.CommanderState"/>.
/// </summary>
/// <remarks>
/// The game's own <c>CommunityGoal</c> event is a periodic snapshot of every goal active nearby, not
/// a delta, so goals are upserted by <c>CGID</c> — the same shape as <see cref="Missions.MissionTracker"/>'s
/// active-mission dictionary. The snapshot carries no "joined" flag, so that state is only ever set by
/// <c>CommunityGoalJoin</c> and preserved across later snapshots rather than re-derived from them.
/// </remarks>
public sealed class CommunityGoalTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<long, CommunityGoal> _goals = new();

    /// <summary>Raised after any community goal event changes the tracked picture.</summary>
    public event Action? Changed;

    public CommunityGoalTracker(JournalEventBus bus)
    {
        bus.Subscribe("CommunityGoal", OnSnapshot);
        bus.Subscribe("CommunityGoalJoin", OnJoin);
        bus.Subscribe("CommunityGoalReward", OnReward);
        bus.Subscribe("CommunityGoalDiscard", OnDiscard);
    }

    /// <summary>Every goal currently tracked, soonest to expire first.</summary>
    public IReadOnlyList<CommunityGoal> Active
    {
        get { lock (_gate) return _goals.Values.OrderBy(g => g.Expiry ?? DateTimeOffset.MaxValue).ToList(); }
    }

    /// <summary>Forget everything — used by "reset to live data".</summary>
    public void Clear()
    {
        lock (_gate) _goals.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// The periodic snapshot of every goal active nearby. Upserts by CGID: fields the game supplies
    /// win, fields it omits fall back to whatever was already known (join state, reward info, and any
    /// detail a later snapshot happens not to repeat).
    /// </summary>
    private void OnSnapshot(JournalEntry e)
    {
        if (!e.Raw.TryGetProperty("CurrentGoals", out var goals) || goals.ValueKind != JsonValueKind.Array)
            return;

        var changed = false;
        lock (_gate)
        {
            foreach (var item in goals.EnumerateArray())
            {
                if (!TryGetInt64(item, "CGID", out var id)) continue;

                _goals.TryGetValue(id, out var previous);
                var (topTierName, topTierBonus) = ReadTopTier(item);

                _goals[id] = new CommunityGoal(
                    CGID: id,
                    Title: GetString(item, "Title") ?? previous?.Title ?? "Community Goal",
                    System: GetString(item, "SystemName") ?? previous?.System,
                    Station: GetString(item, "MarketName") ?? previous?.Station,
                    Expiry: ReadTime(item, "Expiry") ?? previous?.Expiry,
                    IsComplete: GetBool(item, "IsComplete") ?? previous?.IsComplete ?? false,
                    CurrentTotal: GetInt64(item, "CurrentTotal") ?? previous?.CurrentTotal ?? 0,
                    PlayerContribution: GetInt64(item, "PlayerContribution") ?? previous?.PlayerContribution ?? 0,
                    NumContributors: GetInt64(item, "NumContributors") ?? previous?.NumContributors,
                    TopRankSize: GetInt32(item, "TopRankSize") ?? previous?.TopRankSize,
                    TopTierName: topTierName ?? previous?.TopTierName,
                    TopTierBonus: topTierBonus ?? previous?.TopTierBonus,
                    TierReached: GetString(item, "TierReached") ?? previous?.TierReached,
                    PlayerInTopRank: GetBool(item, "PlayerInTopRank") ?? previous?.PlayerInTopRank,
                    PlayerPercentileBand: GetInt32(item, "PlayerPercentileBand") ?? previous?.PlayerPercentileBand,
                    Joined: previous?.Joined ?? false,
                    Rewarded: previous?.Rewarded ?? false,
                    RewardAmount: previous?.RewardAmount,
                    RewardBonus: previous?.RewardBonus);

                changed = true;
            }
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>Marks a goal as joined, creating a bare-bones entry if no snapshot has arrived yet.</summary>
    private void OnJoin(JournalEntry e)
    {
        if (e.GetInt64("CGID") is not long id) return;

        lock (_gate)
        {
            _goals[id] = _goals.TryGetValue(id, out var existing)
                ? existing with { Joined = true }
                : Stub(id, e, joined: true);
        }

        Changed?.Invoke();
    }

    /// <summary>Completion payout: keeps the goal visible with its reward rather than deleting it.</summary>
    private void OnReward(JournalEntry e)
    {
        if (e.GetInt64("CGID") is not long id) return;

        var reward = e.GetInt64("Reward");
        var bonus = e.GetInt64("Bonus");

        lock (_gate)
        {
            _goals[id] = _goals.TryGetValue(id, out var existing)
                ? existing with { IsComplete = true, Rewarded = true, RewardAmount = reward, RewardBonus = bonus }
                : Stub(id, e, joined: true) with { IsComplete = true, Rewarded = true, RewardAmount = reward, RewardBonus = bonus };
        }

        Changed?.Invoke();
    }

    /// <summary>The commander dropped the goal — it is no longer tracked at all.</summary>
    private void OnDiscard(JournalEntry e)
    {
        if (e.GetInt64("CGID") is not long id) return;

        bool removed;
        lock (_gate) removed = _goals.Remove(id);
        if (removed) Changed?.Invoke();
    }

    private static CommunityGoal Stub(long id, JournalEntry e, bool joined) => new(
        CGID: id,
        Title: e.GetString("Name") ?? "Community Goal",
        System: e.GetString("System"),
        Station: null,
        Expiry: null,
        IsComplete: false,
        CurrentTotal: 0,
        PlayerContribution: 0,
        NumContributors: null,
        TopRankSize: null,
        TopTierName: null,
        TopTierBonus: null,
        TierReached: null,
        PlayerInTopRank: null,
        PlayerPercentileBand: null,
        Joined: joined);

    private static (string? Name, string? Bonus) ReadTopTier(JsonElement item)
    {
        if (!item.TryGetProperty("TopTier", out var tier) || tier.ValueKind != JsonValueKind.Object)
            return (null, null);
        return (GetString(tier, "Name"), GetString(tier, "Bonus"));
    }

    private static DateTimeOffset? ReadTime(JsonElement item, string prop)
        => GetString(item, prop) is { Length: > 0 } s
           && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    // --- Raw JsonElement helpers: CurrentGoals entries are nested elements, not top-level
    // JournalEntry payloads, so the JournalEntry accessors don't apply directly. ---

    private static string? GetString(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? GetInt64(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static int? GetInt32(JsonElement e, string prop)
        => GetInt64(e, prop) is { } n ? (int)n : null;

    private static bool? GetBool(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static bool TryGetInt64(JsonElement e, string prop, out long value)
    {
        if (e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value))
            return true;
        value = 0;
        return false;
    }
}
