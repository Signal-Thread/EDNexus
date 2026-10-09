using System.Text.Json;

namespace EDNexus.Plugins.Abstractions;

/// <summary>
/// A read-only view of a single journal (or status) event, handed to plugins in place of the
/// host's internal <c>JournalEntry</c>. Exposes the same defensive scalar accessors so plugins
/// never need to know the underlying JSON representation, and never get a mutable handle to it.
/// </summary>
public interface IJournalEvent
{
    /// <summary>The journal <c>event</c> field, e.g. <c>"FSDJump"</c> or <c>"MarketSell"</c>.</summary>
    string Event { get; }

    /// <summary>The event's <c>timestamp</c> field, or <see cref="DateTimeOffset.MinValue"/> if absent/unparsable.</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>
    /// True when this event was replayed from an existing journal file at startup rather than
    /// observed live. Plugins should typically suppress alerts/notifications for historical events.
    /// </summary>
    bool IsHistorical { get; }

    /// <summary>
    /// True when the event was fabricated by the host's developer mode rather than read from the
    /// game. Plugins must never forward simulated events off the machine; the host already withholds
    /// them from plugins granted the <c>network</c> capability.
    /// </summary>
    bool IsSimulated { get => false; }

    /// <summary>Reads a string field, or <see langword="null"/> if it is missing or not a string.</summary>
    string? GetString(string field);

    /// <summary>Reads an integer field, or <see langword="null"/> if it is missing or not a number.</summary>
    long? GetInt64(string field);

    /// <summary>Reads a floating-point field, or <see langword="null"/> if it is missing or not a number.</summary>
    double? GetDouble(string field);

    /// <summary>Reads a boolean field, or <see langword="null"/> if it is missing or not a boolean.</summary>
    bool? GetBool(string field);

    /// <summary>
    /// Reads the user-facing "<paramref name="field"/>_Localised" variant of a field, falling back
    /// to the raw field when no localised form exists. Prefer this over <see cref="GetString"/> for
    /// anything shown to the user.
    /// </summary>
    string? GetLocalised(string field);

    /// <summary>
    /// The event's complete JSON payload, for fields the scalar accessors cannot reach (arrays,
    /// nested objects). It is an immutable, detached <see cref="JsonElement"/>: valid for as long
    /// as the plugin holds it, and it gives no handle back to the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read it with <see cref="JsonElement"/>'s own navigation (<c>TryGetProperty</c>,
    /// <c>EnumerateArray</c>, <c>GetInt64</c> and so on): journal event shapes change between game
    /// updates, so reading only the fields you need is more resilient than binding the whole payload
    /// to a type.
    /// </para>
    /// <para>
    /// <b>Do not bind it with reflection-based <c>System.Text.Json</c></b>
    /// (<c>Payload.Deserialize&lt;MyType&gt;()</c>, <c>JsonSerializer.Deserialize&lt;MyType&gt;(...)</c>
    /// on a plugin-defined type). The serializer keeps a process-wide, strong cache entry for every
    /// type it binds, which pins the plugin's <see cref="System.Runtime.Loader.AssemblyLoadContext"/>:
    /// the plugin's assemblies then stay loaded and locked on disk after it is unloaded, so it cannot
    /// be updated or reinstalled without restarting EDNexus. This SDK therefore has no
    /// <c>Deserialize&lt;T&gt;</c> of its own. A source-generated <c>JsonSerializerContext</c>
    /// defined in the plugin does not use that cache.
    /// </para>
    /// </remarks>
    JsonElement Payload { get; }
}
