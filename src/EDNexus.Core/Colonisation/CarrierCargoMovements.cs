using System.Text.Json;
using EDNexus.Core.Journal;

namespace EDNexus.Core.Colonisation;

/// <summary>One commodity's signed change in a carrier's hold: positive when it goes in, negative when it comes out.</summary>
/// <param name="Commodity">The commodity exactly as the journal spells it (e.g. <c>steel</c>); each consumer canonicalises it its own way.</param>
/// <param name="Units">Tons moved, never zero.</param>
public sealed record CarrierCargoDelta(string Commodity, int Units);

/// <summary>
/// A journal event read as a change to a carrier's cargo.
/// </summary>
/// <param name="MarketId">
/// The carrier it happened on, when the event or the dock says. Null when a transfer happened with no
/// known dock; nothing can then be said about <em>which</em> carrier it was.
/// </param>
/// <param name="IsMarketTrade">
/// True for a <c>MarketBuy</c>/<c>MarketSell</c>. Those fire at every station, so unlike a transfer they
/// only mean "my carrier" once <paramref name="MarketId"/> is matched against the commander's own.
/// </param>
/// <param name="Deltas">The commodities moved, in event order.</param>
public sealed record CarrierCargoMovement(long? MarketId, bool IsMarketTrade, IReadOnlyList<CarrierCargoDelta> Deltas);

/// <summary>
/// The one definition of "this journal event moved cargo on a carrier", used by both the local tally
/// (<see cref="CarrierCargoTracker"/>) and the Raven Colonial sharing, so the two cannot disagree.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>CargoTransfer</c>: <c>tocarrier</c> adds, <c>toship</c> removes. The event does not name the
/// carrier, so the caller supplies the docked market id.</item>
/// <item><c>MarketSell</c> to a carrier's market adds; <c>MarketBuy</c> from it removes. A carrier owner
/// stocks and drains their hold this way as much as by transferring.</item>
/// </list>
/// This is pure: it decides what moved, never whether the carrier is the commander's own.
/// </remarks>
public static class CarrierCargoMovements
{
    /// <summary>The movement an event describes, or null when it is not one (or carries nothing usable).</summary>
    /// <param name="e">The journal event.</param>
    /// <param name="dockedMarketId">The market id the commander is docked at, used for a <c>CargoTransfer</c>.</param>
    public static CarrierCargoMovement? Read(JournalEntry e, long? dockedMarketId)
    {
        switch (e.Event)
        {
            case "CargoTransfer":
                return ReadTransfer(e, dockedMarketId);

            case "MarketSell":
            case "MarketBuy":
                if (e.GetInt64("MarketID") is not long marketId || marketId <= 0) return null;
                var sign = e.Event == "MarketSell" ? 1 : -1;
                return Build(marketId, isMarketTrade: true, ReadOne(e.GetString("Type"), CountOf(e.Raw), sign));

            default:
                return null;
        }
    }

    private static CarrierCargoMovement? ReadTransfer(JournalEntry e, long? dockedMarketId)
    {
        if (!e.Raw.TryGetProperty("Transfers", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

        var deltas = new List<CarrierCargoDelta>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var sign = ReadString(item, "Direction") switch
            {
                "tocarrier" => 1,
                "toship" => -1,
                _ => 0,
            };
            if (sign == 0) continue;

            deltas.AddRange(ReadOne(ReadString(item, "Type"), CountOf(item), sign));
        }
        return Build(dockedMarketId > 0 ? dockedMarketId : null, isMarketTrade: false, deltas);
    }

    private static CarrierCargoMovement? Build(long? marketId, bool isMarketTrade, IReadOnlyList<CarrierCargoDelta> deltas)
        => deltas.Count == 0 ? null : new CarrierCargoMovement(marketId, isMarketTrade, deltas);

    private static IReadOnlyList<CarrierCargoDelta> ReadOne(string? type, int count, int sign)
        => string.IsNullOrWhiteSpace(type) || count <= 0
            ? Array.Empty<CarrierCargoDelta>()
            : new[] { new CarrierCargoDelta(type.Trim(), sign * count) };

    private static int CountOf(JsonElement item)
        => item.TryGetProperty("Count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : 0;

    private static string? ReadString(JsonElement item, string prop)
        => item.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
