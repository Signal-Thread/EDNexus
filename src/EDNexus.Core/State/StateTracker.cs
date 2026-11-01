using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using EDNexus.Core.Engineering;
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
        bus.Subscribe("StartJump", OnStartJump);
        bus.Subscribe("Cargo", OnCargo);
        bus.Subscribe("Materials", OnMaterials);
        bus.Subscribe("MaterialCollected", OnMaterialCollected);
        // Everything that SPENDS or swaps materials. The game writes a full Materials snapshot only at
        // launch and relog, so without these the inventory only ever goes up between snapshots.
        bus.Subscribe("MaterialTrade", OnMaterialTrade);
        bus.Subscribe("MaterialDiscarded", OnNamedMaterialSpent);
        bus.Subscribe("EngineerCraft", OnEngineerCraft);
        bus.Subscribe("EngineerLegacyConvert", OnEngineerLegacyConvert);
        bus.Subscribe("Synthesis", OnSynthesis);
        bus.Subscribe("TechnologyBroker", OnTechnologyBroker);
        bus.Subscribe("EngineerContribution", OnEngineerContribution);
        bus.Subscribe("ScientificResearch", OnNamedMaterialSpent);
        bus.Subscribe("MissionCompleted", OnMissionCompleted);   // the one thing that GIVES materials besides a pickup or a trade
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
        // Where the previous commander was heading must never be published under the new one's name.
        _state.Flight = FlightMode.Unknown;
        _state.JumpDestination = null;
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
        if (Ship.ShipPowerProfile.FromLoadout(e) is { } power) _state.Power = power;

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
        _state.JumpDestination = null;
        _state.StarSystem = e.GetString("StarSystem") ?? _state.StarSystem;
        _state.Body = e.GetString("Body") ?? _state.Body;
        _state.Docked = e.GetBool("Docked") ?? _state.Docked;
        _state.StationName = e.GetString("StationName");
        _state.StationType = e.GetString("StationType");
    }

    private void OnJump(JournalEntry e)
    {
        _state.JumpDestination = null;
        _state.StarSystem = e.GetString("StarSystem") ?? _state.StarSystem;
        _state.Body = e.GetString("Body") ?? e.GetString("StarSystem");

        // A ship jump leaves any station behind. A carrier jump carries the station with it: the
        // commander is still sitting on the pad, and the event says so (and names the carrier).
        var stillDocked = e.Event == "CarrierJump" && e.GetBool("Docked") == true;
        _state.Docked = stillDocked;
        _state.StationName = stillDocked ? e.GetString("StationName") : null;
        _state.StationType = stillDocked ? e.GetString("StationType") : null;

        // Any ship jump uses up a pending FSD injection, whether or not the jump needed it.
        if (e.Event == "FSDJump") _state.FsdInjectionMultiplier = 1;

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

    /// <summary>A jump has begun. Only a hyperspace jump has a destination; a supercruise one does not.</summary>
    private void OnStartJump(JournalEntry e)
        => _state.JumpDestination = e.GetString("JumpType") == "Hyperspace" ? e.GetString("StarSystem") : null;

    private void OnSupercruiseExit(JournalEntry e) => _state.Body = e.GetString("Body") ?? _state.Body;

    private void OnStatus(JournalEntry e)
    {
        // The flags are the one source of the flight mode. Status.json is a snapshot, so it cannot
        // disagree with itself the way a stream of journal events can when one is read a poll late.
        if (e.GetInt64("Flags") is long flags
            && ShipStatusFlags.Derive(flags, e.GetInt64("Flags2") ?? 0) is FlightMode mode)
            _state.Flight = mode;
        if (e.GetInt64("Flags") is long hpFlags) _state.HardpointsDeployed = ShipStatusFlags.HardpointsDeployed(hpFlags);

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

        if (Int(e.Raw, "Count", fallback: 1) is int count && Gain(TargetFor(category), name, count))
            _state.RaiseMaterialsChanged();
    }

    /// <summary>
    /// A material trader swap: what was paid leaves, what was received arrives. Without this a commander
    /// who trades sees the old stock and none of the new, until the next game launch.
    /// </summary>
    private void OnMaterialTrade(JournalEntry e)
    {
        var changed = false;
        if (e.Raw.TryGetProperty("Paid", out var paid) && paid.ValueKind == JsonValueKind.Object)
            changed |= Int(paid, "Quantity") is int quantity && Spend(Str(paid, "Material"), quantity, Str(paid, "Category"));
        if (e.Raw.TryGetProperty("Received", out var received) && received.ValueKind == JsonValueKind.Object
            && Str(received, "Material") is { } material && Str(received, "Category") is { } category)
            changed |= Int(received, "Quantity") is int quantity && Gain(TargetFor(category), material, quantity);
        if (changed) _state.RaiseMaterialsChanged();
    }

    /// <summary>
    /// Discarding a material and Guardian/Thargoid research each name one <c>{ Name, Category, Count }</c>
    /// to take out of the inventory.
    /// </summary>
    private void OnNamedMaterialSpent(JournalEntry e)
    {
        if (Int(e.Raw, "Count", fallback: 1) is int count && Spend(e.GetString("Name"), count, e.GetString("Category")))
            _state.RaiseMaterialsChanged();
    }

    /// <summary>An engineer modification consumes its ingredients (<c>Ingredients</c>: Name/Count, no category).</summary>
    private void OnEngineerCraft(JournalEntry e) => SpendAll(e, "Ingredients");

    /// <summary>
    /// Converting a legacy engineered module costs ingredients like a roll does, unless the event is only a
    /// preview of that cost.
    /// </summary>
    private void OnEngineerLegacyConvert(JournalEntry e)
    {
        if (e.GetBool("IsPreview") == true) return;
        SpendAll(e, "Ingredients");
    }

    /// <summary>Synthesis (ammo, repair, FSD boost…) consumes the listed materials.</summary>
    private void OnSynthesis(JournalEntry e)
    {
        SpendAll(e, "Materials");
        // An FSD injection boosts the next jump's range; route plots need to know.
        if (Ship.FsdInjection.MultiplierFor(e.GetString("Name")) is { } boost) _state.FsdInjectionMultiplier = boost;
    }

    /// <summary>
    /// Unlocking a technology broker module consumes materials (and commodities, which are cargo and not
    /// handled here).
    /// </summary>
    private void OnTechnologyBroker(JournalEntry e) => SpendAll(e, "Materials");

    /// <summary>
    /// Handing materials to an engineer to unlock them. Read by what the event names rather than by its
    /// <c>Type</c>: a commodity, credits, bond or bounty contribution carries no <c>Material</c> (commodities
    /// are cargo, not this), so only a material contribution has anything to take out.
    /// </summary>
    private void OnEngineerContribution(JournalEntry e)
    {
        if (Int(e.Raw, "Quantity", fallback: 1) is int quantity && Spend(e.GetString("Material"), quantity, e.GetString("Category")))
            _state.RaiseMaterialsChanged();
    }

    /// <summary>
    /// A mission can pay out ship materials (<c>MaterialsReward</c>: Name/Category/Count). Only the three ship
    /// categories count: an Odyssey reward arrives as <c>$MICRORESOURCE_CATEGORY_*</c>, which is ship-locker
    /// stock the <c>ShipLocker</c> snapshot keeps right, and must not land in an engineering bucket.
    /// </summary>
    private void OnMissionCompleted(JournalEntry e)
    {
        if (!e.Raw.TryGetProperty("MaterialsReward", out var rewards) || rewards.ValueKind != JsonValueKind.Array) return;

        var changed = false;
        foreach (var reward in rewards.EnumerateArray())
        {
            if (reward.ValueKind != JsonValueKind.Object) continue;
            if (Str(reward, "Name") is not { } name || Str(reward, "Category") is not { } category) continue;
            if (!IsShipMaterialCategory(category)) continue;
            changed |= Int(reward, "Count", fallback: 1) is int count && Gain(TargetFor(category), name, count);
        }

        if (changed) _state.RaiseMaterialsChanged();
    }

    private static bool IsShipMaterialCategory(string category)
        => category.Equals("Raw", StringComparison.OrdinalIgnoreCase)
            || category.Equals("Manufactured", StringComparison.OrdinalIgnoreCase)
            || category.Equals("Encoded", StringComparison.OrdinalIgnoreCase);

    /// <summary>Spends every <c>{ Name, Count[, Category] }</c> in the named array and raises one change.</summary>
    private void SpendAll(JournalEntry e, string arrayProperty)
    {
        if (!e.Raw.TryGetProperty(arrayProperty, out var items) || items.ValueKind != JsonValueKind.Array) return;

        var changed = false;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            changed |= Int(item, "Count", fallback: 1) is int count && Spend(Str(item, "Name"), count, Str(item, "Category"));
        }

        if (changed) _state.RaiseMaterialsChanged();
    }

    /// <summary>
    /// Adds <paramref name="count"/> to a material, up to its cap. The game logs the pickup but only
    /// holds up to the cap (100 for a grade 5, 300 for a grade 1…), so counting it all would leave the
    /// inventory above what the commander really has, and every later subtraction would be off by the same
    /// amount. A material the catalogue does not know has no known cap and is not limited. Never lowers a
    /// count that is already above the cap, since that would be the catalogue disagreeing with the game.
    /// Returns whether the stored count changed (a pickup at the cap changes nothing).
    /// </summary>
    private static bool Gain(ConcurrentDictionary<string, int> bucket, string name, int count)
    {
        if (count <= 0) return false;
        var cap = EngineeringCatalog.Default.Material(name)?.Cap ?? int.MaxValue;
        var had = bucket.TryGetValue(name, out var held);
        var next = !had ? Math.Min(count, cap)
            : held >= cap ? held
            : (int)Math.Min((long)held + count, cap);
        bucket[name] = next;
        return !had || next != held;
    }

    /// <summary>
    /// Removes <paramref name="count"/> of a material, never below zero. Ingredients carry no category,
    /// so unless one is given the material is looked for in all three buckets. A material that is not held
    /// is ignored: spending what the tracker never saw (a log that starts mid-session) must not invent stock.
    /// Returns whether the stored count changed (spending from zero changes nothing).
    /// </summary>
    private bool Spend(string? name, int count, string? category = null)
    {
        if (name is null || count <= 0) return false;

        var buckets = category is null
            ? new[] { _state.Materials.Raw, _state.Materials.Manufactured, _state.Materials.Encoded }
            : new[] { TargetFor(category), _state.Materials.Raw, _state.Materials.Manufactured, _state.Materials.Encoded };

        foreach (var bucket in buckets)
        {
            if (!bucket.TryGetValue(name, out var held)) continue;
            var next = Math.Max(0, held - count);
            bucket[name] = next;
            return next != held;
        }

        return false;
    }

    private static string? Str(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// A whole-number property: <paramref name="fallback"/> when it is absent, null when it is present but
    /// not a usable integer — an invalid quantity is ignored, not read as "one".
    /// </summary>
    private static int? Int(JsonElement obj, string prop, int fallback = 0)
        => !obj.TryGetProperty(prop, out var v) ? fallback
            : v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

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
