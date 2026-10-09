using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using EDNexus.Core.Journal;
using EDNexus.Core.Settings;
using EliteDangerous.Eddn;

namespace EDNexus.Core.Reporting;

/// <summary>
/// Connects the journal event bus to the reusable <see cref="EliteDangerous.Eddn"/> client. It warms
/// the EDDN context from every event (including historical replay) so location augmentation is ready,
/// but only uploads <b>live</b> events, and only while the EDDN reporter is enabled in settings.
/// Contiguous runs of <c>FSSSignalDiscovered</c> events are coalesced into one message, as the
/// <c>fsssignaldiscovered</c> schema asks. When a <see cref="IReportingLog"/> is supplied, every upload
/// attempt is recorded for validation.
/// </summary>
public sealed class EddnBridge : IAsyncDisposable
{
    private static readonly TimeSpan DefaultFssBatchWindow = TimeSpan.FromSeconds(5);

    private readonly AppSettings _settings;
    private readonly Func<bool> _isSuppressed;
    private readonly EddnState _state = new();
    private readonly EddnJournalTransformer _transformer;
    private readonly EddnUploader _uploader;
    private readonly IReportingLog? _log;
    private readonly TimeSpan _fssBatchWindow;

    // Serialises bus callbacks with the batch-flush timer; protects _state and the pending signal run.
    private readonly object _gate = new();
    private readonly List<JsonElement> _pendingSignals = new();
    private Timer? _flushTimer;
    private bool _disposed;

    // The (redacted) payload of each queued upload, keyed by the message so an upload result (which
    // carries its message) can be paired back with what was sent even when the uploader drops messages
    // out of order. Each value is null when payload logging is off. Only used when a log is attached.
    private readonly ConcurrentDictionary<EddnMessage, string?> _outbound = new();

    public EddnBridge(JournalEventBus bus, AppSettings settings, EddnUploader uploader,
        EddnJournalTransformer transformer, Func<bool>? isSuppressed = null, IReportingLog? log = null)
        : this(bus, settings, uploader, transformer, DefaultFssBatchWindow, isSuppressed, log) { }

    /// <summary>Test-only constructor allowing the FSS signal batch window to be shortened.</summary>
    internal EddnBridge(JournalEventBus bus, AppSettings settings, EddnUploader uploader,
        EddnJournalTransformer transformer, TimeSpan fssBatchWindow, Func<bool>? isSuppressed = null, IReportingLog? log = null)
    {
        _settings = settings;
        _uploader = uploader;
        _transformer = transformer;
        _fssBatchWindow = fssBatchWindow;
        _isSuppressed = isSuppressed ?? (static () => false);
        _log = log;

        if (_log is not null) _uploader.Completed += OnUploadCompleted;
        bus.SubscribeAny(OnEvent);
    }

    private void OnEvent(JournalEntry e)
    {
        // While developer mode is fabricating events onto the bus, stay completely out of the way:
        // don't upload and don't even warm the rolling context, so synthetic data can't leak to EDDN
        // or corrupt the location fix used to augment real events later.
        if (_isSuppressed()) return;

        lock (_gate)
        {
            if (_disposed) return;

            // Warm the rolling context from all events — even historical — so a live event that arrives
            // before the next FSDJump/LoadGame can still be augmented and identified.
            _state.Observe(e.Raw);

            var isSignal = e.Event == "FSSSignalDiscovered";

            // Any other event ends a run of signals. Flush after Observe so a just-arrived Location /
            // FSDJump / CarrierJump supplies the location (Odyssey writes the signals before it).
            if (!isSignal) FlushSignals();

            if (e.IsHistorical) return;                       // never re-upload replayed history
            if (!_settings.Reporting.Eddn.Enabled) return;    // opt-in, read live so toggles take effect

            if (isSignal)
            {
                _pendingSignals.Add(e.Raw);
                _flushTimer ??= new Timer(_ => { lock (_gate) FlushSignals(); }, null, _fssBatchWindow, Timeout.InfiniteTimeSpan);
                return;
            }

            var message = _transformer.Transform(e.Raw, _state);
            if (message is not null) Send(message);
        }
    }

    /// <summary>Sends the buffered <c>FSSSignalDiscovered</c> run as one message. Caller holds <see cref="_gate"/>.</summary>
    private void FlushSignals()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        if (_pendingSignals.Count == 0) return;

        var run = _pendingSignals.ToArray();
        _pendingSignals.Clear();

        if (_isSuppressed() || !_settings.Reporting.Eddn.Enabled) return;
        var message = _transformer.TransformFssSignals(run, _state);
        if (message is not null) Send(message);
    }

    private void Send(EddnMessage message)
    {
        // Capture the payload (redacted) before handing the message to the uploader, so it can be
        // paired with the result when it completes.
        if (_log is not null)
            _outbound[message] = _settings.Reporting.LogPayloads ? RedactPayload(message) : null;
        _uploader.Enqueue(message);
    }

    private void OnUploadCompleted(EddnUploadResult result)
    {
        if (_log is null) return;
        string? payload = null;
        if (result.Message is not null) _outbound.TryRemove(result.Message, out payload);

        var status = result.Status is { } code ? $"{(int)code} {code}" : "no response";
        _log.Record(new ReportingUpload(
            DateTimeOffset.UtcNow, ReportingTarget.Eddn, result.SchemaRef, result.Success, status,
            Error: result.Success ? null : result.Error,
            Payload: payload));
    }

    /// <summary>
    /// The EDDN envelope's <c>header.uploaderID</c> is the raw commander name (the relay obfuscates it
    /// on receipt, but the bytes we send do not). Strip it before writing the payload to a local log
    /// so a commander's name never lands on disk. Returns null if the envelope can't be reparsed.
    /// </summary>
    private static string? RedactPayload(EddnMessage message)
    {
        try
        {
            var node = JsonNode.Parse(message.ToString());
            if (node?["header"] is JsonObject header && header.ContainsKey("uploaderID"))
                header["uploaderID"] = "[redacted]";
            return node?.ToJsonString();
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            FlushSignals();      // don't lose a trailing run of signals at shutdown
            _disposed = true;
        }
        return _uploader.DisposeAsync();
    }
}
