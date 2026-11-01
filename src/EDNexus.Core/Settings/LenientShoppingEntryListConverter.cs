using System.Text.Json;
using System.Text.Json.Serialization;
using EDNexus.Core.ShoppingList;

namespace EDNexus.Core.Settings;

/// <summary>
/// Reads the manual shopping lines one by one and drops any that cannot be parsed (an unrecognised
/// kind or source from a newer or hand-edited file), so one bad line never fails — and gets
/// quarantined — the whole settings file.
/// </summary>
public sealed class LenientShoppingEntryListConverter : JsonConverter<List<ShoppingEntry>>
{
    public override List<ShoppingEntry>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<ShoppingEntry>();
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            try
            {
                var entry = item.Deserialize<ShoppingEntry>(options);
                if (entry is not null && entry.Key is not null && entry.Name is not null) result.Add(entry);
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<ShoppingEntry> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var e in value) JsonSerializer.Serialize(writer, e, options);
        writer.WriteEndArray();
    }
}
