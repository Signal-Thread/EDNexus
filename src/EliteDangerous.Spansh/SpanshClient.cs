using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EliteDangerous.Spansh;

/// <summary>
/// Queries the Spansh <c>stations/search</c> endpoint and parses the reply into plain Spansh-shaped
/// records. This is pure transport: it does not decide which commodity to look up or how to rank the
/// answer — that policy belongs to the caller. Following the reporting clients' convention it never
/// throws for network/HTTP problems; failures surface as <see cref="SpanshStationsResult.TransportError"/>.
/// A single instance is safe to reuse across queries.
/// </summary>
/// <remarks>
/// The request/response shapes follow Spansh's documented <c>stations/search</c> schema. They are
/// isolated to <see cref="BuildRequest"/> and <see cref="Parse"/> so they are easy to reconcile
/// against the live API — useful because the field names are covered by fixture tests, not a live call.
/// </remarks>
public sealed class SpanshClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SpanshClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public SpanshClient(SpanshClientOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(Sanitize(_options.SoftwareName), Sanitize(_options.SoftwareVersion)));
    }

    /// <summary>
    /// Search stations near the reference system for the wanted side of a commodity. Never throws:
    /// a transport or HTTP failure comes back as <see cref="SpanshStationsResult.TransportError"/>.
    /// </summary>
    public async Task<SpanshStationsResult> SearchStationsAsync(SpanshStationQuery query, CancellationToken ct = default)
    {
        try
        {
            var request = BuildRequest(query);
            using var response = await SendWithRetryAsync(
                c => _http.PostAsJsonAsync($"{_options.BaseUrl.TrimEnd('/')}/stations/search", request, Json, c), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return SpanshStationsResult.TransportError($"HTTP {(int)response.StatusCode}");

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return SpanshStationsResult.TransportError(ex.Message);
        }
    }

    /// <summary>
    /// Plot a neutron-highway route (boosting off neutron stars). Spansh runs this as a background job:
    /// this submits the job, then polls for the result (spacing polls by
    /// <see cref="SpanshClientOptions.RoutePollInterval"/>) until it is ready, the attempt budget runs
    /// out, or the job errors. Never throws — every failure comes back as
    /// <see cref="SpanshRouteResult.Failure"/>.
    /// </summary>
    public Task<SpanshRouteResult> PlotRouteAsync(SpanshRouteQuery query, CancellationToken ct = default)
    {
        var submitUrl =
            $"{_options.BaseUrl.TrimEnd('/')}/route?efficiency={query.Efficiency}" +
            $"&range={Num(query.RangeLy)}" +
            $"&from={Uri.EscapeDataString(query.From)}&to={Uri.EscapeDataString(query.To)}";
        return RunRouteJobAsync(c => _http.PostAsync(submitUrl, content: null, c), ParseNeutronPoll, ct);
    }

    /// <summary>
    /// Plot a plain jump route with neutron boosting turned off, modelling the ship's real FSD so the
    /// per-jump fuel burn is accurate. Backed by Spansh's "galaxy plotter". Same job/poll lifecycle and
    /// no-throw contract as <see cref="PlotRouteAsync"/>.
    /// </summary>
    public Task<SpanshRouteResult> PlotGalaxyRouteAsync(SpanshGalaxyRouteQuery query, CancellationToken ct = default)
    {
        FormUrlEncodedContent BuildForm() => new(new Dictionary<string, string>
        {
            ["source"] = query.From,
            ["destination"] = query.To,
            ["algorithm"] = query.Algorithm,
            ["use_supercharge"] = query.UseSupercharge ? "1" : "0",
            ["use_injections"] = "0",
            ["exclude_secondary"] = "0",
            ["optimal_mass"] = Num(query.OptimalMass),
            ["base_mass"] = Num(query.BaseMass),
            ["tank_size"] = Num(query.TankSize),
            ["internal_tank_size"] = Num(query.ReserveSize),
            ["fuel_power"] = Num(query.FuelPower),
            ["fuel_multiplier"] = Num(query.FuelMultiplier),
            ["max_fuel_per_jump"] = Num(query.MaxFuelPerJump),
            ["range_boost"] = Num(query.RangeBoost),
            ["cargo"] = Num(query.Cargo),
        });
        return RunRouteJobAsync(c => _http.PostAsync($"{_options.BaseUrl.TrimEnd('/')}/generic/route", BuildForm(), c), ParseShipPoll, ct);
    }

    /// <summary>
    /// Plot a fleet-carrier route (fixed 500 ly hops, tritium-fuelled, never boosting). Backed by
    /// Spansh's fleet-carrier plotter. Same job/poll lifecycle and no-throw contract as
    /// <see cref="PlotRouteAsync"/>; the returned waypoints carry the tritium burn/restock fields.
    /// </summary>
    public Task<SpanshRouteResult> PlotFleetCarrierRouteAsync(SpanshFleetCarrierRouteQuery query, CancellationToken ct = default)
    {
        FormUrlEncodedContent BuildForm() => new(new Dictionary<string, string>
        {
            ["source"] = query.From,
            ["destination"] = query.To,
            ["capacity_used"] = Num(query.CapacityUsed),
            ["calculate_starting_fuel"] = query.CalculateStartingFuel ? "1" : "0",
        });
        return RunRouteJobAsync(c => _http.PostAsync($"{_options.BaseUrl.TrimEnd('/')}/fleetcarrier/route", BuildForm(), c), ParseFleetCarrierPoll, ct);
    }

    /// <summary>
    /// The shared submit-then-poll lifecycle every Spansh route endpoint uses: POST to start the job,
    /// then GET <c>results/{job}</c> until the supplied <paramref name="parsePoll"/> reports the job is
    /// done. Endpoint-specific bits — the submit request and the result shape — are the two delegates.
    /// Never throws (cancellation excepted); failures come back as <see cref="SpanshRouteResult.Failure"/>.
    /// </summary>
    private async Task<SpanshRouteResult> RunRouteJobAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> submit,
        Func<string, (bool Done, SpanshRouteResult Result)> parsePoll,
        CancellationToken ct)
    {
        string jobId;
        try
        {
            using var response = await SendWithRetryAsync(submit, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return SpanshRouteResult.Failure($"HTTP {(int)response.StatusCode}");

            var submitBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var submitDoc = JsonDocument.Parse(submitBody);
            if (ReadString(submitDoc.RootElement, "job") is not { Length: > 0 } id)
                return SpanshRouteResult.Failure(ReadString(submitDoc.RootElement, "error") ?? "no job id returned");
            jobId = id;
        }
        catch (JsonException ex) { return SpanshRouteResult.Failure("unparseable submit response: " + ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return SpanshRouteResult.Failure(ex.Message); }

        // The id goes into a URL path: "." / ".." survive escaping and would address another endpoint.
        if (jobId.Trim() is "." or "..")
            return SpanshRouteResult.Failure("invalid job id returned");

        // Poll the job to completion. "queued" / "running" means keep waiting; "ok" carries the route.
        var transientFailures = 0;
        string lastTransient = "";
        for (var attempt = 0; attempt < Math.Max(1, _options.RoutePollAttempts); attempt++)
        {
            if (attempt > 0 && _options.RoutePollInterval > TimeSpan.Zero)
                await Task.Delay(_options.RoutePollInterval, ct).ConfigureAwait(false);

            try
            {
                using var poll = await _http.GetAsync(
                    $"{_options.BaseUrl.TrimEnd('/')}/results/{Uri.EscapeDataString(jobId)}", ct).ConfigureAwait(false);
                if (!poll.IsSuccessStatusCode)
                {
                    // A 5xx or 429 on one poll says nothing about the job itself: back off and poll again
                    // a few times rather than discarding a minutes-long route. Other statuses are final.
                    if (IsTransient(poll.StatusCode))
                    {
                        lastTransient = $"HTTP {(int)poll.StatusCode}";
                        if (++transientFailures > _options.RoutePollRetries) return SpanshRouteResult.Failure(lastTransient);
                        await BackOffAsync(poll, ct).ConfigureAwait(false);
                        continue;
                    }
                    return SpanshRouteResult.Failure($"HTTP {(int)poll.StatusCode}");
                }

                var pollBody = await poll.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                transientFailures = 0;
                var (done, result) = parsePoll(pollBody);
                if (done) return result;
            }
            catch (JsonException ex) { return SpanshRouteResult.Failure("unparseable poll response: " + ex.Message); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A network error or an HttpClient timeout: transient too, up to the same budget.
                lastTransient = ex.Message;
                if (++transientFailures > _options.RoutePollRetries) return SpanshRouteResult.Failure(lastTransient);
                if (_options.TransientRetryDelay > TimeSpan.Zero) await Task.Delay(_options.TransientRetryDelay, ct).ConfigureAwait(false);
            }
        }

        return SpanshRouteResult.Failure("route timed out");
    }

    private static bool IsTransient(HttpStatusCode status)
        => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>Waits the server's <c>Retry-After</c> (bounded) or the default retry delay before the next poll.</summary>
    private async Task BackOffAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var wait = ReadRetryAfter(response) ?? _options.TransientRetryDelay;
        if (wait > _options.MaxRetryAfter) wait = _options.MaxRetryAfter;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request, retrying once when the server answers 429/503 and its <c>Retry-After</c> (or the
    /// default retry delay) is within <see cref="SpanshClientOptions.MaxRetryAfter"/>; a longer wait is
    /// not honoured and the busy response is returned for the caller to report.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        var response = await send(ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            return response;

        var wait = ReadRetryAfter(response) ?? _options.TransientRetryDelay;
        if (wait > _options.MaxRetryAfter) return response;

        response.Dispose();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        return await send(ct).ConfigureAwait(false);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta >= TimeSpan.Zero ? delta : null;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>
    /// Classify a poll reply's job status. <c>Running</c> while queued/running, <c>Failed</c> on an
    /// error status (with <paramref name="error"/> set), <c>Ready</c> once the result is available.
    /// </summary>
    private static JobPhase ReadPhase(JsonElement root, out string? error)
    {
        error = null;
        var status = ReadString(root, "status");
        if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            return JobPhase.Running;
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            error = ReadString(root, "error") ?? $"job status '{status}'";
            return JobPhase.Failed;
        }
        return JobPhase.Ready;
    }

    private enum JobPhase { Running, Ready, Failed }

    /// <summary>
    /// Parse a neutron-plotter poll. Returns <c>(done: false, …)</c> while queued/running and
    /// <c>(done: true, result)</c> once finished. Missing or reshaped fields degrade to a failure or
    /// an empty route rather than throwing.
    /// </summary>
    private static (bool Done, SpanshRouteResult Result) ParseNeutronPoll(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        switch (ReadPhase(root, out var error))
        {
            case JobPhase.Running: return (false, SpanshRouteResult.Failure("still running"));
            case JobPhase.Failed: return (true, SpanshRouteResult.Failure(error!));
        }

        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("system_jumps", out var jumps) || jumps.ValueKind != JsonValueKind.Array)
            return (true, SpanshRouteResult.Ok(Array.Empty<SpanshRouteWaypoint>()));

        var waypoints = new List<SpanshRouteWaypoint>();
        foreach (var hop in jumps.EnumerateArray())
            waypoints.Add(new SpanshRouteWaypoint(
                System: ReadString(hop, "system") ?? "Unknown",
                Jumps: ReadInt(hop, "jumps"),
                IsNeutron: hop.TryGetProperty("neutron_star", out var n) && n.ValueKind is JsonValueKind.True,
                DistanceJumpedLy: ReadDouble(hop, "distance_jumped"),
                DistanceRemainingLy: ReadDouble(hop, "distance_left")));

        return (true, SpanshRouteResult.Ok(waypoints));
    }

    /// <summary>
    /// Parse a galaxy-plotter poll. Each waypoint is a single plain jump, carrying the ship-fuel figures
    /// Spansh reports. Same running/ready/failed handling as the neutron parser.
    /// </summary>
    private static (bool Done, SpanshRouteResult Result) ParseShipPoll(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        switch (ReadPhase(root, out var error))
        {
            case JobPhase.Running: return (false, SpanshRouteResult.Failure("still running"));
            case JobPhase.Failed: return (true, SpanshRouteResult.Failure(error!));
        }

        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("jumps", out var jumps) || jumps.ValueKind != JsonValueKind.Array)
            return (true, SpanshRouteResult.Ok(Array.Empty<SpanshRouteWaypoint>()));

        var waypoints = new List<SpanshRouteWaypoint>();
        var index = 0;
        foreach (var hop in jumps.EnumerateArray())
        {
            waypoints.Add(new SpanshRouteWaypoint(
                System: ReadString(hop, "name") ?? "Unknown",
                Jumps: index == 0 ? 0 : 1,
                IsNeutron: false,   // a no-boost route never scoop-boosts, even past a neutron star
                DistanceJumpedLy: ReadDouble(hop, "distance"),
                DistanceRemainingLy: ReadDouble(hop, "distance_to_destination"),
                FuelUsed: ReadNullableDouble(hop, "fuel_used"),
                FuelInTank: ReadNullableDouble(hop, "fuel_in_tank"),
                IsScoopable: ReadBool(hop, "is_scoopable"),
                MustRestock: ReadBool(hop, "must_refuel")));
            index++;
        }

        return (true, SpanshRouteResult.Ok(waypoints));
    }

    /// <summary>
    /// Parse a fleet-carrier-plotter poll. Each waypoint is one 500 ly hop, carrying the tritium burn,
    /// tank level and restock hints Spansh reports. Same running/ready/failed handling as the others.
    /// </summary>
    private static (bool Done, SpanshRouteResult Result) ParseFleetCarrierPoll(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        switch (ReadPhase(root, out var error))
        {
            case JobPhase.Running: return (false, SpanshRouteResult.Failure("still running"));
            case JobPhase.Failed: return (true, SpanshRouteResult.Failure(error!));
        }

        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("jumps", out var jumps) || jumps.ValueKind != JsonValueKind.Array)
            return (true, SpanshRouteResult.Ok(Array.Empty<SpanshRouteWaypoint>()));

        var waypoints = new List<SpanshRouteWaypoint>();
        var index = 0;
        foreach (var hop in jumps.EnumerateArray())
        {
            waypoints.Add(new SpanshRouteWaypoint(
                System: ReadString(hop, "name") ?? "Unknown",
                Jumps: index == 0 ? 0 : 1,
                IsNeutron: false,
                DistanceJumpedLy: ReadDouble(hop, "distance"),
                DistanceRemainingLy: ReadDouble(hop, "distance_to_destination"),
                FuelUsed: ReadNullableDouble(hop, "fuel_used"),
                FuelInTank: ReadNullableDouble(hop, "fuel_in_tank"),
                MustRestock: ReadInt(hop, "must_restock") != 0,
                RestockAmount: ReadNullableDouble(hop, "restock_amount"),
                HasIcyRing: ReadBool(hop, "has_icy_ring")));
            index++;
        }

        return (true, SpanshRouteResult.Ok(waypoints));
    }

    /// <summary>
    /// Build the <c>stations/search</c> body: rank by distance from the reference system and filter
    /// to stations that have the wanted side of the commodity's market. (Spansh filter shape.)
    /// </summary>
    private static object BuildRequest(SpanshStationQuery query)
    {
        // Selling to a station needs demand there; buying from one needs supply. Spansh range filters
        // take a [min, max] pair; an open upper bound means "at least 1".
        var side = query.WantsDemand ? "demand" : "supply";
        var marketFilter = new Dictionary<string, object>
        {
            ["name"] = query.CommodityName,
            [side] = new { value = new[] { "1", "" } },
        };

        return new
        {
            filters = new { market = new[] { marketFilter } },
            sort = new[] { new Dictionary<string, object> { ["distance"] = new { direction = "asc" } } },
            size = query.MaxResults,
            reference_system = query.ReferenceSystem,
        };
    }

    /// <summary>
    /// Map the response into stations, defensively: missing or reshaped fields yield fewer results
    /// rather than throwing, so a schema drift degrades gracefully instead of crashing the caller.
    /// </summary>
    private static SpanshStationsResult Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return SpanshStationsResult.Ok(Array.Empty<SpanshStation>());

            var stations = new List<SpanshStation>();
            foreach (var station in results.EnumerateArray())
            {
                var commodities = new List<SpanshCommodity>();
                if (station.TryGetProperty("market", out var market) && market.ValueKind == JsonValueKind.Array)
                    foreach (var line in market.EnumerateArray())
                        commodities.Add(new SpanshCommodity(
                            Name: ReadString(line, "commodity") ?? "",
                            BuyPrice: ReadInt(line, "buy_price"),
                            SellPrice: ReadInt(line, "sell_price"),
                            Supply: ReadInt(line, "supply"),
                            Demand: ReadInt(line, "demand")));

                stations.Add(new SpanshStation(
                    SystemName: ReadString(station, "system_name") ?? "Unknown",
                    StationName: ReadString(station, "name") ?? "Unknown",
                    DistanceLy: ReadDouble(station, "distance"),
                    MarketUpdated: ReadDate(station, "market_updated_at"),
                    Commodities: commodities));
            }

            return SpanshStationsResult.Ok(stations);
        }
        catch (JsonException ex)
        {
            return SpanshStationsResult.TransportError("unparseable response: " + ex.Message);
        }
    }

    private static string? ReadString(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int ReadInt(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static double ReadDouble(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private static double? ReadNullableDouble(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static bool ReadBool(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && (v.ValueKind is JsonValueKind.True
            || (v.ValueKind is JsonValueKind.Number && v.TryGetInt32(out var n) && n != 0));

    /// <summary>Format a number for a Spansh request body — invariant culture, so '.' is always the decimal point.</summary>
    private static string Num(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadDate(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var d) ? d : null;

    /// <summary>User-Agent product tokens can't contain whitespace or separators; collapse them.</summary>
    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "app" : cleaned;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
