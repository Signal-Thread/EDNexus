using System.Text.Json;
using EDNexus.Core.Journal;

namespace EDNexus.Core.Colonisation;

/// <summary>
/// Feature service that keeps a running tally of what is stored in the commander's fleet carrier,
/// folded from <c>CargoTransfer</c> events (<c>tocarrier</c> adds, <c>toship</c> removes). The journal
/// has no snapshot of carrier cargo, so this is a delta tally: it is exact over the journal history it
/// has seen and clamped at zero where that history is incomplete. It owns its own derived state and
/// never touches <c>CommanderState</c>.
/// </summary>
public sealed class CarrierCargoTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _stock = new();

    /// <summary>Raised after a transfer changes the tallied carrier stock.</summary>
    public event Action? Changed;

    public CarrierCargoTracker(JournalEventBus bus)
    {
        bus.Subscribe("CargoTransfer", OnTransfer);
    }

    /// <summary>Carrier stock as canonical commodity key (see <see cref="CommodityName"/>) → tons.</summary>
    public IReadOnlyDictionary<string, int> Stock
    {
        get { lock (_gate) return new Dictionary<string, int>(_stock); }
    }

    private void OnTransfer(JournalEntry e)
    {
        if (!e.Raw.TryGetProperty("Transfers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;

        var changed = false;
        lock (_gate)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var symbol = CommodityName.Canonicalize(ReadString(item, "Type"));
                if (symbol.Length == 0) continue;
                if (!item.TryGetProperty("Count", out var c) || c.ValueKind != JsonValueKind.Number
                    || !c.TryGetInt32(out var count) || count <= 0) continue;

                var delta = ReadString(item, "Direction") switch
                {
                    "tocarrier" => count,
                    "toship" => -count,
                    _ => 0,
                };
                if (delta == 0) continue;

                _stock[symbol] = Math.Max(0, (_stock.TryGetValue(symbol, out var v) ? v : 0) + delta);
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private static string? ReadString(JsonElement item, string prop)
        => item.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
