using System.Text.Json;

namespace EDNexus.Plugins.Abstractions;

/// <summary>JSON convenience helpers over <see cref="IPluginStorage"/>.</summary>
public static class PluginStorageExtensions
{
    /// <summary>
    /// Reads and deserializes the value saved under <paramref name="key"/>. Returns
    /// <see langword="default"/> if it is unset or is not valid JSON for <typeparamref name="T"/>.
    /// </summary>
    public static T? ReadJson<T>(this IPluginStorage storage, string key)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var text = storage.GetString(key);
        if (string.IsNullOrWhiteSpace(text)) return default;
        try { return JsonSerializer.Deserialize<T>(text); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return default; }
    }

    /// <summary>Serializes <paramref name="value"/> as JSON and saves it under <paramref name="key"/>.</summary>
    public static void WriteJson<T>(this IPluginStorage storage, string key, T value)
    {
        ArgumentNullException.ThrowIfNull(storage);
        storage.SetString(key, JsonSerializer.Serialize(value));
    }
}
