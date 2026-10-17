using System.Collections.Concurrent;

namespace EDNexus.Core.State;

/// <summary>
/// The single live picture of the commander that every feature module reads from.
/// Scalar properties raise <see cref="ObservableObject.PropertyChanged"/>; the collections
/// raise their own change events since dictionaries don't notify on their own.
/// </summary>
public sealed class CommanderState : ObservableObject
{
    private string? _name;
    public string? Name { get => _name; set => Set(ref _name, value); }

    private long _balance;
    public long Balance { get => _balance; set => Set(ref _balance, value); }

    private string? _ship;
    public string? Ship { get => _ship; set => Set(ref _ship, value); }

    private string? _shipName;
    public string? ShipName { get => _shipName; set => Set(ref _shipName, value); }

    private string? _shipIdent;
    public string? ShipIdent { get => _shipIdent; set => Set(ref _shipIdent, value); }

    private string? _starSystem;
    public string? StarSystem { get => _starSystem; set => Set(ref _starSystem, value); }

    private string? _body;
    public string? Body { get => _body; set => Set(ref _body, value); }

    private bool _docked;
    public bool Docked { get => _docked; set => Set(ref _docked, value); }

    private FlightMode _flight;
    /// <summary>
    /// What the commander is doing right now (docked, landed, in supercruise, …), from the game's
    /// <c>Status.json</c>. <see cref="FlightMode.Unknown"/> until the first one is read.
    /// </summary>
    public FlightMode Flight { get => _flight; set => Set(ref _flight, value); }

    private string? _jumpDestination;
    /// <summary>
    /// The system a hyperspace jump in progress is heading to, from <c>StartJump</c>. Null when not
    /// jumping; cleared on arrival.
    /// </summary>
    public string? JumpDestination { get => _jumpDestination; set => Set(ref _jumpDestination, value); }

    private string? _stationName;
    /// <summary>
    /// The docked station exactly as the journal names it. For a fleet carrier that is the callsign,
    /// not the carrier's name — read <see cref="StationDisplayName"/> when showing it to a commander.
    /// </summary>
    public string? StationName
    {
        get => _stationName;
        set { if (Set(ref _stationName, value)) Raise(nameof(StationDisplayName)); }
    }

    private string? _stationType;
    /// <summary>The docked station's kind, from <c>Docked</c>/<c>Location</c> (e.g. "Coriolis", "FleetCarrier"). Null when not docked.</summary>
    public string? StationType { get => _stationType; set => Set(ref _stationType, value); }

    private string? _carrierName;
    /// <summary>The name the commander gave their own fleet carrier, from <c>CarrierStats</c>. Null until one is seen.</summary>
    public string? CarrierName
    {
        get => _carrierName;
        set { if (Set(ref _carrierName, value)) Raise(nameof(StationDisplayName)); }
    }

    private string? _carrierCallsign;
    /// <summary>The commander's own fleet carrier callsign (e.g. "K7Q-B3L"), from <c>CarrierStats</c>. Null until one is seen.</summary>
    public string? CarrierCallsign
    {
        get => _carrierCallsign;
        set { if (Set(ref _carrierCallsign, value)) Raise(nameof(StationDisplayName)); }
    }

    /// <summary>
    /// The docked station as a commander would name it: their own carrier's name in place of its
    /// callsign, and <see cref="StationName"/> unchanged for everywhere else.
    /// </summary>
    public string? StationDisplayName => StationDisplay.Resolve(StationName, CarrierName, CarrierCallsign);

    private double _fuelMain;
    public double FuelMain { get => _fuelMain; set => Set(ref _fuelMain, value); }

    private double _fuelCapacity;
    public double FuelCapacity { get => _fuelCapacity; set => Set(ref _fuelCapacity, value); }

    private double _cargoTons;
    public double CargoTons { get => _cargoTons; set => Set(ref _cargoTons, value); }

    private double _carrierFuel;
    /// <summary>Tritium in the current fleet carrier's fuel reserve (t), from <c>CarrierStats</c>. 0 when unknown.</summary>
    public double CarrierFuel { get => _carrierFuel; set => Set(ref _carrierFuel, value); }

    private double _carrierJumpRange;
    /// <summary>The fleet carrier's current jump range (ly) at its present load, from <c>CarrierStats</c>. 0 when unknown.</summary>
    public double CarrierJumpRange { get => _carrierJumpRange; set => Set(ref _carrierJumpRange, value); }

    private double _carrierUsedCapacity;
    /// <summary>
    /// Tonnage of the carrier's capacity already used by crew, services and cargo (<c>SpaceUsage.TotalCapacity
    /// - SpaceUsage.FreeSpace</c>), from <c>CarrierStats</c>. 0 when unknown. Drives the fleet-carrier route
    /// plot's fuel model — less free space means less room to carry tritium reserves for restocking.
    /// </summary>
    public double CarrierUsedCapacity { get => _carrierUsedCapacity; set => Set(ref _carrierUsedCapacity, value); }

    private string? _carrierPendingSystem;
    /// <summary>
    /// The system a fleet-carrier jump is scheduled to, from a <c>CarrierJumpRequest</c> that hasn't yet
    /// completed or been cancelled. Non-null means the carrier is booked to move but the journal has not
    /// logged the arrival — which is why the live location can lag behind where the commander expects to be.
    /// </summary>
    public string? CarrierPendingSystem { get => _carrierPendingSystem; set => Set(ref _carrierPendingSystem, value); }

    private DateTimeOffset? _carrierPendingDeparture;
    /// <summary>When the pending carrier jump is scheduled to depart, from <c>CarrierJumpRequest</c>. Null when none is pending.</summary>
    public DateTimeOffset? CarrierPendingDeparture { get => _carrierPendingDeparture; set => Set(ref _carrierPendingDeparture, value); }

    private Ship.ShipFsdProfile? _fsd;
    /// <summary>The current ship's Frame Shift Drive, derived from the last <c>Loadout</c>. Null until one is seen. Drives no-boost route plots.</summary>
    public Ship.ShipFsdProfile? Fsd { get => _fsd; set => Set(ref _fsd, value); }

    private string? _lastLoadoutJson;
    /// <summary>
    /// The raw text of the last <c>Loadout</c> event, kept verbatim (not just the fields we parse) so it
    /// can be handed to <see cref="Ship.CoriolisShareLink"/> for a "share my build" link. Null until one
    /// is seen this session.
    /// </summary>
    public string? LastLoadoutJson { get => _lastLoadoutJson; set => Set(ref _lastLoadoutJson, value); }

    private DateTimeOffset _lastUpdated;
    public DateTimeOffset LastUpdated { get => _lastUpdated; set => Set(ref _lastUpdated, value); }

    /// <summary>Current cargo hold: commodity name → tons.</summary>
    public ConcurrentDictionary<string, int> Cargo { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MaterialsInventory Materials { get; } = new();

    /// <summary>On-foot (Odyssey) inventory: components, data, goods and consumables in the ship locker/backpack.</summary>
    public OnFootInventory OnFoot { get; } = new();

    private string? _suitName;
    /// <summary>Localised name of the currently equipped suit (e.g. "Dominator Suit"), or null if unknown.</summary>
    public string? SuitName { get => _suitName; set => Set(ref _suitName, value); }

    private string? _suitSymbol;
    /// <summary>Raw journal suit symbol (e.g. "tacticalsuit_class3"), used to resolve the suit + grade.</summary>
    public string? SuitSymbol { get => _suitSymbol; set => Set(ref _suitSymbol, value); }

    private int _suitClass;
    /// <summary>Current suit grade (1-5), parsed from the suit symbol's "_classN" suffix.</summary>
    public int SuitClass { get => _suitClass; set => Set(ref _suitClass, value); }

    public event Action? CargoChanged;
    public event Action? MaterialsChanged;
    public event Action? OnFootChanged;

    public void RaiseCargoChanged() => CargoChanged?.Invoke();
    public void RaiseMaterialsChanged() => MaterialsChanged?.Invoke();
    public void RaiseOnFootChanged() => OnFootChanged?.Invoke();
}

/// <summary>Engineering materials split into the game's three categories.</summary>
public sealed class MaterialsInventory
{
    public ConcurrentDictionary<string, int> Raw { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, int> Manufactured { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, int> Encoded { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int TotalCount => Raw.Values.Sum() + Manufactured.Values.Sum() + Encoded.Values.Sum();
}

/// <summary>On-foot goods split into the game's four ship-locker categories.</summary>
public sealed class OnFootInventory
{
    public ConcurrentDictionary<string, int> Components { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, int> Data { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, int> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, int> Consumables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int TotalCount => Components.Values.Sum() + Data.Values.Sum() + Items.Values.Sum() + Consumables.Values.Sum();
}
