using EDNexus.Core.Journal;
using EDNexus.Core.State;

namespace EDNexus.Core.Colonisation;

/// <summary>
/// Feature service that keeps a running tally of what is stored in the commander's fleet carrier,
/// folded from the cargo movements <see cref="CarrierCargoMovements"/> reads off the journal:
/// <c>CargoTransfer</c> (<c>tocarrier</c> adds, <c>toship</c> removes) and trades at the carrier's own
/// market (<c>MarketSell</c> adds, <c>MarketBuy</c> removes). The journal has no snapshot of carrier
/// cargo, so this is a delta tally: it is exact over the journal history it has seen and clamped at
/// zero where that history is incomplete. It owns its own derived state and never touches
/// <c>CommanderState</c>.
/// </summary>
public sealed class CarrierCargoTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _stock = new();
    private readonly CommanderState? _state;

    /// <summary>Raised after a movement changes the tallied carrier stock.</summary>
    public event Action? Changed;

    /// <param name="bus">The journal bus to listen on.</param>
    /// <param name="state">
    /// Supplies the commander's own carrier and the dock, so a trade is only counted at the commander's
    /// own carrier and a transfer is not counted at someone else's. Without it every transfer counts and
    /// no trade does, which is all a bus alone can tell.
    /// </param>
    public CarrierCargoTracker(JournalEventBus bus, CommanderState? state = null)
    {
        _state = state;
        bus.Subscribe("CargoTransfer", OnMovement);
        bus.Subscribe("MarketSell", OnMovement);
        bus.Subscribe("MarketBuy", OnMovement);
    }

    /// <summary>Carrier stock as canonical commodity key (see <see cref="CommodityName"/>) → tons.</summary>
    public IReadOnlyDictionary<string, int> Stock
    {
        get { lock (_gate) return new Dictionary<string, int>(_stock); }
    }

    private void OnMovement(JournalEntry e)
    {
        if (CarrierCargoMovements.Read(e, _state?.MarketId) is not { } movement) return;
        if (!IsOwnCarrier(movement)) return;

        var changed = false;
        lock (_gate)
        {
            foreach (var delta in movement.Deltas)
            {
                var symbol = CommodityName.Canonicalize(delta.Commodity);
                if (symbol.Length == 0) continue;

                _stock[symbol] = Math.Max(0, (_stock.TryGetValue(symbol, out var v) ? v : 0) + delta.Units);
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// A trade fires at every station, so it only counts once its market is positively the commander's
    /// own carrier. A transfer only happens at a carrier, so it counts unless the commander is known to
    /// be docked at a <em>different</em> one (a squadron carrier, say).
    /// </summary>
    private bool IsOwnCarrier(CarrierCargoMovement movement)
    {
        var own = _state?.CarrierId;
        if (movement.IsMarketTrade) return own is not null && movement.MarketId == own;
        return own is null || movement.MarketId is null || movement.MarketId == own;
    }
}
