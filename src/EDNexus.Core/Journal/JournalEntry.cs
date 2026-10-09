using System.Text.Json;

namespace EDNexus.Core.Journal;

/// <summary>
/// A single line from a journal or status file, parsed just far enough to know its
/// <see cref="Event"/> name and <see cref="Timestamp"/>. The full payload is retained
/// as raw JSON so subscribers deserialize only the fields they care about — which keeps
/// the engine resilient to Frontier adding or changing event shapes.
/// </summary>
public sealed class JournalEntry
{
    public required string Event { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public JsonElement Raw { get; init; }

    /// <summary>
    /// True when replayed from an existing file at startup rather than observed live.
    /// Lets state warm up silently without firing alerts / voice callouts.
    /// </summary>
    public bool IsHistorical { get; init; }

    public static bool TryParse(string line, bool historical, out JournalEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            // A line can be valid JSON yet not an object ([], 5, null, "x"); TryGetProperty throws on those.
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.String)
                return false;

            // TryGetDateTimeOffset throws (rather than returning false) unless the value is a string.
            var timestamp = root.TryGetProperty("timestamp", out var ts)
                && ts.ValueKind == JsonValueKind.String
                && ts.TryGetDateTimeOffset(out var t)
                    ? t : default;

            entry = new JournalEntry
            {
                Event = ev.GetString() ?? string.Empty,
                Timestamp = timestamp,
                // Clone() detaches the element so it stays valid after the document is disposed.
                Raw = root.Clone(),
                IsHistorical = historical,
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    // --- Convenience accessors for the common case of reading one scalar field. ---

    public string? GetString(string prop)
        => Raw.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public long? GetInt64(string prop)
        => Raw.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : null;

    public double? GetDouble(string prop)
        => Raw.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : null;

    public bool? GetBool(string prop)
        => Raw.TryGetProperty(prop, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;

    /// <summary>Prefer a "_Localised" variant of a field, falling back to the raw name.</summary>
    public string? GetLocalised(string prop)
        => GetString(prop + "_Localised") ?? GetString(prop);

    public T? Deserialize<T>() => Raw.Deserialize<T>(JournalJson.Options);
}
