using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using EDNexus.Core.Journal;

namespace EDNexus.Core.State;

/// <summary>
/// Wires journal events to <see cref="CommanderState"/>. This is the one place that knows
/// how individual events mutate the live picture; feature modules read state instead of
/// re-parsing events themselves.
/// </summary>
public sealed class StateTracker
{
    private readonly CommanderState _state;

    public StateTracker(JournalEventBus bus, CommanderState state)
    {
        _state = state;

        bus.Subscribe("Commander", OnCommander);
        bus.Subscribe("LoadGame", OnLoadGame);
        bus.Subscribe("Loadout", OnLoadout);
        bus.Subscribe("Location", OnLocation);
        bus.Subscribe("FSDJump", OnJump);
        bus.Subscribe("CarrierJump", OnJump);
        bus.Subscribe("Docked", OnDocked);
        bus.Subscribe("Undocked", OnUndocked);
        bus.Subscribe("SupercruiseExit", OnSupercruiseExit);
        bus.Subscribe("Status", OnStatus);
        bus.Subscribe("Cargo", OnCargo);
        bus.Subscribe("Materials", OnMaterials);
        bus.Subscribe("MaterialCollected", OnMaterialCollected);
        bus.Subscribe("ShipLocker", OnShipLocker);
        bus.Subscribe("BackpackChange", OnBackpackChange);
        bus.Subscribe("SuitLoadout", OnSuitLoadout);
        bus.Subscribe("SwitchSuitLoadout", OnSuitLoadout);
        bus.Subscribe("CarrierStats", OnCarrierStats);
        bus.Subscribe("CarrierJumpRequest", OnCarrierJumpRequest);
        bus.Subscribe("CarrierJumpCancelled", OnCarrierJumpCancelled);

        bus.SubscribeAny(e =>
        {
            if (e.Timestamp != default) _state.LastUpdated = e.Timestamp;
        });
    }

    private void OnCommander(JournalEntry e) => SetCommanderName(e.GetString("Name"));

    /// <summary>
    /// Adopts the commander name from <c>Commander</c>/<c>LoadGame</c>. A different commander than the one
    /// already loaded (the same session, or a replayed journal, can switch accounts) must not inherit the
    /// previous one's carrier or suit, so those are cleared first. Logging back in as the same commander
    /// keeps them — a scheduled carrier jump, say, outlives a relog.
    /// </summary>
    private void SetCommanderName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (_state.Name is { Length: > 0 } previous
            && !string.Equals(previous, name, StringComparison.OrdinalIgnoreCase))
            ResetPerCommanderState();
        _state.Name = name;
    }

    private void ResetPerCommanderState()
    {
        _state.CarrierName = null;
        _state.CarrierCallsign = null;
        _state.CarrierFuel = 0;
        _state.CarrierJumpRange = 0;
        _state.CarrierUsedCapacity = 0;
        _state.CarrierPendingSystem = null;
        _state.CarrierPendingDeparture = null;
        _state.SuitName = null;
        _state.SuitSymbol = null;
        _state.SuitClass = 0;
    }

    private void OnLoadGame(JournalEntry e)
    {
        SetCommanderName(e.GetString("Commander"));
        _state.Ship = e.GetLocalised("Ship") ?? _state.Ship;
        _state.ShipName = e.GetString("ShipName") ?? _state.ShipName;
        _state.ShipIdent = e.GetString("ShipIdent") ?? _state.ShipIdent;
        if (e.GetInt64("Credits") is long credits) _state.Balance = credits;
    }

    private void OnLoadout(JournalEntry e)
    {
        _state.Ship = e.GetLocalised("Ship") ?? _state.Ship;
        _state.ShipName = e.GetString("ShipName") ?? _state.ShipName;
        _state.ShipIdent = e.GetString("ShipIdent") ?? _state.ShipIdent;
        if (e.Raw.TryGetProperty("FuelCapacity", out var fc)
            && fc.TryGetProperty("Main", out var main) && main.TryGetDouble(out var cap))
            _state.FuelCapacity = cap;

        // Derive the FSD profile so the no-boost route plot can model this ship's jumps.
        if (Ship.ShipFsdProfile.FromLoadout(e) is { } fsd) _state.Fsd = fsd;

        // Keep the raw event verbatim for the Coriolis/EDSY share link, which needs every module's
        // engineering detail, not just the fields we extract above.
        _state.LastLoadoutJson = e.Raw.GetRawText();
    }

    /// <summary>
    /// CarrierStats reports the carrier's tritium reserve and current jump range — updated on login and
    /// after carrier actions, so it tracks fuel as it is burned by jumps and topped up by deposits.
    /// </summary>
    private void OnCarrierStats(JournalEntry e)
    {
        if (e.GetDouble("FuelLevel") is double fuel) _state.CarrierFuel = fuel;
        if (e.GetDouble("JumpRangeCurr") is double range) _state.CarrierJumpRange = range;

        // Used capacity = total minus free, so it tracks crew/services/cargo together the way Spansh's
        // fleet-carrier plotter expects it (as "capacity_used"), not just the cargo hold.
        if (e.Raw.TryGetProperty("SpaceUsage", out var usage)
            && usage.TryGetProperty("TotalCapacity", out var totalEl) && totalEl.TryGetDouble(out var total)
            && usage.TryGetProperty("FreeSpace", out var freeEl) && freeEl.TryGetDouble(out var free))
            _state.CarrierUsedCapacity = Math.Max(0, total - free);

        // The only place the carrier's chosen name is ever reported — Docked/Location carry the
        // callsign alone. Pairing the two here is what lets the UI show "Nomad's Reach" instead
        // of "K7Q-B3L", and only for the commander's own carrier.
        if (e.GetString("Name") is { Length: > 0 } name) _state.CarrierName = name;
        if (e.GetString("Callsign") is { Length: > 0 } callsign) _state.CarrierCallsign = callsign;
    }

    /// <summary>
    /// A carrier jump has been scheduled: record the destination and departure time so the UI can show
    /// where the carrier is heading before the (much later) arrival lands in the journal. If the game is
    /// closed before departure, no arrival is ever logged — the pending destination is the only clue.
    /// </summary>
    private void OnCarrierJumpRequest(JournalEntry e)
    {
        _state.CarrierPendingSystem = e.GetString("SystemName");
        _state.CarrierPendingDeparture =
            e.Raw.TryGetProperty("DepartureTime", out var d) && d.ValueKind == JsonValueKind.String
            && d.TryGetDateTimeOffset(out var t) ? t : null;
    }

    private void OnCarrierJumpCancelled(JournalEntry e)
    {
        _state.CarrierPendingSystem = null;
        _state.CarrierPendingDeparture = null;
    }

    private void OnLocation(JournalEntry e)
    {
        _state.StarSystem = e.GetString("StarSystem") ?? _state.StarSystem;
        _state.Body = e.GetString("Body") ?? _state.Body;
        _state.Docked = e.GetBool("Docked") ?? _state.Docked;
        _state.StationName = e.GetString("StationName");
        _state.StationType = e.GetString("StationType");
    }

    private void OnJump(JournalEntry e)
    {
        _state.StarSystem = e.GetString("StarSystem") ?? _state.StarSystem;
        _state.Body = e.GetString("Body") ?? e.GetString("StarSystem");
        _state.Docked = false;
        _state.StationName = null;
        _state.StationType = null;

        // A completed carrier jump fulfils any pending request — clear it so the UI stops advertising it.
        if (e.Event == "CarrierJump")
        {
            _state.CarrierPendingSystem = null;
            _state.CarrierPendingDeparture = null;
        }
    }

    private void OnDocked(JournalEntry e)
    {
        _state.Docked = true;
        _state.StationName = e.GetString("StationName");
        _state.StationType = e.GetString("StationType");
    }

    private void OnUndocked(JournalEntry e)
    {
        _state.Docked = false;
        _state.StationName = null;
        _state.StationType = null;
    }

    private void OnSupercruiseExit(JournalEntry e) => _state.Body = e.GetString("Body") ?? _state.Body;

    private void OnStatus(JournalEntry e)
    {
        if (e.Raw.TryGetProperty("Fuel", out var fuel)
            && fuel.TryGetProperty("FuelMain", out var fm) && fm.TryGetDouble(out var main))
            _state.FuelMain = main;
        if (e.GetDouble("Cargo") is double cargo) _state.CargoTons = cargo;
        if (e.GetInt64("Balance") is long balance) _state.Balance = balance;
    }

    private void OnCargo(JournalEntry e)
    {
        // The Cargo event sometimes omits Inventory and defers to Cargo.json; only rebuild
        // when the array is actually present so we don't wipe the hold to empty.
        if (!e.Raw.TryGetProperty("Inventory", out var inv) || inv.ValueKind != JsonValueKind.Array)
            return;

        _state.Cargo.Clear();
        foreach (var item in inv.EnumerateArray())
        {
            var name = ReadLocalisedName(item);
            var count = item.TryGetProperty("Count", out var c) && c.TryGetInt32(out var n) ? n : 0;
            if (name is not null) _state.Cargo[name] = count;
        }
        _state.RaiseCargoChanged();
    }

    private void OnMaterials(JournalEntry e)
    {
        LoadCategory(e, "Raw", _state.Materials.Raw);
        LoadCategory(e, "Manufactured", _state.Materials.Manufactured);
        LoadCategory(e, "Encoded", _state.Materials.Encoded);
        _state.RaiseMaterialsChanged();
    }

    private void OnMaterialCollected(JournalEntry e)
    {
        var category = e.GetString("Category");
        var name = e.GetString("Name");
        if (name is null || category is null) return;

        var count = (int)(e.GetInt64("Count") ?? 1);
        TargetFor(category).AddOrUpdate(name, count, (_, v) => v + count);
        _state.RaiseMaterialsChanged();
    }

    private ConcurrentDictionary<string, int> TargetFor(string category) => category.ToLowerInvariant() switch
    {
        "raw" => _state.Materials.Raw,
        "manufactured" => _state.Materials.Manufactured,
        _ => _state.Materials.Encoded,
    };

    private static void LoadCategory(JournalEntry e, string prop, ConcurrentDictionary<string, int> target)
    {
        if (!e.Raw.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        target.Clear();
        foreach (var item in arr.EnumerateArray())
        {
            var name = item.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var count = item.TryGetProperty("Count", out var c) && c.TryGetInt32(out var v) ? v : 0;
            if (name is not null) target[name] = count;
        }
    }

    private static string? ReadLocalisedName(JsonElement item)
    {
        if (item.TryGetProperty("Name_Localised", out var loc) && loc.ValueKind == JsonValueKind.String)
            return loc.GetString();
        return item.TryGetProperty("Name", out var n) ? n.GetString() : null;
    }

    // --- Odyssey: on-foot inventory + current suit. ---

    private void OnShipLocker(JournalEntry e)
    {
        // Like Cargo, ShipLocker sometimes omits the arrays and defers to ShipLocker.json;
        // only rebuild a category when its array is actually present.
        LoadOnFootCategory(e, "Items", _state.OnFoot.Items);
        LoadOnFootCategory(e, "Components", _state.OnFoot.Components);
        LoadOnFootCategory(e, "Consumables", _state.OnFoot.Consumables);
        LoadOnFootCategory(e, "Data", _state.OnFoot.Data);
        _state.RaiseOnFootChanged();
    }

    private void OnBackpackChange(JournalEntry e)
    {
        var changed = false;
        if (e.Raw.TryGetProperty("Added", out var added) && added.ValueKind == JsonValueKind.Array)
            changed |= ApplyBackpackDeltas(added, +1);
        if (e.Raw.TryGetProperty("Removed", out var removed) && removed.ValueKind == JsonValueKind.Array)
            changed |= ApplyBackpackDeltas(removed, -1);
        if (changed) _state.RaiseOnFootChanged();
    }

    private bool ApplyBackpackDeltas(JsonElement arr, int sign)
    {
        var changed = false;
        foreach (var item in arr.EnumerateArray())
        {
            // Raw (not localised) symbol, matching the Odyssey catalog's material keys — same convention
            // as ship Materials (LoadCategory), which is deliberately not localised like Cargo/BuildShoppingList.
            var name = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : null;
            var type = item.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var count = item.TryGetProperty("Count", out var c) && c.TryGetInt32(out var n) ? n : 0;
            if (name is null || type is null || count == 0) continue;

            var target = OnFootTargetFor(type);
            if (target is null) continue;

            target.AddOrUpdate(name, Math.Max(0, sign * count), (_, v) => Math.Max(0, v + sign * count));
            changed = true;
        }
        return changed;
    }

    private ConcurrentDictionary<string, int>? OnFootTargetFor(string type) => type.ToLowerInvariant() switch
    {
        "item" => _state.OnFoot.Items,
        "component" => _state.OnFoot.Components,
        "consumable" => _state.OnFoot.Consumables,
        "data" => _state.OnFoot.Data,
        _ => null,
    };

    private static void LoadOnFootCategory(JournalEntry e, string prop, ConcurrentDictionary<string, int> target)
    {
        if (!e.Raw.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        target.Clear();
        foreach (var item in arr.EnumerateArray())
        {
            // Raw symbol (see ApplyBackpackDeltas) so held counts join the Odyssey catalog by the same key.
            var name = item.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var count = item.TryGetProperty("Count", out var c) && c.TryGetInt32(out var v) ? v : 0;
            if (name is not null) target[name] = count;
        }
    }

    private static readonly Regex SuitGradeSuffix = new(@"_class(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void OnSuitLoadout(JournalEntry e)
    {
        var symbol = e.GetString("SuitName");
        if (symbol is null) return;

        _state.SuitSymbol = symbol;
        _state.SuitName = e.GetLocalised("SuitName") ?? symbol;
        _state.SuitClass = SuitGradeSuffix.Match(symbol) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var grade)
            ? grade : 1;
    }
}
