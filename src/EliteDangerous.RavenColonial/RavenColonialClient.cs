using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EliteDangerous.RavenColonial;

/// <summary>
/// Queries the Raven Colonial read APIs (a project by id, a project by construction depot, and the
/// projects in a system) and parses the replies into plain records, and can report a commander's
/// delivery to a project (<see cref="ContributeAsync"/>). This is pure transport: matching a project
/// to what the commander is docked at, and deciding what to show or send, belongs to the caller.
/// Following the EDSM, Spansh and Galnet clients' convention it never throws for network/HTTP
/// problems; failures surface as <see cref="RavenResult{T}.Failure"/>, and an unknown project as an
/// OK result with a null value. A single instance is safe to reuse across queries.
/// </summary>
/// <remarks>
/// The API declares no authentication — every read here is public, which is why this client carries
/// no credentials. Its numeric fields are declared as integer-or-string in the published schema, so
/// every number is read leniently rather than assuming a JSON number.
/// <para>
/// The one write, <see cref="ContributeAsync"/>, is unauthenticated too: the commander is only a path
/// segment, so anyone can post a delivery in anyone's name. That is Raven Colonial's design, not
/// something this client can mitigate, which is why callers must only send a commander's own
/// deliveries, and only when the commander has opted in.
/// </para>
/// </remarks>
public sealed class RavenColonialClient : IDisposable
{
    private readonly RavenColonialClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public RavenColonialClient(RavenColonialClientOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(Sanitize(_options.SoftwareName), Sanitize(_options.SoftwareVersion)));
    }

    /// <summary>
    /// The shared state of one project. An OK result with a null value means Raven Colonial has no
    /// project with that id.
    /// </summary>
    public async Task<RavenResult<RavenProject>> GetProjectAsync(string buildId, CancellationToken ct = default)
    {
        if (!IsSafeSegment(buildId)) return RavenResult<RavenProject>.Ok(null);

        return await GetAsync($"{Base}/api/project/{Uri.EscapeDataString(buildId.Trim())}", ct, body =>
        {
            using var doc = JsonDocument.Parse(body);
            return RavenResult<RavenProject>.Ok(ReadProject(doc.RootElement));
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The project registered against a construction depot — the match a docked commander needs. An
    /// OK result with a null value means the depot is not tracked on Raven Colonial.
    /// </summary>
    public async Task<RavenResult<RavenProject>> GetProjectForDepotAsync(
        long systemAddress, long marketId, CancellationToken ct = default)
    {
        return await GetAsync($"{Base}/api/System/{systemAddress}/{marketId}", ct, body =>
        {
            using var doc = JsonDocument.Parse(body);
            return RavenResult<RavenProject>.Ok(ReadProject(doc.RootElement));
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Every project Raven Colonial knows in a system, by name or id64. An OK result with an empty
    /// list means the system has none.
    /// </summary>
    public async Task<RavenResult<IReadOnlyList<RavenProjectRef>>> GetSystemProjectsAsync(
        string systemNameOrId64, CancellationToken ct = default)
    {
        if (!IsSafeSegment(systemNameOrId64))
            return RavenResult<IReadOnlyList<RavenProjectRef>>.Ok(Array.Empty<RavenProjectRef>());

        var url = $"{Base}/api/System/{Uri.EscapeDataString(systemNameOrId64.Trim())}";
        return await GetAsync(url, ct, body =>
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return RavenResult<IReadOnlyList<RavenProjectRef>>.Ok(Array.Empty<RavenProjectRef>());

            var projects = new List<RavenProjectRef>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (ReadString(el, "buildId") is not { Length: > 0 } id) continue;
                projects.Add(new RavenProjectRef(
                    BuildId: id,
                    BuildName: ReadString(el, "buildName") ?? "",
                    BuildType: ReadString(el, "buildType") ?? "",
                    SystemName: ReadString(el, "systemName") ?? "",
                    SystemAddress: ReadLong(el, "systemAddress"),
                    MarketId: ReadLong(el, "marketId"),
                    Complete: ReadBool(el, "complete"),
                    Architect: ReadString(el, "architectName")));
            }

            return RavenResult<IReadOnlyList<RavenProjectRef>>.Ok(projects);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports a commander's own delivery to a project: <c>POST /api/project/{buildId}/contribute/{cmdr}</c>
    /// with a JSON object of commodity name to units delivered. Never throws; a rejected, failed or
    /// unreachable call is a <see cref="RavenResult{T}.Failure"/>.
    /// </summary>
    /// <param name="buildId">The project's <see cref="RavenProject.BuildId"/>.</param>
    /// <param name="cmdr">The commander the delivery is credited to. The endpoint takes it on trust.</param>
    /// <param name="deltas">
    /// Commodity name to units delivered. Names must be spelled the way the project's own
    /// <see cref="RavenProject.Remaining"/> keys are; this client does not guess. Blank names and
    /// amounts of zero or less are dropped, and when nothing is left no request is made.
    /// </param>
    /// <returns>
    /// OK with a receipt of what was actually sent, or a failure. A blank or dot-segment
    /// <paramref name="buildId"/> or <paramref name="cmdr"/>, and an empty <paramref name="deltas"/>,
    /// fail without touching the network.
    /// </returns>
    public async Task<RavenResult<RavenContribution>> ContributeAsync(
        string buildId, string cmdr, IReadOnlyDictionary<string, int> deltas, CancellationToken ct = default)
    {
        if (!IsSafeSegment(buildId)) return RavenResult<RavenContribution>.Failure("invalid build id");
        if (!IsSafeSegment(cmdr)) return RavenResult<RavenContribution>.Failure("invalid commander name");

        var send = new Dictionary<string, int>(StringComparer.Ordinal);
        if (deltas is not null)
            foreach (var (name, units) in deltas)
            {
                if (string.IsNullOrWhiteSpace(name) || units <= 0) continue;
                var key = name.Trim();
                send[key] = send.TryGetValue(key, out var already) ? already + units : units;
            }
        if (send.Count == 0) return RavenResult<RavenContribution>.Failure("nothing to contribute");

        var url = $"{Base}/api/project/{Uri.EscapeDataString(buildId.Trim())}/contribute/{Uri.EscapeDataString(cmdr.Trim())}";
        var json = JsonSerializer.Serialize(send);

        try
        {
            // A fresh request per attempt: a request message (and its content) can only be sent once.
            using var response = await SendWithRetryAsync(
                token => _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), token), ct)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? RavenResult<RavenContribution>.Ok(new RavenContribution(buildId.Trim(), send))
                : RavenResult<RavenContribution>.Failure($"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return RavenResult<RavenContribution>.Failure(ex.Message); }   // incl. an HttpClient timeout
    }

    private string Base => _options.BaseUrl.TrimEnd('/');

    /// <summary>Shared GET + parse plumbing: never throws, mapping every failure onto a Failure result.</summary>
    private async Task<RavenResult<T>> GetAsync<T>(string url, CancellationToken ct, Func<string, RavenResult<T>> parse)
        where T : class
    {
        try
        {
            using var response = await SendWithRetryAsync(token => _http.GetAsync(url, token), ct).ConfigureAwait(false);

            // "No project here" is an ordinary answer for a depot nobody is tracking, not a fault.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
                return RavenResult<T>.Ok(null);

            if (!response.IsSuccessStatusCode)
                return RavenResult<T>.Failure($"HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? RavenResult<T>.Ok(null) : parse(body);
        }
        catch (JsonException ex) { return RavenResult<T>.Failure("unparseable response: " + ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return RavenResult<T>.Failure(ex.Message); }   // incl. an HttpClient timeout
    }

    /// <summary>
    /// Sends a request (built afresh by <paramref name="send"/> for each attempt) with a single retry
    /// when the server says it is busy (429/503): waits the server's <c>Retry-After</c> (or
    /// <see cref="RavenColonialClientOptions.TransientRetryDelay"/>) if that is within
    /// <see cref="RavenColonialClientOptions.MaxRetryAfter"/>, and otherwise hands the busy response straight back.
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
    /// A value is only usable as a URL path segment if it is non-blank and not a dot-segment:
    /// <c>Uri.EscapeDataString("..")</c> leaves <c>..</c> untouched, and the HTTP stack would then
    /// collapse it and address a different endpoint.
    /// </summary>
    private static bool IsSafeSegment(string value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed is not ("." or "..");
    }

    private static RavenProject? ReadProject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (ReadString(root, "buildId") is not { Length: > 0 } buildId) return null;

        var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("commodities", out var commodities) && commodities.ValueKind == JsonValueKind.Object)
            foreach (var entry in commodities.EnumerateObject())
                if (AsInt(entry.Value) is { } units)
                    remaining[entry.Name] = units;

        // "commanders" maps a commander to the commodities they have taken on; only the names matter here.
        var contributors = new List<string>();
        if (root.TryGetProperty("commanders", out var commanders) && commanders.ValueKind == JsonValueKind.Object)
            foreach (var entry in commanders.EnumerateObject())
                contributors.Add(entry.Name);

        return new RavenProject(
            BuildId: buildId,
            BuildName: ReadString(root, "buildName") ?? "",
            BuildType: ReadString(root, "buildType") ?? "",
            SystemName: ReadString(root, "systemName") ?? "",
            SystemAddress: ReadLong(root, "systemAddress"),
            MarketId: ReadLong(root, "marketId"),
            Remaining: remaining,
            SumRemaining: ReadInt(root, "sumNeed") ?? remaining.Values.Sum(),
            MaxNeed: ReadInt(root, "maxNeed") ?? 0,
            Complete: ReadBool(root, "complete"),
            Architect: ReadString(root, "architectName"),
            Contributors: contributors);
    }

    private static string? ReadString(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool ReadBool(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? ReadInt(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) ? AsInt(v) : null;

    private static long? ReadLong(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) ? AsLong(v) : null;

    /// <summary>The schema declares its numbers as integer-or-string, so accept either.</summary>
    private static int? AsInt(JsonElement v) => AsLong(v) is { } l && l is >= int.MinValue and <= int.MaxValue
        ? (int)l
        : null;

    private static long? AsLong(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
        _ => null,
    };

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
