using System.Text.Json;
using EDNexus.Core.Journal;
using EDNexus.Core.State;

namespace EDNexus.Core.Exobio;

/// <summary>
/// Feature service that turns the scanner and sampler events into a live exobiology picture: which
/// bodies carry biological signals, how far each three-sample run has progressed, and what the
/// session has earned or is still carrying. It owns its own derived state — it never mutates
/// <see cref="CommanderState"/>, only reads it to name the body when an event omits one.
/// </summary>
public sealed class ExobiologyTracker
{
    private readonly CommanderState? _state;
    private readonly ExobiologyCatalog _catalog;
    private readonly object _gate = new();

    private readonly Dictionary<BodyKey, BodyBioSignals> _signals = new();
    private readonly Dictionary<(BodyKey Body, string Species), OrganicScan> _scans = new();
    private readonly Dictionary<BodyKey, string> _bodyNames = new();
    private readonly Dictionary<BodyKey, BodyEnvironment> _environments = new();

    private BodyKey? _currentBody;
    private long _soldValue;
    private long _soldBonus;
    private int _soldCount;

    /// <summary>Raised after any scanner/sampler event changes the tracked picture.</summary>
    public event Action? Changed;

    public ExobiologyTracker(JournalEventBus bus, CommanderState? state = null, ExobiologyCatalog? catalog = null)
    {
        _state = state;
        _catalog = catalog ?? ExobiologyCatalog.Default;

        bus.Subscribe("Scan", OnScan);
        bus.Subscribe("SAASignalsFound", OnSaaSignals);
        bus.Subscribe("FSSBodySignals", OnFssSignals);
        bus.Subscribe("ScanOrganic", OnScanOrganic);
        bus.Subscribe("SellOrganicData", OnSellOrganicData);
        bus.Subscribe("CodexEntry", OnCodexEntry);
        bus.Subscribe("ApproachBody", OnApproachBody);
        bus.Subscribe("Touchdown", OnApproachBody);
        bus.Subscribe("LeaveBody", OnLeaveBody);
        bus.Subscribe("Died", OnDied);
    }

    /// <summary>Every body known to carry biological signals, richest estimate first.</summary>
    public IReadOnlyList<BodyBioSignals> Bodies
    {
        get
        {
            lock (_gate)
                return _signals.Values
                    .Where(b => b.SignalCount > 0)
                    .OrderByDescending(b => b.ValueRange?.Max ?? 0)
                    .ThenBy(b => b.BodyName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
    }

    /// <summary>
    /// Signals for the body the commander is currently at — the "bio signals here" summary. Null
    /// when away from a body, or when the body has been neither FSS- nor DSS-scanned.
    /// </summary>
    public BodyBioSignals? CurrentBody
    {
        get
        {
            lock (_gate)
                return _currentBody is BodyKey k && _signals.TryGetValue(k, out var b) ? b : null;
        }
    }

    /// <summary>Sample runs in progress or finished, most recently touched first.</summary>
    public IReadOnlyList<OrganicScan> Scans
    {
        get { lock (_gate) return _scans.Values.OrderByDescending(s => s.Updated).ToList(); }
    }

    /// <summary>The run currently under way — the one the suit is part-way through.</summary>
    public OrganicScan? ActiveScan
    {
        get
        {
            lock (_gate)
                return _scans.Values
                    .Where(s => !s.Complete)
                    .OrderByDescending(s => s.Updated)
                    .FirstOrDefault();
        }
    }

    /// <summary>This session's earnings: analysed-but-unsold data, and what selling has paid.</summary>
    public ExobiologySession Session
    {
        get
        {
            lock (_gate)
                return new ExobiologySession(
                    _scans.Values.Where(s => s.Complete).OrderByDescending(s => s.Value).ToList(),
                    _soldValue, _soldBonus, _soldCount);
        }
    }

    // --- Scanners: which bodies carry signals. ---

    /// <summary>
    /// A body scan. For planets it records the physics — class, atmosphere, gravity, temperature,
    /// volcanism and whether anyone discovered it first — so the catalog can predict what grows
    /// there. The FSS usually logs a body's signals just before its scan, so a body already carrying
    /// signals has its predictions filled in here.
    /// </summary>
    private void OnScan(JournalEntry e)
    {
        if (KeyOf(e) is not BodyKey key) return;
        if (e.GetString("PlanetClass") is not { Length: > 0 } planetClass) return;   // stars, belts

        var env = new BodyEnvironment(
            PlanetClass: planetClass,
            Atmosphere: e.GetString("Atmosphere") ?? "",
            AtmosphereType: e.GetString("AtmosphereType"),
            SurfaceGravityG: (e.GetDouble("SurfaceGravity") ?? 0) / StandardGravity,
            SurfaceTemperatureK: e.GetDouble("SurfaceTemperature") ?? 0,
            Landable: e.GetBool("Landable") ?? false,
            // Absent on pre-3.3 journals; assume discovered rather than promise a bonus.
            WasDiscovered: e.GetBool("WasDiscovered") ?? true,
            Volcanism: e.GetString("Volcanism"));

        bool changed;
        lock (_gate)
        {
            _environments[key] = env;
            if (e.GetString("BodyName") is { Length: > 0 } name)
                _bodyNames[key] = name;

            // Most scanned planets carry no biology; only redraw when one that does gains its physics.
            changed = _signals.TryGetValue(key, out var existing);
            if (changed)
                _signals[key] = existing! with
                {
                    Environment = env,
                    Predictions = _catalog.Predict(env, existing.Genera),
                };
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>The journal reports surface gravity in m/s²; the spawn rules are written in g.</summary>
    private const double StandardGravity = 9.80665;

    /// <summary>
    /// A surface (DSS) mapping. Unlike the FSS pass this names the genera present, which is the
    /// first point at which the body's biology can be valued.
    /// </summary>
    private void OnSaaSignals(JournalEntry e)
    {
        if (KeyOf(e) is not BodyKey key) return;

        var genera = new List<BioGenus>();
        if (e.Raw.TryGetProperty("Genuses", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (_catalog.Genus(ReadString(item, "Genus")) is { } genus)
                    genera.Add(genus);

        Record(key, e, BiologicalCount(e), genera, mapped: true);
    }

    /// <summary>An FSS pass: a signal count only, with no clue which genera they are.</summary>
    private void OnFssSignals(JournalEntry e)
    {
        if (KeyOf(e) is not BodyKey key) return;
        var count = BiologicalCount(e);
        if (count == 0) return;

        lock (_gate)
        {
            // Never let an FSS count overwrite genera a DSS pass already established.
            if (_signals.TryGetValue(key, out var existing) && existing.Mapped) return;
        }

        Record(key, e, count, Array.Empty<BioGenus>(), mapped: false);
    }

    private void Record(BodyKey key, JournalEntry e, int count, IReadOnlyList<BioGenus> genera, bool mapped)
    {
        var name = e.GetString("BodyName") ?? _state?.Body ?? "Unknown body";
        lock (_gate)
        {
            _bodyNames[key] = name;
            // With the physics known, predict — narrowed to the genera when a DSS pass named them.
            var env = _environments.GetValueOrDefault(key);
            var predictions = env is null ? Array.Empty<BioPrediction>() : _catalog.Predict(env, genera);

            _signals[key] = new BodyBioSignals(key, name, count, genera, mapped, predictions, env);
        }
        Changed?.Invoke();
    }

    /// <summary>The biological entry in a signals array, or 0 when the body has none.</summary>
    private static int BiologicalCount(JournalEntry e)
    {
        if (!e.Raw.TryGetProperty("Signals", out var arr) || arr.ValueKind != JsonValueKind.Array) return 0;

        foreach (var item in arr.EnumerateArray())
        {
            var type = ReadString(item, "Type");
            if (type is null || !type.Contains("Biological", StringComparison.OrdinalIgnoreCase)) continue;
            if (item.TryGetProperty("Count", out var c) && c.TryGetInt32(out var n)) return n;
        }
        return 0;
    }

    // --- Sampler: the three-scan progression. ---

    /// <summary>
    /// One press of the sampler. The suit takes three samples before the data is complete, and the
    /// journal names the stage rather than numbering it: <c>Log</c> opens the run, <c>Sample</c> is
    /// the middle, and <c>Analyse</c> closes it. A run can be restarted on a different body, so the
    /// progression is tracked per body-and-species rather than globally.
    /// </summary>
    private void OnScanOrganic(JournalEntry e)
    {
        if (KeyOf(e) is not BodyKey key) return;

        var speciesSymbol = e.GetString("Species");
        var speciesName = e.GetLocalised("Species") ?? speciesSymbol ?? "Unknown species";
        var species = _catalog.Resolve(speciesSymbol, e.GetLocalised("Species"));
        var genusName = e.GetLocalised("Genus") ?? species?.Genus ?? "";
        // How far to walk before the next sample counts. Unknown (0) rather than a guess for a new genus.
        var sampleDistance = _catalog.Genus(e.GetString("Genus"))?.SampleDistanceMeters
            ?? species?.SampleDistanceMeters
            ?? 0;

        var samples = e.GetString("ScanType")?.ToLowerInvariant() switch
        {
            "log" => 1,
            "sample" => 2,
            "analyse" or "analyze" => 3,
            _ => 1,
        };

        var indexKey = (key, speciesSymbol ?? speciesName);
        lock (_gate)
        {
            // "Sample" repeats when a run is resumed, so never let a later event walk progress back.
            var prior = _scans.GetValueOrDefault(indexKey);
            var reached = Math.Max(samples, prior?.Samples ?? 0);

            _scans[indexKey] = new OrganicScan(
                key,
                BodyNameFor(key),
                species,
                species?.Name ?? speciesName,
                genusName,
                reached,
                e.Timestamp,
                sampleDistance);
        }
        Changed?.Invoke();
    }

    // --- Vista Genomics: what the data actually paid. ---

    /// <summary>
    /// Data sold. The event reports what each sample fetched, including the first-logged bonus, so
    /// the session tally uses real credits rather than the catalog estimate. Sold scans leave the
    /// pending list.
    /// </summary>
    private void OnSellOrganicData(JournalEntry e)
    {
        if (!e.Raw.TryGetProperty("BioData", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        var soldSpecies = new List<string>();
        long value = 0, bonus = 0;
        var count = 0;

        foreach (var item in arr.EnumerateArray())
        {
            value += ReadInt64(item, "Value");
            bonus += ReadInt64(item, "Bonus");
            count++;
            if (ReadString(item, "Species") is { } s) soldSpecies.Add(s);
        }

        lock (_gate)
        {
            _soldValue += value + bonus;
            _soldBonus += bonus;
            _soldCount += count;

            foreach (var symbol in soldSpecies)
                foreach (var k in _scans.Where(kv => kv.Value.Complete && kv.Key.Species == symbol).Select(kv => kv.Key).ToList())
                    _scans.Remove(k);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// A codex entry. It carries no value, but <c>IsNewEntry</c> is the journal's own word on
    /// whether this was a first discovery — worth surfacing because it is what the five-times
    /// bonus is paid for.
    /// </summary>
    private void OnCodexEntry(JournalEntry e)
    {
        if (e.GetBool("IsNewEntry") is not true) return;
        if (e.GetString("SubCategory") is not { } sub || !sub.Contains("Organic", StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate) NewDiscoveries++;
        Changed?.Invoke();
    }

    /// <summary>First-discovery codex entries seen this session — each one earns the bonus when sold.</summary>
    public int NewDiscoveries { get; private set; }

    // --- Where the commander is, so the card can lead with "signals here". ---

    private void OnApproachBody(JournalEntry e)
    {
        if (KeyOf(e) is not BodyKey key) return;
        lock (_gate)
        {
            _currentBody = key;
            if (e.GetString("Body") is { Length: > 0 } name) _bodyNames[key] = name;
        }
        Changed?.Invoke();
    }

    private void OnLeaveBody(JournalEntry e)
    {
        lock (_gate) _currentBody = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// The commander died. Biological samples are carried on the suit, and (per the game's rules as
    /// documented by Frontier and the community) dying forfeits everything unsold: runs part-way through
    /// and finished-but-unsold data alike. Without this they would sit in the pending list, and in the
    /// "carrying" total on the stream card, until the next sale that could never happen. The sold
    /// tally is untouched: that money was already banked.
    /// </summary>
    private void OnDied(JournalEntry e)
    {
        lock (_gate)
        {
            _scans.Clear();
            _currentBody = null;
        }
        Changed?.Invoke();
    }

    // --- Helpers. ---

    /// <summary>
    /// Build the body key an event refers to. Bio events name the body two ways: the scanners use
    /// <c>BodyID</c>, the sampler uses <c>Body</c> for the same number.
    /// </summary>
    private static BodyKey? KeyOf(JournalEntry e)
    {
        if (e.GetInt64("SystemAddress") is not long system) return null;
        var bodyId = e.GetInt64("BodyID") ?? e.GetInt64("Body");
        return bodyId is long id ? new BodyKey(system, (int)id) : null;
    }

    private string BodyNameFor(BodyKey key)
        => _bodyNames.GetValueOrDefault(key) ?? _state?.Body ?? "Unknown body";

    private static string? ReadString(JsonElement item, string prop)
        => item.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static long ReadInt64(JsonElement item, string prop)
        => item.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : 0;
}
