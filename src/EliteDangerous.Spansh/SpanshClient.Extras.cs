using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace EliteDangerous.Spansh;

// Additional Spansh routers (Road to Riches, exobiology, tourist) and search endpoints (bodies,
// stations-by-service). Job routers reuse RunJobAsync; searches are single synchronous POSTs.
// Field-name status (see issue #98): the bodies/search request filters and response fields
// (name, system_name, subtype, distance, distance_to_arrival, is_landable) and the stations/search
// has_large_pad/large_pads fields were spot-checked against the live API. The riches/exobiology/tourist
// request forms and result shapes, the stations "services" array and the min_value form field remain
// unverified and are covered by fixture tests only. Searches therefore also filter client-side.
public sealed partial class SpanshClient
{
    /// <summary>Upper bound applied to <c>MaxResults</c> on the search endpoints.</summary>
    internal const int MaxSearchResults = 100;

    /// <summary>Page index sent with search requests (always the first page).</summary>
    private const int SearchPage = 0;

    /// <summary>
    /// Plot a Road to Riches route: systems along the way with the most valuable bodies to scan/map.
    /// Same job/poll lifecycle and no-throw contract as <see cref="PlotRouteAsync"/> (cancellation
    /// excepted: it rethrows <see cref="OperationCanceledException"/>). Invalid input (null query, blank
    /// start, non-positive range) returns a failure without a network call.
    /// </summary>
    /// <remarks>
    /// Request form field names and the result shape are unverified against the live Spansh API (see
    /// issue #98); they are covered by fixture tests only. An unrecognised result shape is reported as a
    /// failure rather than an empty route.
    /// </remarks>
    public Task<SpanshSurveyRouteResult> PlotRoadToRichesAsync(SpanshSurveyRouteQuery query, CancellationToken ct = default)
        => RunSurveyAsync("riches/route", query, includeMapping: true, ct);

    /// <summary>
    /// Plot an exobiology route: systems with the best predicted biological payouts. Same lifecycle and
    /// no-throw contract as <see cref="PlotRoadToRichesAsync"/>.
    /// </summary>
    /// <remarks>
    /// Request form field names (notably <c>min_value</c>, which may not apply to this router) and the
    /// result shape are unverified against the live Spansh API (see issue #98).
    /// </remarks>
    public Task<SpanshSurveyRouteResult> PlotExobiologyRouteAsync(SpanshSurveyRouteQuery query, CancellationToken ct = default)
        => RunSurveyAsync("exobiology/route", query, includeMapping: false, ct);

    /// <summary>
    /// Plot a tourist route through a set of target systems. Returns the standard waypoint result; the
    /// neutron/fuel fields are left at their defaults. Same lifecycle and no-throw contract as
    /// <see cref="PlotRouteAsync"/>. System names are sent comma-joined, so a name containing a comma
    /// cannot be represented.
    /// </summary>
    /// <remarks>
    /// Request form field names and the result shape are unverified against the live Spansh API (see
    /// issue #98); they are covered by fixture tests only.
    /// </remarks>
    public Task<SpanshRouteResult> PlotTouristRouteAsync(SpanshTouristRouteQuery query, CancellationToken ct = default)
    {
        if (query is null) return Task.FromResult(SpanshRouteResult.Failure("query is required"));
        if (string.IsNullOrWhiteSpace(query.From)) return Task.FromResult(SpanshRouteResult.Failure("start system is required"));
        if (!IsPositiveFinite(query.RangeLy)) return Task.FromResult(SpanshRouteResult.Failure("range must be > 0"));
        if (query.Systems is null || query.Systems.Count == 0) return Task.FromResult(SpanshRouteResult.Failure("at least one system is required"));

        FormUrlEncodedContent BuildForm() => new(new Dictionary<string, string>
        {
            ["source"] = query.From,
            ["range"] = Num(query.RangeLy),
            ["systems"] = string.Join(",", query.Systems),
        });
        return RunRouteJobAsync(c => _http.PostAsync($"{_options.BaseUrl.TrimEnd('/')}/tourist/route", BuildForm(), c), ParseTouristPoll, ct);
    }

    /// <summary>
    /// Search bodies (e.g. Earth-like or ammonia worlds) nearest a reference system. Never throws, except
    /// that it rethrows <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// Invalid input (null query, blank reference system, empty sub-types) returns a failure. Results are
    /// also filtered client-side on sub-type (and landability when requested), so a server that ignores a
    /// filter cannot return mislabelled bodies; if rows came back but none match, that is a failure.
    /// </summary>
    /// <remarks>
    /// The request filter names and response fields were spot-checked against the live API (see issue
    /// #98) but the contract is not formally documented by Spansh; treat them as unverified.
    /// </remarks>
    public async Task<SpanshBodiesResult> SearchBodiesAsync(SpanshBodyQuery query, CancellationToken ct = default)
    {
        if (query is null) return SpanshBodiesResult.Failure("query is required");
        if (string.IsNullOrWhiteSpace(query.ReferenceSystem)) return SpanshBodiesResult.Failure("reference system is required");
        if (query.SubTypes is null || query.SubTypes.Count == 0) return SpanshBodiesResult.Failure("at least one sub-type is required");

        var subTypes = query.SubTypes.ToArray();
        var filters = new Dictionary<string, object> { ["subtype"] = new { value = subTypes } };
        if (query.Landable is { } landable) filters["is_landable"] = new { value = landable };

        var (body, error) = await PostSearchAsync("bodies/search", filters, query.ReferenceSystem, query.MaxResults, ct).ConfigureAwait(false);
        if (body is null) return SpanshBodiesResult.Failure(error!);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var rows = Results(doc.RootElement, out var recognised);
            if (!recognised) return SpanshBodiesResult.Failure("unrecognised response shape");

            var list = new List<SpanshBody>();
            foreach (var r in rows)
            {
                var parsed = new SpanshBody(
                    Name: ReadString(r, "name") ?? "Unknown",
                    SystemName: ReadString(r, "system_name") ?? "Unknown",
                    SubType: ReadString(r, "subtype") ?? "",
                    DistanceLy: ReadDouble(r, "distance"),
                    DistanceToArrivalLs: ReadDouble(r, "distance_to_arrival"),
                    IsLandable: ReadBool(r, "is_landable"));
                // Fail-closed: drop rows the server should have excluded.
                if (!subTypes.Contains(parsed.SubType, StringComparer.OrdinalIgnoreCase)) continue;
                if (query.Landable is { } want && parsed.IsLandable != want) continue;
                list.Add(parsed);
            }
            if (rows.Count > 0 && list.Count == 0)
                return SpanshBodiesResult.Failure($"server returned {rows.Count} rows but none matched the requested filters");
            return SpanshBodiesResult.Ok(list);
        }
        catch (JsonException ex) { return SpanshBodiesResult.Failure("unparseable response: " + ex.Message); }
    }

    /// <summary>
    /// "Where's the nearest X": stations with the requested services nearest a reference system. Never
    /// throws, except that it rethrows <see cref="OperationCanceledException"/> when <paramref name="ct"/>
    /// is cancelled. Invalid input (null query, blank reference system, empty services) returns a failure.
    /// Results are also filtered client-side on services and large pad; if rows came back but none match,
    /// that is a failure.
    /// </summary>
    /// <remarks>
    /// Request filter names and the <c>services</c> response array are unverified against the live API
    /// (see issue #98); only <c>has_large_pad</c>/<c>large_pads</c> were spot-checked. A row satisfies a
    /// service if its <c>services</c> array lists it or its <c>has_&lt;service&gt;</c> flag (e.g.
    /// <c>has_shipyard</c>) is true; rows offering neither signal are dropped.
    /// </remarks>
    public async Task<SpanshServiceStationsResult> SearchStationsByServiceAsync(SpanshServiceQuery query, CancellationToken ct = default)
    {
        if (query is null) return SpanshServiceStationsResult.Failure("query is required");
        if (string.IsNullOrWhiteSpace(query.ReferenceSystem)) return SpanshServiceStationsResult.Failure("reference system is required");
        if (query.Services is null || query.Services.Count == 0) return SpanshServiceStationsResult.Failure("at least one service is required");

        var wanted = query.Services.ToArray();
        var filters = new Dictionary<string, object>
        {
            ["services"] = wanted.Select(s => new { name = s }).ToArray(),
        };
        if (query.RequireLargePad) filters["has_large_pad"] = new { value = true };

        var (body, error) = await PostSearchAsync("stations/search", filters, query.ReferenceSystem, query.MaxResults, ct).ConfigureAwait(false);
        if (body is null) return SpanshServiceStationsResult.Failure(error!);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var rows = Results(doc.RootElement, out var recognised);
            if (!recognised) return SpanshServiceStationsResult.Failure("unrecognised response shape");

            var list = new List<SpanshServiceStation>();
            foreach (var r in rows)
            {
                var services = new List<string>();
                if (r.TryGetProperty("services", out var sv) && sv.ValueKind == JsonValueKind.Array)
                    foreach (var s in sv.EnumerateArray())
                    {
                        var n = s.ValueKind == JsonValueKind.String ? s.GetString() : s.ValueKind == JsonValueKind.Object ? ReadString(s, "name") : null;
                        if (!string.IsNullOrEmpty(n)) services.Add(n);
                    }
                var station = new SpanshServiceStation(
                    SystemName: ReadString(r, "system_name") ?? "Unknown",
                    StationName: ReadString(r, "name") ?? "Unknown",
                    StationType: ReadString(r, "type") ?? "",
                    DistanceLy: ReadDouble(r, "distance"),
                    DistanceToArrivalLs: ReadDouble(r, "distance_to_arrival"),
                    HasLargePad: ReadBool(r, "has_large_pad") || ReadInt(r, "large_pads") > 0,
                    Services: services);
                // Fail-closed: drop rows the server should have excluded.
                if (query.RequireLargePad && !station.HasLargePad) continue;
                if (!wanted.All(w => OffersService(r, services, w))) continue;
                list.Add(station);
            }
            if (rows.Count > 0 && list.Count == 0)
                return SpanshServiceStationsResult.Failure($"server returned {rows.Count} rows but none matched the requested filters");
            return SpanshServiceStationsResult.Ok(list);
        }
        catch (JsonException ex) { return SpanshServiceStationsResult.Failure("unparseable response: " + ex.Message); }
    }

    /// <summary>True when the row lists <paramref name="service"/> in its services array or has the matching <c>has_*</c> flag.</summary>
    private static bool OffersService(JsonElement row, List<string> services, string service)
    {
        if (services.Contains(service, StringComparer.OrdinalIgnoreCase)) return true;
        var flag = "has_" + service.Trim().ToLowerInvariant().Replace(' ', '_');
        return ReadBool(row, flag);
    }

    private static bool IsPositiveFinite(double v) => v > 0 && double.IsFinite(v);

    private async Task<(string? Body, string? Error)> PostSearchAsync(
        string path, Dictionary<string, object> filters, string referenceSystem, int size, CancellationToken ct)
    {
        try
        {
            var request = new
            {
                filters,
                sort = new[] { new Dictionary<string, object> { ["distance"] = new { direction = "asc" } } },
                size = Math.Clamp(size, 1, MaxSearchResults),
                page = SearchPage,
                reference_system = referenceSystem,
            };
            using var response = await SendWithRetryAsync(
                c => _http.PostAsJsonAsync($"{_options.BaseUrl.TrimEnd('/')}/{path}", request, Json, c), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, $"HTTP {(int)response.StatusCode}");
            return (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>The object rows under <c>results</c>; <paramref name="recognised"/> is false when <c>results</c> is not an array.</summary>
    private static List<JsonElement> Results(JsonElement root, out bool recognised)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            recognised = true;
            return r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        }
        recognised = false;
        return new List<JsonElement>();
    }

    private Task<SpanshSurveyRouteResult> RunSurveyAsync(string path, SpanshSurveyRouteQuery query, bool includeMapping, CancellationToken ct)
    {
        if (query is null) return Task.FromResult(SpanshSurveyRouteResult.Failure("query is required"));
        if (string.IsNullOrWhiteSpace(query.From)) return Task.FromResult(SpanshSurveyRouteResult.Failure("start system is required"));
        if (!IsPositiveFinite(query.RangeLy)) return Task.FromResult(SpanshSurveyRouteResult.Failure("range must be > 0"));
        if (query.RadiusLy < 0 || query.MaxSystems < 0) return Task.FromResult(SpanshSurveyRouteResult.Failure("radius and max systems must not be negative"));

        static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        FormUrlEncodedContent BuildForm()
        {
            var form = new Dictionary<string, string>
            {
                ["from"] = query.From,
                ["range"] = Num(query.RangeLy),
                ["radius"] = I(query.RadiusLy),
                ["max_results"] = I(query.MaxSystems),
                ["max_distance"] = I(query.MaxDistanceLs),
                ["min_value"] = I(query.MinValue),
                ["loop"] = query.Loop ? "1" : "0",
                ["avoid_thargoids"] = query.AvoidThargoids ? "1" : "0",
            };
            if (!string.IsNullOrWhiteSpace(query.To)) form["to"] = query.To;
            if (includeMapping) form["use_mapping_value"] = query.UseMappingValue ? "1" : "0";
            return new FormUrlEncodedContent(form);
        }
        return RunJobAsync(
            c => _http.PostAsync($"{_options.BaseUrl.TrimEnd('/')}/{path}", BuildForm(), c),
            ParseSurveyPoll, SpanshSurveyRouteResult.Failure, ct);
    }

    /// <summary>Parse a Road to Riches / exobiology poll: <c>result</c> is an array of systems, each with <c>bodies</c>.</summary>
    private static (bool Done, SpanshSurveyRouteResult Result) ParseSurveyPoll(string body)
        => ParsePoll(body, SpanshSurveyRouteResult.Failure, root =>
        {
            // A present-but-empty array is a legitimately empty route; any other shape is not recognised.
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return SpanshSurveyRouteResult.Failure("unrecognised result shape");

            var systems = new List<SpanshSurveySystem>();
            foreach (var sys in result.EnumerateArray())
            {
                if (sys.ValueKind != JsonValueKind.Object) continue;
                var bodies = new List<SpanshSurveyBody>();
                if (sys.TryGetProperty("bodies", out var bs) && bs.ValueKind == JsonValueKind.Array)
                    foreach (var b in bs.EnumerateArray())
                    {
                        if (b.ValueKind != JsonValueKind.Object) continue;
                        var landmarks = new List<SpanshLandmark>();
                        if (b.TryGetProperty("landmarks", out var lm) && lm.ValueKind == JsonValueKind.Array)
                            foreach (var l in lm.EnumerateArray())
                                if (l.ValueKind == JsonValueKind.Object)
                                    landmarks.Add(new SpanshLandmark(
                                        ReadString(l, "type") ?? "", ReadString(l, "subtype") ?? "",
                                        ReadInt(l, "count"), ReadLong(l, "value")));
                        bodies.Add(new SpanshSurveyBody(
                            Name: ReadString(b, "name") ?? "Unknown",
                            SubType: ReadString(b, "subtype") ?? "",
                            DistanceToArrivalLs: ReadDouble(b, "distance_to_arrival"),
                            ScanValue: ReadLong(b, "estimated_scan_value"),
                            MappingValue: ReadLong(b, "estimated_mapping_value"),
                            IsTerraformable: ReadBool(b, "is_terraformable"),
                            Landmarks: landmarks));
                    }
                systems.Add(new SpanshSurveySystem(
                    Name: ReadString(sys, "name") ?? "Unknown",
                    Jumps: ReadInt(sys, "jumps"),
                    DistanceJumpedLy: ReadDouble(sys, "distance_jumped"),
                    Bodies: bodies));
            }
            return SpanshSurveyRouteResult.Ok(systems);
        });

    /// <summary>
    /// Parse a tourist poll. The stop list may arrive as a bare <c>result</c> array or under
    /// <c>result.system_jumps</c>/<c>result.jumps</c>; each stop's name is <c>system</c> or <c>name</c>.
    /// </summary>
    private static (bool Done, SpanshRouteResult Result) ParseTouristPoll(string body)
        => ParsePoll(body, SpanshRouteResult.Failure, root =>
        {
            JsonElement stops = default;
            if (root.TryGetProperty("result", out var result))
            {
                if (result.ValueKind == JsonValueKind.Array) stops = result;
                else if (result.ValueKind == JsonValueKind.Object)
                {
                    if (result.TryGetProperty("system_jumps", out var sj) && sj.ValueKind == JsonValueKind.Array) stops = sj;
                    else if (result.TryGetProperty("jumps", out var j) && j.ValueKind == JsonValueKind.Array) stops = j;
                }
            }
            if (stops.ValueKind != JsonValueKind.Array)
                return SpanshRouteResult.Failure("unrecognised result shape");

            var waypoints = new List<SpanshRouteWaypoint>();
            foreach (var hop in stops.EnumerateArray())
            {
                if (hop.ValueKind != JsonValueKind.Object) continue;
                var jumped = ReadDouble(hop, "distance_jumped");
                waypoints.Add(new SpanshRouteWaypoint(
                    System: ReadString(hop, "system") ?? ReadString(hop, "name") ?? "Unknown",
                    Jumps: ReadInt(hop, "jumps"),
                    IsNeutron: false,
                    DistanceJumpedLy: jumped > 0 ? jumped : ReadDouble(hop, "distance"),
                    DistanceRemainingLy: ReadDouble(hop, "distance_left")));
            }
            return SpanshRouteResult.Ok(waypoints);
        });

    private static long ReadLong(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt64(out var n)) return n;
        // Out-of-range or fractional: clamp rather than rely on unspecified (long) conversion behaviour.
        if (!v.TryGetDouble(out var d) || !double.IsFinite(d)) return 0;
        return d >= long.MaxValue ? long.MaxValue : d <= long.MinValue ? long.MinValue : (long)d;
    }
}
