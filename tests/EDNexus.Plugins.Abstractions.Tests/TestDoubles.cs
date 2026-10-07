using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Abstractions.Tests;

/// <summary>Minimal in-memory fakes used to exercise the SDK contract without a real host.</summary>
internal sealed class FakeJournalEvent : IJournalEvent
{
    private readonly Dictionary<string, object?> _fields;

    public FakeJournalEvent(string @event, DateTimeOffset timestamp, bool isHistorical, Dictionary<string, object?>? fields = null)
    {
        Event = @event;
        Timestamp = timestamp;
        IsHistorical = isHistorical;
        _fields = fields ?? new Dictionary<string, object?>();
    }

    public string Event { get; }
    public DateTimeOffset Timestamp { get; }
    public bool IsHistorical { get; }

    public string? GetString(string field) => _fields.TryGetValue(field, out var v) ? v as string : null;
    public long? GetInt64(string field) => _fields.TryGetValue(field, out var v) ? v as long? : null;
    public double? GetDouble(string field) => _fields.TryGetValue(field, out var v) ? v as double? : null;
    public bool? GetBool(string field) => _fields.TryGetValue(field, out var v) ? v as bool? : null;
    public string? GetLocalised(string field) => GetString(field + "_Localised") ?? GetString(field);
    public System.Text.Json.JsonElement Payload => default;
}

internal sealed class FakePluginEvents : IPluginEvents
{
    private readonly Dictionary<string, List<Action<IJournalEvent>>> _handlers = new(StringComparer.Ordinal);
    private readonly List<Action<IJournalEvent>> _anyHandlers = [];

    public void Subscribe(string eventName, Action<IJournalEvent> handler)
    {
        if (!_handlers.TryGetValue(eventName, out var list))
            _handlers[eventName] = list = [];
        list.Add(handler);
    }

    public void SubscribeAny(Action<IJournalEvent> handler) => _anyHandlers.Add(handler);

    public void Publish(IJournalEvent journalEvent)
    {
        if (_handlers.TryGetValue(journalEvent.Event, out var list))
            foreach (var handler in list) handler(journalEvent);
        foreach (var handler in _anyHandlers) handler(journalEvent);
    }
}

internal sealed class FakePluginLog : IPluginLog
{
    public List<string> Messages { get; } = [];
    public void Debug(string message) => Messages.Add($"DEBUG: {message}");
    public void Info(string message) => Messages.Add($"INFO: {message}");
    public void Warn(string message) => Messages.Add($"WARN: {message}");
    public void Error(string message, Exception? exception = null) => Messages.Add($"ERROR: {message} {exception}");
}

internal sealed class FakePluginStorage : IPluginStorage
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    public string? GetString(string key) => _values.GetValueOrDefault(key);
    public void SetString(string key, string value) => _values[key] = value;
    public void Remove(string key) => _values.Remove(key);
}

internal sealed class FakeUiRegistry : IUiRegistry
{
    public Dictionary<string, object> Registered { get; } = new(StringComparer.Ordinal);
    public void Register(string id, object descriptor) => Registered[id] = descriptor;
}

internal sealed class FakeCommanderState : IReadOnlyCommanderState
{
    public string? Name { get; init; }
    public long Balance { get; init; }
    public string? Ship { get; init; }
    public string? StarSystem { get; init; }
    public bool Docked { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
}

internal sealed class FakePluginContext : IPluginContext
{
    public IReadOnlyCommanderState State { get; init; } = new FakeCommanderState();
    public FakePluginEvents Events { get; } = new();
    IPluginEvents IPluginContext.Events => Events;
    public IPluginLog Log { get; } = new FakePluginLog();
    public IPluginStorage Storage { get; } = new FakePluginStorage();
    public IUiRegistry Ui { get; } = new FakeUiRegistry();
    public PluginManifest Manifest { get; init; } = new("test.plugin", "Test Plugin", "1.0.0", PluginSdk.CurrentVersionString);
}
