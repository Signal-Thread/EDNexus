using System.Text.Json;
using EDNexus.Core.Colonisation;
using EliteDangerous.Spansh;

namespace EDNexus.Core.Trade;

/// <summary>
/// The engine-side <see cref="ITradeSearch"/> adapter over the reusable <see cref="EliteDangerous.Spansh"/>
/// client. The library is pure transport and returns Spansh-shaped stations; this adapter applies the
/// EDNexus policy on top: canonical commodity matching (so "Low Temperature Diamonds" and the
/// <c>lowtemperaturediamond</c> symbol match), picking the buy/sell side, and caching answers on disk
/// so repeat lookups skip the network.
/// </summary>
public sealed class SpanshTradeSearch : ITradeSearch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SpanshClient _client;
    private readonly IResponseCache? _cache;

    public string SourceName => "Spansh";

    public SpanshTradeSearch(SpanshClient client, IResponseCache? cache = null)
    {
        _client = client;
        _cache = cache;
    }

    public async Task<IReadOnlyList<TradeStationQuote>> SearchAsync(TradeQuery query, CancellationToken ct = default)
    {
        var key = CacheKey(query);
        if (_cache.GetTyped<List<TradeStationQuote>>(key, Json) is { } cached)
            return cached;

        var result = await _client.SearchStationsAsync(new SpanshStationQuery
        {
            CommodityName = query.Commodity,
            ReferenceSystem = query.ReferenceSystem,
            WantsDemand = query.Mode == TradeMode.Sell,
            MaxResults = query.MaxResults,
        }, ct).ConfigureAwait(false);

        // A transport failure is transient — surface it as "no results" without caching, so the next
        // attempt retries rather than serving an empty answer for the whole TTL.
        if (!result.IsOk) return Array.Empty<TradeStationQuote>();

        var quotes = Map(result.Stations, query);
        _cache?.Put(key, JsonSerializer.Serialize(quotes, Json));
        return quotes;
    }

    /// <summary>Translate Spansh stations into ranked quotes, keeping the source's nearest-first order.</summary>
    private static IReadOnlyList<TradeStationQuote> Map(IReadOnlyList<SpanshStation> stations, TradeQuery query)
    {
        var wanted = CommodityName.Canonicalize(query.Commodity);
        var quotes = new List<TradeStationQuote>();

        foreach (var station in stations)
        {
            var line = station.Commodities.FirstOrDefault(c => CommodityName.Canonicalize(c.Name) == wanted);
            if (line is null) continue;

            var price = query.Mode == TradeMode.Sell ? line.SellPrice : line.BuyPrice;
            var quantity = query.Mode == TradeMode.Sell ? line.Demand : line.Supply;
            if (price <= 0) continue;

            quotes.Add(new TradeStationQuote(
                station.SystemName, station.StationName, station.DistanceLy, price, quantity, station.MarketUpdated));
        }

        return quotes;
    }

    private static string CacheKey(TradeQuery q) =>
        $"spansh|stations|{q.Mode}|{q.ReferenceSystem}|{CommodityName.Canonicalize(q.Commodity)}|{q.MaxResults}";
}
