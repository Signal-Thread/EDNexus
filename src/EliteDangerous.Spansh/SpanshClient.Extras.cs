using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace EliteDangerous.Spansh;

// Additional Spansh routers (Road to Riches, exobiology, tourist) and search endpoints (bodies,
// stations-by-service). Job routers reuse RunJobAsync; searches are single synchronous POSTs.
// Request field names follow Spansh's site forms / search API and are covered by fixture tests only.
public sealed partial class SpanshClient
{
    /// <summary>
    /// Plot a Road to Riches route: systems along the way with the most valuable bodies to scan/map.
    /// Same job/poll lifecycle and no-throw contract as <see cref="PlotRouteAsync"/>.
    /// </summary>
    public Task<SpanshSurveyRouteResult> PlotRoadToRichesAsync(SpanshSurveyRouteQuery query, CancellationToken ct = default)
        => RunSurveyAsync("riches/route", query, includeMapping: true, ct);

    /// <summary>
    /// Plot an exobiology route: systems with the best predicted biological payouts. Same lifecycle and
    /// no-throw contract as <see cref="PlotRouteAsync"/>.
    /// </summary>
    public Task<SpanshSurveyRouteResult> PlotExobiologyRouteAsync(SpanshSurveyRouteQuery query, CancellationToken ct = default)
        => RunSurveyAsync("exobiology/route", query, includeMapping: false, ct);

    /// <summary>
    /// Plot a tourist route through a set of target systems. Returns the standard waypoint result; the
    /// neutron/fuel fields are left at their defaults.
    /// </summary>
    public Task<SpanshRouteResult> PlotTouristRouteAsync(SpanshTouristRouteQuery query, CancellationToken ct = default)
    {
        FormUrlEncodedContent BuildForm() => new(new Dictionary<string, string>
        {
            ["source"] = query.From,
            ["range"] = Num(query.RangeLy),
            ["systems"] = string.Join(",", query.Systems),
        });
        return RunRouteJobAsync(c => _http.PostAsync($"{_options.BaseUrl.TrimEnd('/')}/tourist/route", BuildForm(), c), ParseTouristPoll, ct);
    }

    /// <summary>Search bodies (e.g. Earth-like or ammonia worlds) nearest a reference system. Never throws.</summary>
    public async Task<SpanshBodiesResult> SearchBodiesAsync(SpanshBodyQuery query, CancellationToken ct = default)
    {
        var filters = new Dictionary<string, object> { ["subtype"] = new { value = query.SubTypes } };
        if (query.Landable is { } landable) filters["is_landable"] = new { value = landable };

        var (body, error) = await PostSearchAsync("bodies/search", filters, query.ReferenceSystem, query.MaxResults, ct).ConfigureAwait(false);
        if (body is null) return SpanshBodiesResult.Failure(error!);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var list = new List<SpanshBody>();
            foreach (var r in Results(doc.RootElement))
                list.Add(new SpanshBody(
                    Name: ReadString(r, "name") ?? "Unknown",
                    SystemName: ReadString(r, "system_name") ?? "Unknown",
                    SubType: ReadString(r, "subtype") ?? "",
                    DistanceLy: ReadDouble(r, "distance"),
                    DistanceToArrivalLs: ReadDouble(r, "distance_to_arrival"),
                    IsLandable: ReadBool(r, "is_landable")));
            return SpanshBodiesResult.Ok(list);
        }
        catch (JsonException ex) { return SpanshBodiesResult.Failure("unparseable response: " + ex.Message); }
    }

    /// <summary>"Where's the nearest X": stations with the requested services nearest a reference system. Never throws.</summary>
    public async Task<SpanshServiceStationsResult> SearchStationsByServiceAsync(SpanshServiceQuery query, CancellationToken ct = default)
    {
        var filters = new Dictionary<string, object>
        {
            ["services"] = query.Services.Select(s => new { name = s }).ToArray(),
        };
        if (query.RequireLargePad) filters["has_large_pad"] = new { value = true };

        var (body, error) = await PostSearchAsync("stations/search", filters, query.ReferenceSystem, query.MaxResults, ct).ConfigureAwait(false);
        if (body is null) return SpanshServiceStationsResult.Failure(error!);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var list = new List<SpanshServiceStation>();
            foreach (var r in Results(doc.RootElement))
            {
                var services = new List<string>();
                if (r.TryGetProperty("services", out var sv) && sv.ValueKind == JsonValueKind.Array)
                    foreach (var s in sv.EnumerateArray())
                    {
                        var n = s.ValueKind == JsonValueKind.String ? s.GetString() : s.ValueKind == JsonValueKind.Object ? ReadString(s, "name") : null;
                        if (!string.IsNullOrEmpty(n)) services.Add(n);
                    }
                list.Add(new SpanshServiceStation(
                    SystemName: ReadString(r, "system_name") ?? "Unknown",
                    StationName: ReadString(r, "name") ?? "Unknown",
                    StationType: ReadString(r, "type") ?? "",
                    DistanceLy: ReadDouble(r, "distance"),
                    DistanceToArrivalLs: ReadDouble(r, "distance_to_arrival"),
                    HasLargePad: ReadBool(r, "has_large_pad") || ReadInt(r, "large_pads") > 0,
                    Services: services));
            }
            return SpanshServiceStationsResult.Ok(list);
        }
        catch (JsonException ex) { return SpanshServiceStationsResult.Failure("unparseable response: " + ex.Message); }
    }

    private async Task<(string? Body, string? Error)> PostSearchAsync(
        string path, Dictionary<string, object> filters, string referenceSystem, int size, CancellationToken ct)
    {
        try
        {
            var request = new
            {
                filters,
                sort = new[] { new Dictionary<string, object> { ["distance"] = new { direction = "asc" } } },
                size = Math.Clamp(size, 1, 100),
                page = 0,
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

    private static List<JsonElement> Results(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList()
            : new List<JsonElement>();

    private Task<SpanshSurveyRouteResult> RunSurveyAsync(string path, SpanshSurveyRouteQuery query, bool includeMapping, CancellationToken ct)
    {
        static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        FormUrlEncodedContent BuildForm()
        {
            var form = new Dictionary<string, string>
            {
                ["from"] = query.From,
                ["to"] = query.To ?? "",
                ["range"] = Num(query.RangeLy),
                ["radius"] = I(query.RadiusLy),
                ["max_results"] = I(query.MaxSystems),
                ["max_distance"] = I(query.MaxDistanceLs),
                ["min_value"] = I(query.MinValue),
                ["loop"] = query.Loop ? "1" : "0",
                ["avoid_thargoids"] = query.AvoidThargoids ? "1" : "0",
            };
            if (includeMapping) form["use_mapping_value"] = query.UseMappingValue ? "1" : "0";
            return new FormUrlEncodedContent(form);
        }
        return RunJobAsync(
            c => _http.PostAsync($"{_options.BaseUrl.TrimEnd('/')}/{path}", BuildForm(), c),
            ParseSurveyPoll, SpanshSurveyRouteResult.Failure, ct);
    }

    /// <summary>Parse a Road to Riches / exobiology poll: <c>result</c> is an array of systems, each with <c>bodies</c>.</summary>
    private static (bool Done, SpanshSurveyRouteResult Result) ParseSurveyPoll(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        switch (ReadPhase(root, out var error))
        {
            case JobPhase.Running: return (false, SpanshSurveyRouteResult.Failure("still running"));
            case JobPhase.Failed: return (true, SpanshSurveyRouteResult.Failure(error!));
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return (true, SpanshSurveyRouteResult.Ok(Array.Empty<SpanshSurveySystem>()));

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
        return (true, SpanshSurveyRouteResult.Ok(systems));
    }

    /// <summary>
    /// Parse a tourist poll. The stop list may arrive as a bare <c>result</c> array or under
    /// <c>result.system_jumps</c>/<c>result.jumps</c>; each stop's name is <c>system</c> or <c>name</c>.
    /// </summary>
    private static (bool Done, SpanshRouteResult Result) ParseTouristPoll(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        switch (ReadPhase(root, out var error))
        {
            case JobPhase.Running: return (false, SpanshRouteResult.Failure("still running"));
            case JobPhase.Failed: return (true, SpanshRouteResult.Failure(error!));
        }

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
            return (true, SpanshRouteResult.Ok(Array.Empty<SpanshRouteWaypoint>()));

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
        return (true, SpanshRouteResult.Ok(waypoints));
    }

    private static long ReadLong(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? (v.TryGetInt64(out var n) ? n : (v.TryGetDouble(out var d) ? (long)d : 0))
            : 0;
}
