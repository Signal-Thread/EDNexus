using System.Text;
using System.Text.Json;

namespace EliteDangerous.Inara;

/// <summary>
/// Sends batches of <see cref="InaraEvent"/> to the Inara API and parses the reply. This is pure
/// transport: it does not decide which events to send or how often — that policy belongs to the
/// caller. A single instance is safe to reuse across sends and commanders.
/// </summary>
public sealed class InaraClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    private readonly InaraClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public InaraClient(InaraClientOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
    }

    /// <summary>
    /// POSTs <paramref name="events"/> under <paramref name="identity"/> and returns the parsed
    /// result. Never throws for network/HTTP problems — those surface as
    /// <see cref="InaraResponse.TransportError"/> so callers can back off without a try/catch.
    /// </summary>
    public async Task<InaraResponse> SendAsync(InaraIdentity identity, IReadOnlyList<InaraEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return new InaraResponse { Status = 200, StatusText = "no events" };

        var body = BuildBody(identity, events);
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_options.Endpoint, content, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A client error other than "slow down" (408/429) cannot succeed on a retry, so it is
                // reported with its own status rather than as a transport failure that would be requeued.
                var code = (int)response.StatusCode;
                if (code is >= 400 and < 500 and not 408 and not 429)
                    return new InaraResponse { Status = code, StatusText = $"HTTP {code}" };

                var retryAfter = response.Headers.RetryAfter is { } ra ? ra.Delta ?? (ra.Date - DateTimeOffset.UtcNow) : null;
                return InaraResponse.TransportError($"HTTP {(int)response.StatusCode}", retryAfter > TimeSpan.Zero ? retryAfter : null);
            }
            return Parse(text);
        }
        catch (Exception ex)
        {
            return InaraResponse.TransportError(ex.Message);
        }
    }

    private string BuildBody(InaraIdentity identity, IReadOnlyList<InaraEvent> events)
    {
        var header = new Dictionary<string, object?>
        {
            ["appName"] = _options.AppName,
            ["appVersion"] = _options.AppVersion,
            ["isBeingDeveloped"] = _options.IsBeingDeveloped,
            ["APIkey"] = identity.ApiKey,
            ["commanderName"] = identity.CommanderName,
            ["commanderFrontierID"] = identity.CommanderFrontierID,
        };

        var payloadEvents = events.Select(e => new Dictionary<string, object?>
        {
            ["eventName"] = e.EventName,
            ["eventTimestamp"] = e.EventTimestamp.UtcDateTime.ToString(TimestampFormat),
            ["eventData"] = e.EventData,
        });

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["header"] = header,
            ["events"] = payloadEvents,
        }, Json);
    }

    private static InaraResponse Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return InaraResponse.TransportError("unparseable response: not a JSON object");

            int status = 0;
            string? statusText = null;
            if (root.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Object)
            {
                if (header.TryGetProperty("eventStatus", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sv)) status = sv;
                if (header.TryGetProperty("eventStatusText", out var st) && st.ValueKind == JsonValueKind.String)
                    statusText = st.GetString();
            }

            var events = new List<InaraEventStatus>();
            if (root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
                foreach (var ev in evs.EnumerateArray())
                {
                    if (ev.ValueKind != JsonValueKind.Object) continue;
                    var es = ev.TryGetProperty("eventStatus", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var ev2) ? ev2 : 0;
                    var et = ev.TryGetProperty("eventStatusText", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    events.Add(new InaraEventStatus(es, et));
                }

            return new InaraResponse { Status = status, StatusText = statusText, Events = events };
        }
        catch (JsonException ex)
        {
            return InaraResponse.TransportError("unparseable response: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
