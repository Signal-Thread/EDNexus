using System.Text.Json;
using EDNexus.Core.Colonisation;
using EDNexus.Core.Journal;

namespace EDNexus.Core.Mining;

/// <summary>
/// Feature service tracking prospected asteroids for the current mining session. Fed by the journal's
/// <c>ProspectedAsteroid</c> event (one per prospector-limpet hit); keeps a rolling history so the
/// mining card can show what has turned up as the commander works a hotspot, not just the latest rock.
/// </summary>
public sealed class MiningTracker
{
    /// <summary>Bounds memory for a very long session; the useful window is the last handful of rocks anyway.</summary>
    private const int MaxHistory = 60;

    private readonly object _gate = new();
    private readonly List<ProspectResult> _history = new();

    // Not capped like _history: this is never rendered directly, only diffed by count (see
    // MiningCardViewModel), so trimming it would shift indices out from under that diff. A session's
    // worth of refined units — at most a full cargo hold, a few hundred — costs nothing to keep.
    private readonly List<RefinedUnit> _refined = new();

    /// <summary>Status.json <c>Flags</c> bit set while the commander is driving an SRV.</summary>
    private const long StatusFlagInSrv = 0x0400_0000;

    // The system the commander is in (from arrival events) and the SRV's latest surface fix (from
    // Status.json), so a unit refined in an SRV can be pinned to where it was mined.
    private long? _systemAddress;
    private string? _starSystem;
    private MiningFix? _srvPosition;

    /// <summary>Raised after a new prospect is recorded or the history is cleared.</summary>
    public event Action? Changed;

    public MiningTracker(JournalEventBus bus)
    {
        bus.Subscribe("ProspectedAsteroid", OnProspected);
        bus.Subscribe("MiningRefined", OnRefined);
        bus.Subscribe("Status", OnStatus);
        bus.Subscribe("Location", OnArrival);
        bus.Subscribe("FSDJump", OnArrival);
        bus.Subscribe("CarrierJump", OnArrival);
    }

    /// <summary>Every prospect this session, oldest first.</summary>
    public IReadOnlyList<ProspectResult> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    /// <summary>The most recently prospected rock, or null before the first hit this session.</summary>
    public ProspectResult? Latest
    {
        get { lock (_gate) return _history.Count > 0 ? _history[^1] : null; }
    }

    /// <summary>Every unit refined into cargo this session, oldest first.</summary>
    public IReadOnlyList<RefinedUnit> Refined
    {
        get { lock (_gate) return _refined.ToList(); }
    }

    /// <summary>Drop the session's history — used by "reset to live" and a commander-requested clear.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _history.Clear();
            _refined.Clear();
        }
        Changed?.Invoke();
    }

    private void OnRefined(JournalEntry e)
    {
        // Same reasoning as OnProspected: a warm-up replay must not re-add units a prior run of the
        // app already recorded into the persisted daily total.
        if (e.IsHistorical) return;

        var raw = e.GetString("Type");
        var localised = e.GetLocalised("Type");
        var symbol = CommodityName.Canonicalize(raw ?? localised);
        if (symbol.Length == 0) return;

        lock (_gate) _refined.Add(new RefinedUnit(e.Timestamp, symbol, localised ?? raw ?? symbol, _srvPosition));
        Changed?.Invoke();
    }

    private void OnArrival(JournalEntry e)
    {
        lock (_gate)
        {
            _systemAddress = e.GetInt64("SystemAddress") ?? _systemAddress;
            _starSystem = e.GetString("StarSystem") ?? _starSystem;
            _srvPosition = null;
        }
    }

    /// <summary>
    /// Keep the SRV's latest surface fix. Status.json is rewritten as the commander drives, so the fix
    /// in hand when a <c>MiningRefined</c> arrives is within metres of the deposit. Any update that
    /// isn't in an SRV with a position (back aboard the ship, in space) clears it, so ship refining is
    /// never pinned to a stale planetary position.
    /// </summary>
    private void OnStatus(JournalEntry e)
    {
        var inSrv = e.GetInt64("Flags") is long flags && (flags & StatusFlagInSrv) != 0;
        // Same parsing (and range/finite checks) as the commander's surface position.
        var fix = inSrv ? Surface.SurfacePosition.FromStatus(e, null) : null;
        var body = e.GetString("BodyName");

        lock (_gate)
        {
            _srvPosition = fix is not null && !string.IsNullOrEmpty(body)
                ? new MiningFix(_systemAddress, _starSystem, body, fix.Latitude, fix.Longitude, fix.PlanetRadius)
                : null;
        }
    }

    private void OnProspected(JournalEntry e)
    {
        // A warm-up replay of days-old prospects has no bearing on the mining spot the commander is
        // sitting in right now, and would otherwise dump a stale history onto a fresh session.
        if (e.IsHistorical) return;

        var materials = new List<ProspectedMaterial>();
        if (e.Raw.TryGetProperty("Materials", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var raw = ReadString(item, "Name");
                var localised = ReadString(item, "Name_Localised");
                var symbol = CommodityName.Canonicalize(raw ?? localised);
                if (symbol.Length == 0) continue;

                var proportion = item.TryGetProperty("Proportion", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? d : 0;
                materials.Add(new ProspectedMaterial(localised ?? raw ?? symbol, symbol, proportion));
            }
        }
        materials.Sort((a, b) => b.Proportion.CompareTo(a.Proportion));

        var motherlodeRaw = e.GetString("MotherlodeMaterial");
        var motherlodeName = e.GetLocalised("MotherlodeMaterial");
        var motherlodeSymbol = CommodityName.Canonicalize(motherlodeRaw);

        var result = new ProspectResult(
            Timestamp: e.Timestamp,
            Materials: materials,
            MotherlodeName: motherlodeSymbol.Length > 0 ? motherlodeName : null,
            MotherlodeSymbol: motherlodeSymbol.Length > 0 ? motherlodeSymbol : null,
            Content: ParseContentLevel(e.GetString("Content")),
            Remaining: e.GetDouble("Remaining") ?? 100.0);

        lock (_gate)
        {
            _history.Add(result);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The raw field is an internal token (<c>$AsteroidMaterialContent_High;</c>), not the plain
    /// "Low"/"Medium"/"High" the manual implies — parsing it directly (rather than the localised text)
    /// keeps the level locale-independent, since the app renders its own English labels regardless of
    /// the game client's language.
    /// </summary>
    private static string ParseContentLevel(string? raw)
    {
        const string prefix = "$AsteroidMaterialContent_";
        if (raw is null) return "";
        var s = raw.Trim();
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        return s.TrimEnd(';');
    }

    private static string? ReadString(JsonElement item, string prop)
        => item.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
