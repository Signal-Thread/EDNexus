using System.Globalization;
using System.Text.Json;
using EDNexus.Core.Journal;

namespace EDNexus.Core.Missions;

/// <summary>
/// Feature service that turns the mission journal events into a live picture of what the commander
/// is holding: the missions themselves, the stacks they form against a common target, and the
/// hand-ins waiting at each station. It owns its own derived state and never mutates
/// <see cref="State.CommanderState"/>.
/// </summary>
/// <remarks>
/// The game's own <c>Missions</c> snapshot on startup lists ids and little else — no faction, no
/// target, no reward — so it is used only to prune missions that ended while EDNexus was closed,
/// never to invent entries. The detail comes from the <c>MissionAccepted</c> events in the journal,
/// which the replay walks on start-up.
/// </remarks>
public sealed class MissionTracker
{
    /// <summary>The game's cap on simultaneously held missions.</summary>
    public const int MissionCap = 20;

    private readonly object _gate = new();
    private readonly Dictionary<long, Mission> _active = new();

    // Bounties collected per victim faction, which is what progresses every massacre mission against
    // that faction at once. Not the game's own mission counter — see KillsLoggedFor.
    private readonly Dictionary<string, int> _bounties = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after any mission event changes the tracked picture.</summary>
    public event Action? Changed;

    public MissionTracker(JournalEventBus bus)
    {
        bus.Subscribe("MissionAccepted", OnAccepted);
        bus.Subscribe("MissionCompleted", OnEnded);
        bus.Subscribe("MissionAbandoned", OnEnded);
        bus.Subscribe("MissionFailed", OnEnded);
        bus.Subscribe("MissionRedirected", OnRedirected);
        bus.Subscribe("Missions", OnSnapshot);
        bus.Subscribe("Bounty", OnBounty);
        bus.Subscribe("FactionKillBond", OnFactionKillBond);
    }

    /// <summary>Everything currently held, newest first.</summary>
    public IReadOnlyList<Mission> Active
    {
        get { lock (_gate) return _active.Values.OrderByDescending(m => m.Accepted).ToList(); }
    }

    /// <summary>How many missions are held against the game's cap.</summary>
    public int ActiveCount
    {
        get { lock (_gate) return _active.Count; }
    }

    /// <summary>
    /// Kill missions grouped by the faction they target — the stacks. Biggest stack first, since
    /// that is the one worth flying. Missions with no named target faction are not stackable and are
    /// left out.
    /// </summary>
    public IReadOnlyList<MissionStack> Stacks
    {
        get
        {
            lock (_gate)
            {
                return _active.Values
                    .Where(m => m.IsKillMission && !string.IsNullOrWhiteSpace(m.TargetFaction))
                    .GroupBy(m => m.TargetFaction!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new MissionStack(
                        g.Key,
                        g.Select(m => m.TargetType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
                        g.OrderByDescending(m => m.KillCount).ToList()))
                    .OrderByDescending(s => s.Missions.Count)
                    .ThenByDescending(s => s.TotalReward)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Missions grouped by where they are handed in, so one stop clears as many as possible. Groups
    /// that are entirely ready come first; the rest follow by size.
    /// </summary>
    public IReadOnlyList<TurnInGroup> TurnIns
    {
        get
        {
            lock (_gate)
            {
                return _active.Values
                    .Where(m => !string.IsNullOrWhiteSpace(m.DestinationStation) || !string.IsNullOrWhiteSpace(m.DestinationSystem))
                    .GroupBy(m => (m.DestinationSystem, m.DestinationStation))
                    .Select(g => new TurnInGroup(g.Key.DestinationSystem, g.Key.DestinationStation, g.ToList()))
                    .OrderByDescending(g => g.AllReady)
                    .ThenByDescending(g => g.Missions.Count)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Missions grouped by the faction that issued them — the BGS view, since influence moves with
    /// the giver. Most missions first.
    /// </summary>
    public IReadOnlyList<(string Faction, IReadOnlyList<Mission> Missions)> ByGiver
    {
        get
        {
            lock (_gate)
            {
                return _active.Values
                    .Where(m => !string.IsNullOrWhiteSpace(m.GiverFaction))
                    .GroupBy(m => m.GiverFaction, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (g.Key, (IReadOnlyList<Mission>)g.ToList()))
                    .OrderByDescending(g => g.Item2.Count)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Bounties logged against a faction since EDNexus started watching. This is a count of kills
    /// seen, not the game's mission progress: the journal never reports how far along a massacre
    /// mission is, and kills made before the mission was accepted are counted here too. Useful as a
    /// running tally for a stack, not as an authoritative "N to go".
    /// </summary>
    public int KillsLoggedFor(string? faction)
    {
        if (string.IsNullOrWhiteSpace(faction)) return 0;
        lock (_gate) return _bounties.TryGetValue(faction, out var n) ? n : 0;
    }

    /// <summary>Forget everything — used by "reset to live data".</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _active.Clear();
            _bounties.Clear();
        }

        Changed?.Invoke();
    }

    private void OnAccepted(JournalEntry e)
    {
        if (e.GetInt64("MissionID") is not long id) return;

        var mission = new Mission(
            MissionId: id,
            Title: e.GetString("LocalisedName") ?? e.GetString("Name") ?? "Mission",
            GiverFaction: e.GetString("Faction") ?? "",
            TargetFaction: e.GetString("TargetFaction"),
            TargetType: e.GetLocalised("TargetType") ?? e.GetString("TargetType"),
            KillCount: (int)(e.GetInt64("KillCount") ?? 0),
            DestinationSystem: e.GetString("DestinationSystem"),
            DestinationStation: e.GetString("DestinationStation"),
            Expiry: ReadTime(e, "Expiry"),
            Reward: e.GetInt64("Reward") ?? 0,
            Influence: e.GetString("Influence"),
            Reputation: e.GetString("Reputation"),
            IsWing: e.GetBool("Wing") ?? false,
            Accepted: e.Timestamp);

        lock (_gate) _active[id] = mission;
        Changed?.Invoke();
    }

    /// <summary>Completed, abandoned or failed — all three simply end the mission.</summary>
    private void OnEnded(JournalEntry e)
    {
        if (e.GetInt64("MissionID") is not long id) return;

        bool removed;
        lock (_gate) removed = _active.Remove(id);
        if (removed) Changed?.Invoke();
    }

    /// <summary>
    /// A redirect means the objective is done and the game has pointed the commander at the hand-in,
    /// which is the only signal the journal gives that a kill mission is finished.
    /// </summary>
    private void OnRedirected(JournalEntry e)
    {
        if (e.GetInt64("MissionID") is not long id) return;

        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var mission)) return;
            _active[id] = mission with
            {
                DestinationSystem = e.GetString("NewDestinationSystem") ?? mission.DestinationSystem,
                DestinationStation = e.GetString("NewDestinationStation") ?? mission.DestinationStation,
                ReadyToTurnIn = true,
            };
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The start-up snapshot lists the ids the game still considers active. It carries no detail, so
    /// it is only used to drop missions that ended while EDNexus was not running — never to add one,
    /// which would put a mission on the card with no faction, target or reward.
    /// </summary>
    private void OnSnapshot(JournalEntry e)
    {
        var live = new HashSet<long>();
        foreach (var section in new[] { "Active", "Complete", "Failed" })
        {
            if (!e.Raw.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in arr.EnumerateArray())
                if (item.TryGetProperty("MissionID", out var v) && v.TryGetInt64(out var id))
                    live.Add(id);
        }

        if (live.Count == 0) return;

        bool changed;
        lock (_gate)
        {
            var stale = _active.Keys.Where(id => !live.Contains(id)).ToList();
            foreach (var id in stale) _active.Remove(id);
            changed = stale.Count > 0;
        }

        if (changed) Changed?.Invoke();
    }

    private void OnBounty(JournalEntry e)
    {
        if (e.GetString("VictimFaction") is not { Length: > 0 } faction) return;

        lock (_gate) _bounties[faction] = _bounties.TryGetValue(faction, out var n) ? n + 1 : 1;
        Changed?.Invoke();
    }

    /// <summary>
    /// A conflict-zone kill paid out as a faction kill bond rather than a bounty — on-foot and space
    /// CZs report this way instead. Tallied into the same running count as <see cref="OnBounty"/>,
    /// keyed by the ship destroyed's faction, since that is what a massacre stack cares about.
    /// </summary>
    private void OnFactionKillBond(JournalEntry e)
    {
        if (e.GetString("VictimFaction") is not { Length: > 0 } faction) return;

        lock (_gate) _bounties[faction] = _bounties.TryGetValue(faction, out var n) ? n + 1 : 1;
        Changed?.Invoke();
    }

    private static DateTimeOffset? ReadTime(JournalEntry e, string prop)
        => e.GetString(prop) is { Length: > 0 } s
           && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}
