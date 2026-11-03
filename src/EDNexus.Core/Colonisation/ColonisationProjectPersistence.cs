using EDNexus.Core.Journal;
using EDNexus.Core.State;

namespace EDNexus.Core.Colonisation;

/// <summary>
/// Keeps a <see cref="ColonisationTracker"/>'s projects across sessions: once the commander is known
/// (<c>Commander</c> / <c>LoadGame</c>) their saved projects are restored into the tracker, and later
/// changes are written back, coalesced so a burst of deliveries is one write. It only reads
/// <see cref="CommanderState"/> (for the name) and never mutates it.
/// </summary>
public sealed class ColonisationProjectPersistence : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly CommanderState _state;
    private readonly ColonisationTracker _tracker;
    private readonly ColonisationProjectStore _store;
    private readonly Func<bool>? _isSuppressed;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private string? _commander;
    private bool _dirty;
    private bool _disposed;

    /// <param name="isSuppressed">
    /// While true nothing is loaded or saved. The app wires this to developer mode so fabricated
    /// projects never reach a commander's saved file.
    /// </param>
    public ColonisationProjectPersistence(
        JournalEventBus bus, CommanderState state, ColonisationTracker tracker,
        ColonisationProjectStore store, Func<bool>? isSuppressed = null)
    {
        _state = state;
        _tracker = tracker;
        _store = store;
        _isSuppressed = isSuppressed;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        // Subscribed after StateTracker, so by now it has adopted the name from the same event.
        bus.Subscribe("Commander", _ => OnCommander());
        bus.Subscribe("LoadGame", _ => OnCommander());
        tracker.Changed += OnChanged;
    }

    private bool Suppressed => _isSuppressed?.Invoke() == true;

    private void OnCommander()
    {
        if (Suppressed || _state.Name is not { Length: > 0 } name) return;

        bool switchedCommander;
        lock (_gate)
        {
            if (string.Equals(_commander, name, StringComparison.OrdinalIgnoreCase)) return;
            // Saving the old commander's projects under the new name would mix them up: write them first.
            if (_dirty) FlushLocked();
            switchedCommander = _commander is not null;
            _commander = name;
        }

        // The previous commander's projects must not be merged into (and later saved under) the new one.
        if (switchedCommander) _tracker.Clear();

        // Restore outside the lock: it raises Changed, which re-enters OnChanged.
        _tracker.Restore(_store.Load(name));
    }

    private void OnChanged()
    {
        lock (_gate)
        {
            if (_disposed || _commander is null) return;
            _dirty = true;
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes any pending change now.</summary>
    public void Flush()
    {
        lock (_gate) FlushLocked();
    }

    private void FlushLocked()
    {
        if (!_dirty || _commander is null) return;
        if (Suppressed) { _dirty = false; return; }
        if (_store.TrySave(_commander, _tracker.Snapshot())) _dirty = false;
        else if (!_disposed) _timer.Change(SaveDelay * 5, Timeout.InfiniteTimeSpan);   // try again rather than lose the change
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _tracker.Changed -= OnChanged;
            _timer.Dispose();
            FlushLocked();
        }
    }
}
