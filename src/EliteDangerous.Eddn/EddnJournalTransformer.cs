using System.Text.Json;
using System.Text.Json.Nodes;

namespace EliteDangerous.Eddn;

/// <summary>
/// Turns raw Elite Dangerous journal and status JSON into ready-to-upload <see cref="EddnMessage"/>
/// envelopes, applying every EDDN protocol rule along the way: routing each event to the schema that
/// accepts it, <c>_Localised</c> stripping, removal of commander-private fields, and location
/// augmentation with a cross-check that drops anything it can't reconcile ("no data is better than
/// bad data"). Event-specific schemas (codexentry, approachsettlement, fss*, navbeaconscan, …) are
/// <c>additionalProperties: false</c>, so those messages are built by projecting an allowlist of keys.
/// </summary>
public sealed class EddnJournalTransformer
{
    private readonly EddnClientOptions _options;

    public EddnJournalTransformer(EddnClientOptions options) => _options = options;

    // Top-level journal fields that are commander-private and must never reach EDDN.
    private static readonly HashSet<string> PrivateJournalKeys = new(StringComparer.Ordinal)
    {
        "ActiveFine", "CockpitBreach", "BoostUsed", "FuelLevel", "FuelUsed", "JumpDist",
        "Latitude", "Longitude", "Altitude", "Heading", "Wanted",
    };

    // Commander-private fields found inside each Factions[] entry (FSDJump/Location/CarrierJump), not
    // at the root. The journal schema disallows them there, so any left in get the upload rejected.
    private static readonly HashSet<string> PrivateFactionKeys = new(StringComparer.Ordinal)
    {
        "MyReputation", "SquadronFaction", "HappiestSystem", "HomeSystem",
    };

    // Cosmetic "modules" that are not real outfitting stock.
    private static readonly string[] CosmeticFragments =
    {
        "bobble", "decal", "paintjob", "nameplate", "enginecustomisation", "voicepack",
        "weaponcustomisation", "shipkit", "string_lights", "spoiler", "wings",
    };

    /// <summary>The shape of one event-specific schema: which journal keys it accepts and requires.</summary>
    private sealed record Projection(string SchemaRef, string NameKey, string[] Allowed, string[] Required);

    // CodexEntry: IsNewEntry / NewTraitsDiscovered are disallowed by the schema and so are not allowed
    // here. BodyID/BodyName are deliberately omitted too: the schema only wants them when cross-checked
    // against Status.json, which the raw journal event cannot provide. Latitude/Longitude are optional and
    // withheld, as a commander's surface position is not needed to identify the codex entry.
    private static readonly Projection CodexEntry = new(EddnSchemas.CodexEntry, "System",
        new[] { "timestamp", "event", "SystemAddress", "Name", "Region", "EntryID", "Category", "SubCategory", "NearestDestination", "VoucherAmount", "Traits" },
        new[] { "timestamp", "event", "System", "StarPos", "SystemAddress", "EntryID" });

    // ApproachSettlement keeps Latitude/Longitude (required here, unlike on the journal schema).
    private static readonly Projection ApproachSettlement = new(EddnSchemas.ApproachSettlement, "StarSystem",
        new[]
        {
            "timestamp", "event", "SystemAddress", "Name", "MarketID", "BodyID", "BodyName", "Latitude", "Longitude",
            "StationGovernment", "StationAllegiance", "StationEconomies", "StationFaction", "StationServices", "StationEconomy",
        },
        new[] { "timestamp", "event", "StarSystem", "StarPos", "SystemAddress", "Name", "BodyID", "BodyName", "Latitude", "Longitude" });

    // FSSDiscoveryScan: Progress is disallowed (a per-commander scan percentage).
    private static readonly Projection FssDiscoveryScan = new(EddnSchemas.FssDiscoveryScan, "SystemName",
        new[] { "timestamp", "event", "SystemAddress", "BodyCount", "NonBodyCount" },
        new[] { "timestamp", "event", "SystemName", "StarPos", "SystemAddress", "BodyCount", "NonBodyCount" });

    private static readonly Projection FssAllBodiesFound = new(EddnSchemas.FssAllBodiesFound, "SystemName",
        new[] { "timestamp", "event", "SystemAddress", "Count" },
        new[] { "timestamp", "event", "SystemName", "StarPos", "SystemAddress", "Count" });

    private static readonly Projection FssBodySignals = new(EddnSchemas.FssBodySignals, "StarSystem",
        new[] { "timestamp", "event", "SystemAddress", "BodyID", "BodyName", "Signals" },
        new[] { "timestamp", "event", "StarSystem", "StarPos", "SystemAddress", "BodyID", "Signals" });

    private static readonly Projection NavBeaconScan = new(EddnSchemas.NavBeaconScan, "StarSystem",
        new[] { "timestamp", "event", "SystemAddress", "NumBodies" },
        new[] { "timestamp", "event", "StarSystem", "StarPos", "SystemAddress", "NumBodies" });

    private static readonly Projection ScanBaryCentre = new(EddnSchemas.ScanBaryCentre, "StarSystem",
        new[]
        {
            "timestamp", "event", "SystemAddress", "BodyID", "SemiMajorAxis", "Eccentricity", "OrbitalInclination",
            "Periapsis", "OrbitalPeriod", "AscendingNode", "MeanAnomaly",
        },
        new[] { "timestamp", "event", "StarSystem", "StarPos", "SystemAddress", "BodyID" });

    /// <summary>
    /// Builds an EDDN message for <paramref name="raw"/>, or returns <c>null</c> if the event is not
    /// uploadable (unknown event, missing required data that can't be augmented, etc.). Feed the same
    /// event through <see cref="EddnState.Observe"/> first so augmentation has current context.
    /// A lone <c>FSSSignalDiscovered</c> becomes a one-signal message; callers that see a run of them
    /// should coalesce with <see cref="TransformFssSignals"/> as the schema asks.
    /// </summary>
    public EddnMessage? Transform(JsonElement raw, EddnState state)
    {
        if (!raw.TryGetProperty("event", out var evEl) || evEl.ValueKind != JsonValueKind.String)
            return null;

        return evEl.GetString() switch
        {
            "Market" => BuildCommodity(raw, state),
            "Outfitting" => BuildOutfitting(raw, state),
            "Shipyard" => BuildShipyard(raw, state),
            "FCMaterials" => BuildFcMaterials(raw, state),
            "NavRoute" => BuildNavRoute(raw, state),
            "CodexEntry" => BuildProjected(raw, state, CodexEntry),
            "ApproachSettlement" => BuildProjected(raw, state, ApproachSettlement),
            "FSSDiscoveryScan" => BuildProjected(raw, state, FssDiscoveryScan),
            "FSSAllBodiesFound" => BuildProjected(raw, state, FssAllBodiesFound),
            "FSSBodySignals" => BuildProjected(raw, state, FssBodySignals),
            "NavBeaconScan" => BuildProjected(raw, state, NavBeaconScan),
            "ScanBaryCentre" => BuildProjected(raw, state, ScanBaryCentre),
            "FSSSignalDiscovered" => TransformFssSignals(new[] { raw }, state),
            var ev when ev is not null && EddnSchemas.JournalWhitelist.Contains(ev) => BuildJournal(raw, state),
            _ => null,
        };
    }

    // --- Journal schema: near-passthrough of the event with strip + augment. ---

    private EddnMessage? BuildJournal(JsonElement raw, EddnState state)
    {
        var msg = ToObject(raw);
        StripLocalised(msg);
        foreach (var key in PrivateJournalKeys) msg.Remove(key);
        StripPrivateFactionKeys(msg);

        if (!Augment(msg, state, "StarSystem")) return null;
        AddGameFlags(msg, state);

        return Envelope(EddnSchemas.Journal, msg, state);
    }

    /// <summary>
    /// Removes <see cref="PrivateFactionKeys"/> from every entry of a <c>Factions</c> array, whichever
    /// event carries it. A missing or non-array <c>Factions</c>, or a non-object entry, is left as-is.
    /// </summary>
    private static void StripPrivateFactionKeys(JsonObject msg)
    {
        if (msg["Factions"] is not JsonArray factions) return;
        foreach (var faction in factions.OfType<JsonObject>())
            foreach (var key in PrivateFactionKeys) faction.Remove(key);
    }

    /// <summary>
    /// Ensures the system name (under <paramref name="nameKey"/>), SystemAddress and StarPos are present
    /// and consistent with the tracked location; false = drop. A key that is present but the wrong type
    /// drops the message rather than being "repaired" from tracked state.
    /// </summary>
    private static bool Augment(JsonObject msg, EddnState state, string nameKey)
    {
        if (!TryReadString(msg, nameKey, out var mSys)) return false;
        if (!TryReadInt64(msg, "SystemAddress", out var mAddr)) return false;
        if (!TryReadPos(msg, "StarPos", out var mPos)) return false;

        // If the message carries its own identity, it must agree with our tracked location before we
        // borrow anything from state — otherwise we could stamp an event with the wrong system.
        var consistent =
            (mSys is null || state.StarSystem is null || string.Equals(mSys, state.StarSystem, StringComparison.Ordinal)) &&
            (mAddr is null || state.SystemAddress is null || mAddr == state.SystemAddress);

        var sys = mSys ?? (consistent ? state.StarSystem : null);
        var addr = mAddr ?? (consistent ? state.SystemAddress : null);
        var pos = mPos ?? (consistent ? state.StarPos : null);
        if (sys is null || addr is null || pos is null) return false;

        msg[nameKey] = sys;
        msg["SystemAddress"] = addr;
        msg["StarPos"] = new JsonArray(pos[0], pos[1], pos[2]);
        return true;
    }

    // --- Event-specific schemas: project an allowlist of the raw event's keys, then augment. ---

    private EddnMessage? BuildProjected(JsonElement raw, EddnState state, Projection p)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;

        var msg = new JsonObject();
        foreach (var prop in raw.EnumerateObject())
        {
            // The event's own claim to a location is kept (cross-checked by Augment); everything else
            // must be on the schema's allowlist, because these schemas reject unknown keys.
            if (prop.Name == p.NameKey || prop.Name == "StarPos" || Array.IndexOf(p.Allowed, prop.Name) >= 0)
                msg[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
        StripLocalised(msg);

        if (!Augment(msg, state, p.NameKey)) return null;
        foreach (var key in p.Required)
            if (!HasValue(msg[key])) return null;

        AddGameFlags(msg, state);
        return Envelope(p.SchemaRef, msg, state);
    }

    /// <summary>
    /// Builds one <c>fsssignaldiscovered</c> message from a contiguous run of <c>FSSSignalDiscovered</c>
    /// events (the schema asks for these to be coalesced into a single <c>signals</c> array). Each signal
    /// is cross-checked against the tracked system and dropped on a mismatch, <c>$USS_Type_MissionTarget;</c>
    /// signals are dropped (only the mission's commander has any use for them), and the ephemeral,
    /// slightly-personal <c>TimeRemaining</c> is removed. Returns <c>null</c> if nothing survives.
    /// </summary>
    public EddnMessage? TransformFssSignals(IReadOnlyList<JsonElement> events, EddnState state)
    {
        if (state.StarSystem is null || state.SystemAddress is null || state.StarPos is null) return null;

        var signals = new JsonArray();
        string? firstTimestamp = null;
        foreach (var e in events)
        {
            if (e.ValueKind != JsonValueKind.Object || EddnState.Str(e, "event") != "FSSSignalDiscovered") continue;
            if (EddnState.Int64(e, "SystemAddress") != state.SystemAddress) continue;
            if (string.Equals(EddnState.Str(e, "USSType"), "$USS_Type_MissionTarget;", StringComparison.OrdinalIgnoreCase)) continue;

            var timestamp = EddnState.Str(e, "timestamp");
            var name = EddnState.Str(e, "SignalName");
            if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(name)) continue;

            var signal = new JsonObject { ["timestamp"] = timestamp, ["SignalName"] = name };
            foreach (var key in FssSignalStringKeys)
                if (EddnState.Str(e, key) is string s) signal[key] = s;
            if (EddnState.Bool(e, "IsStation") is bool isStation) signal["IsStation"] = isStation;
            if (EddnState.Int64(e, "ThreatLevel") is long threat) signal["ThreatLevel"] = threat;

            signals.Add(signal);
            firstTimestamp ??= timestamp;
        }
        if (signals.Count == 0) return null;

        var msg = new JsonObject
        {
            ["timestamp"] = firstTimestamp,
            ["event"] = "FSSSignalDiscovered",
            ["SystemAddress"] = state.SystemAddress,
            ["signals"] = signals,
            ["StarSystem"] = state.StarSystem,
            ["StarPos"] = new JsonArray(state.StarPos[0], state.StarPos[1], state.StarPos[2]),
        };
        AddGameFlags(msg, state);
        return Envelope(EddnSchemas.FssSignalDiscovered, msg, state);
    }

    private static readonly string[] FssSignalStringKeys =
        { "SignalType", "USSType", "SpawningState", "SpawningFaction", "SpawningPower", "OpposingPower" };

    // --- fcmaterials_journal / navroute: built from the sidecar file's content, validated. ---

    // The journal line for these events is only { timestamp, event }; the payload lives in the sidecar
    // file (NavRoute.json / FCMaterials.json), which the watcher publishes as a second event. A bare line
    // therefore has no Route / Items and is dropped here instead of becoming an invalid message.
    private EddnMessage? BuildNavRoute(JsonElement raw, EddnState state)
    {
        if (raw.ValueKind != JsonValueKind.Object || EddnState.Str(raw, "timestamp") is not string timestamp) return null;
        if (!raw.TryGetProperty("Route", out var route) || route.ValueKind != JsonValueKind.Array) return null;

        var jumps = new JsonArray();
        foreach (var hop in route.EnumerateArray())
        {
            if (hop.ValueKind != JsonValueKind.Object) continue;
            var system = EddnState.Str(hop, "StarSystem");
            var starClass = EddnState.Str(hop, "StarClass");
            if (string.IsNullOrEmpty(system) || string.IsNullOrEmpty(starClass)) continue;
            if (EddnState.Int64(hop, "SystemAddress") is not long address) continue;
            if (EddnState.ReadStarPos(hop) is not double[] pos) continue;

            jumps.Add(new JsonObject
            {
                ["StarSystem"] = system,
                ["SystemAddress"] = address,
                ["StarPos"] = new JsonArray(pos[0], pos[1], pos[2]),
                ["StarClass"] = starClass,
            });
        }
        if (jumps.Count == 0) return null;   // an empty route (cleared plot) carries nothing worth sharing

        var msg = new JsonObject { ["timestamp"] = timestamp, ["event"] = "NavRoute", ["Route"] = jumps };
        AddGameFlags(msg, state);
        return Envelope(EddnSchemas.NavRoute, msg, state);
    }

    private EddnMessage? BuildFcMaterials(JsonElement raw, EddnState state)
    {
        if (raw.ValueKind != JsonValueKind.Object || EddnState.Str(raw, "timestamp") is not string timestamp) return null;
        if (EddnState.Int64(raw, "MarketID") is not long marketId) return null;
        var carrierName = EddnState.Str(raw, "CarrierName");
        var carrierId = raw.TryGetProperty("CarrierID", out var idEl)
            ? idEl.ValueKind switch
            {
                JsonValueKind.String => idEl.GetString(),
                JsonValueKind.Number => idEl.GetRawText(),
                _ => null,
            }
            : null;
        if (string.IsNullOrEmpty(carrierName) || string.IsNullOrEmpty(carrierId)) return null;
        if (!raw.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) return null;

        var list = new JsonArray();
        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            var name = EddnState.Str(it, "Name");
            if (string.IsNullOrEmpty(name)) continue;
            if (EddnState.Int64(it, "id") is not long id || EddnState.Int64(it, "Price") is not long price
                || EddnState.Int64(it, "Stock") is not long stock || EddnState.Int64(it, "Demand") is not long demand) continue;

            list.Add(new JsonObject { ["id"] = id, ["Name"] = name, ["Price"] = price, ["Stock"] = stock, ["Demand"] = demand });
        }
        if (list.Count == 0) return null;

        var msg = new JsonObject
        {
            ["timestamp"] = timestamp,
            ["event"] = "FCMaterials",
            ["MarketID"] = marketId,
            ["CarrierName"] = carrierName,
            ["CarrierID"] = carrierId,
            ["Items"] = list,
        };
        AddGameFlags(msg, state);
        return Envelope(EddnSchemas.FcMaterialsJournal, msg, state);
    }

    // --- commodity / outfitting / shipyard: explicit reshaping of the sidecar files. ---

    private EddnMessage? BuildCommodity(JsonElement raw, EddnState state)
    {
        if (!TryStation(raw, state, out var msg)) return null;
        if (!raw.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) return null;

        var commodities = new JsonArray();
        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            var name = EddnState.Str(it, "Name");
            if (name is null) continue;

            // Not purchasable at a station market (limpets), or flagged illegal here: not market data.
            if (EddnState.Str(it, "Category") is string category && category.Contains("nonmarketable", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(EddnState.Str(it, "Legality"))) continue;

            commodities.Add(new JsonObject
            {
                ["name"] = CleanCommodityName(name),
                ["meanPrice"] = EddnState.Int64(it, "MeanPrice") ?? 0,
                ["buyPrice"] = EddnState.Int64(it, "BuyPrice") ?? 0,
                ["stock"] = EddnState.Int64(it, "Stock") ?? 0,
                ["stockBracket"] = Bracket(it, "StockBracket"),
                ["sellPrice"] = EddnState.Int64(it, "SellPrice") ?? 0,
                ["demand"] = EddnState.Int64(it, "Demand") ?? 0,
                ["demandBracket"] = Bracket(it, "DemandBracket"),
            });
        }
        if (commodities.Count == 0) return null;

        msg["commodities"] = commodities;
        AddGameFlags(msg, state);
        return Envelope(EddnSchemas.Commodity, msg, state);
    }

    private EddnMessage? BuildOutfitting(JsonElement raw, EddnState state)
    {
        if (!TryStation(raw, state, out var msg)) return null;
        if (!raw.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) return null;

        var modules = new JsonArray();
        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            var name = EddnState.Str(it, "Name")?.ToLowerInvariant();
            if (name is null || CosmeticFragments.Any(name.Contains)) continue;
            modules.Add(name);
        }
        if (modules.Count == 0) return null;

        msg["modules"] = modules;
        AddGameFlags(msg, state);
        return Envelope(EddnSchemas.Outfitting, msg, state);
    }

    private EddnMessage? BuildShipyard(JsonElement raw, EddnState state)
    {
        if (!TryStation(raw, state, out var msg)) return null;
        if (!raw.TryGetProperty("PriceList", out var list) || list.ValueKind != JsonValueKind.Array) return null;

        var ships = new JsonArray();
        foreach (var it in list.EnumerateArray())
            if (it.ValueKind == JsonValueKind.Object && EddnState.Str(it, "ShipType")?.ToLowerInvariant() is string s)
                ships.Add(s);
        if (ships.Count == 0) return null;

        msg["ships"] = ships;
        AddGameFlags(msg, state);
        return Envelope(EddnSchemas.Shipyard, msg, state);
    }

    /// <summary>Seeds a station-schema message with systemName/stationName/marketId/timestamp.</summary>
    private static bool TryStation(JsonElement raw, EddnState state, out JsonObject msg)
    {
        msg = new JsonObject();
        var system = EddnState.Str(raw, "StarSystem") ?? state.StarSystem;
        var station = EddnState.Str(raw, "StationName");
        var marketId = EddnState.Int64(raw, "MarketID");
        if (system is null || station is null || marketId is null) return false;

        msg["systemName"] = system;
        msg["stationName"] = station;
        msg["marketId"] = marketId;
        if (EddnState.Str(raw, "timestamp") is string ts) msg["timestamp"] = ts;
        return true;
    }

    // --- Shared helpers. ---

    private EddnMessage Envelope(string schemaRef, JsonObject message, EddnState state)
    {
        var header = new JsonObject
        {
            ["uploaderID"] = state.CommanderName ?? "Anonymous",
            ["softwareName"] = _options.SoftwareName,
            ["softwareVersion"] = _options.SoftwareVersion,
        };
        if (state.GameVersion is string gv) header["gameversion"] = gv;
        if (state.GameBuild is string gb) header["gamebuild"] = gb;

        return new EddnMessage
        {
            SchemaRef = schemaRef,
            Envelope = new JsonObject
            {
                ["$schemaRef"] = schemaRef,
                ["header"] = header,
                ["message"] = message,
            },
        };
    }

    private static void AddGameFlags(JsonObject msg, EddnState state)
    {
        if (state.Horizons is bool h) msg["horizons"] = h;
        if (state.Odyssey is bool o) msg["odyssey"] = o;
    }

    /// <summary>Recursively removes every key ending in <c>_Localised</c>.</summary>
    private static void StripLocalised(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).Where(k => k.EndsWith("_Localised", StringComparison.Ordinal)).ToList())
                    obj.Remove(key);
                foreach (var kv in obj) StripLocalised(kv.Value);
                break;
            case JsonArray arr:
                foreach (var item in arr) StripLocalised(item);
                break;
        }
    }

    private static JsonObject ToObject(JsonElement raw)
        => JsonNode.Parse(raw.GetRawText())!.AsObject();

    private static string CleanCommodityName(string n)
    {
        n = n.ToLowerInvariant();
        if (n.StartsWith('$')) n = n[1..];
        if (n.EndsWith("_name;", StringComparison.Ordinal)) n = n[..^6];
        return n.TrimEnd(';');
    }

    private static int Bracket(JsonElement item, string prop)
    {
        // Bracket fields are usually numbers but can arrive as "" for unavailable; treat as 0.
        if (item.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        return 0;
    }

    /// <summary>True if the node holds a usable value (not missing, null, or an empty string).</summary>
    private static bool HasValue(JsonNode? node)
        => node is not null && !(node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0);

    // The Try* readers distinguish "absent" (true, null) from "present but the wrong type" (false), so a
    // malformed field drops the message instead of throwing or being silently repaired.

    private static bool TryReadString(JsonObject msg, string key, out string? value)
    {
        value = null;
        if (msg[key] is not { } node) return true;
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            value = s.Length == 0 ? null : s;
            return true;
        }
        return false;
    }

    private static bool TryReadInt64(JsonObject msg, string key, out long? value)
    {
        value = null;
        if (msg[key] is not { } node) return true;
        if (node is JsonValue v && v.TryGetValue<long>(out var n))
        {
            value = n;
            return true;
        }
        return false;
    }

    private static bool TryReadPos(JsonObject msg, string key, out double[]? value)
    {
        value = null;
        if (msg[key] is not { } node) return true;
        if (node is not JsonArray arr || arr.Count != 3) return false;
        var pos = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (arr[i] is not JsonValue v || !v.TryGetValue<double>(out pos[i])) return false;
        }
        value = pos;
        return true;
    }
}
