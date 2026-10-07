using System.Runtime.InteropServices;
using System.Text.Json;
using EDNexus.Core.Journal;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting.Bridge;

/// <summary>
/// The <see cref="IJournalEvent"/> a plugin receives: a read-only view over a host
/// <see cref="JournalEntry"/>. The entry's payload is a detached, immutable <see cref="JsonElement"/>,
/// and the entry itself is never exposed, so a plugin can read fields but not reach engine types.
/// </summary>
internal sealed class JournalEventAdapter(JournalEntry entry, bool isSimulated) : IJournalEvent
{
    private readonly JournalEntry _entry = entry;

    public string Event => _entry.Event;

    public DateTimeOffset Timestamp => _entry.Timestamp;

    public bool IsHistorical => _entry.IsHistorical;

    public bool IsSimulated { get; } = isSimulated;

    /// <summary>
    /// Roughly how much memory the queued event retains: the payload's UTF-8 length (the detached
    /// element owns a copy of the line) plus a fixed allowance for the wrapper objects. Cheap: no
    /// allocation, so it can be taken on the journal thread.
    /// </summary>
    public long EstimatedBytes { get; } = EstimateBytes(entry);

    private static long EstimateBytes(JournalEntry entry)
    {
        const int Overhead = 256;
        try { return Overhead + JsonMarshal.GetRawUtf8Value(entry.Raw).Length; }
        catch (InvalidOperationException) { return Overhead; }   // default (undefined) element
    }

    public string? GetString(string field) => field is null ? null : _entry.GetString(field);

    public long? GetInt64(string field) => field is null ? null : _entry.GetInt64(field);

    public double? GetDouble(string field) => field is null ? null : _entry.GetDouble(field);

    public bool? GetBool(string field) => field is null ? null : _entry.GetBool(field);

    public string? GetLocalised(string field) => field is null ? null : _entry.GetLocalised(field);

    /// <summary>
    /// The entry's detached payload. There is deliberately no <c>Deserialize&lt;T&gt;</c>: the
    /// serializer would cache the plugin's type process-wide and pin its load context (see
    /// <see cref="IJournalEvent.Payload"/>).
    /// </summary>
    public JsonElement Payload => _entry.Raw;

    public override string ToString() => _entry.Event;
}
