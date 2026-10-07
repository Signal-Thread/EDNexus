using System.Text.Json;
using System.Text.Json.Nodes;

namespace EliteDangerous.Eddn;

/// <summary>
/// Turns raw Elite Dangerous journal and status JSON into ready-to-upload <see cref="EddnMessage"/>
/// envelopes, applying every EDDN protocol rule along the way: the journal-event whitelist,
/// <c>_Localised</c> stripping, removal of commander-private fields, and location augmentation with
/// a cross-check that drops anything it can't reconcile ("no data is better than bad data").
/// </summary>
public sealed class EddnJournalTransformer
{
    private readonly EddnClientOptions _options;

    public EddnJournalTransformer(EddnClientOptions options) => _options = options;

    // Top-level journal fields that are commander-private and must never reach EDDN. The last four are
    // also stripped inside Factions[] below; they stay here too, because the raw message keeps every
    // key the game wrote, so one at the root would otherwise be uploaded as-is.
    private static readonly HashSet<string> PrivateJournalKeys = new(StringComparer.Ordinal)
    {
        "ActiveFine", "CockpitBreach", "BoostUsed", "FuelLevel", "FuelUsed", "JumpDist",
        "Latitude", "Longitude", "Altitude", "Heading", "Wanted",
        "MyReputation", "SquadronFaction", "HappiestSystem", "HomeSystem",
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

    /// <summary>
    /// Builds an EDDN message for <paramref name="raw"/>, or returns <c>null</c> if the event is not
    /// uploadable (unknown event, missing required data that can't be augmented, etc.). Feed the same
    /// event through <see cref="EddnState.Observe"/> first so augmentation has current context.
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
            "FCMaterials" => BuildPassthrough(raw, state, EddnSchemas.FcMaterialsJournal),
            "NavRoute" => BuildPassthrough(raw, state, EddnSchemas.NavRoute),
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

        if (!Augment(msg, state)) return null;
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

    /// <summary>Ensures StarSystem/SystemAddress/StarPos are present and consistent; false = drop.</summary>
    private static bool Augment(JsonObject msg, EddnState state)
    {
        var mSys = (string?)msg["StarSystem"];
        var mAddr = (long?)msg["SystemAddress"];
        var mPos = ReadPos(msg["StarPos"]);

        // If the message carries its own identity, it must agree with our tracked location before we
        // borrow anything from state — otherwise we could stamp an event with the wrong system.
        var consistent =
            (mSys is null || state.StarSystem is null || string.Equals(mSys, state.StarSystem, StringComparison.Ordinal)) &&
            (mAddr is null || state.SystemAddress is null || mAddr == state.SystemAddress);

        var sys = mSys ?? (consistent ? state.StarSystem : null);
        var addr = mAddr ?? (consistent ? state.SystemAddress : null);
        var pos = mPos ?? (consistent ? state.StarPos : null);
        if (sys is null || addr is null || pos is null) return false;

        msg["StarSystem"] = sys;
        msg["SystemAddress"] = addr;
        msg["StarPos"] = new JsonArray(pos[0], pos[1], pos[2]);
        return true;
    }

    // --- fcmaterials_journal / navroute: passthrough of the raw event minus _Localised. ---

    private EddnMessage? BuildPassthrough(JsonElement raw, EddnState state, string schemaRef)
    {
        var msg = ToObject(raw);
        StripLocalised(msg);
        AddGameFlags(msg, state);
        return Envelope(schemaRef, msg, state);
    }

    // --- commodity / outfitting / shipyard: explicit reshaping of the sidecar files. ---

    private EddnMessage? BuildCommodity(JsonElement raw, EddnState state)
    {
        if (!TryStation(raw, state, out var msg)) return null;
        if (!raw.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) return null;

        var commodities = new JsonArray();
        foreach (var it in items.EnumerateArray())
        {
            var name = EddnState.Str(it, "Name");
            if (name is null) continue;
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
        return Envelope(EddnSchemas.Commodity, msg, state);
    }

    private EddnMessage? BuildOutfitting(JsonElement raw, EddnState state)
    {
        if (!TryStation(raw, state, out var msg)) return null;
        if (!raw.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) return null;

        var modules = new JsonArray();
        foreach (var it in items.EnumerateArray())
        {
            var name = EddnState.Str(it, "Name")?.ToLowerInvariant();
            if (name is null || CosmeticFragments.Any(name.Contains)) continue;
            modules.Add(name);
        }
        if (modules.Count == 0) return null;

        msg["modules"] = modules;
        return Envelope(EddnSchemas.Outfitting, msg, state);
    }

    private EddnMessage? BuildShipyard(JsonElement raw, EddnState state)
    {
        if (!TryStation(raw, state, out var msg)) return null;
        if (!raw.TryGetProperty("PriceList", out var list) || list.ValueKind != JsonValueKind.Array) return null;

        var ships = new JsonArray();
        foreach (var it in list.EnumerateArray())
            if (EddnState.Str(it, "ShipType")?.ToLowerInvariant() is string s)
                ships.Add(s);
        if (ships.Count == 0) return null;

        msg["ships"] = ships;
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

    private static double[]? ReadPos(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count != 3) return null;
        var pos = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (arr[i] is null) return null;
            pos[i] = arr[i]!.GetValue<double>();
        }
        return pos;
    }
}
