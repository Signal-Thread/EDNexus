using EDNexus.Core.Journal;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting.Bridge;

/// <summary>
/// One plugin's <see cref="IPluginEvents"/>. Hooks the engine bus through
/// <see cref="JournalEventBus.SubscribeCompleted"/> (so the event is already folded into state) and
/// does nothing on the journal thread but check whether any handler wants the event, wrap it and
/// enqueue it. A dedicated background thread per plugin drains the queue and runs the plugin's
/// handlers one at a time, in order, each inside its own try/catch.
/// </summary>
/// <remarks>
/// <para>
/// Why a thread per plugin rather than the pool: plugin handlers are third-party code and may block.
/// A blocked pool thread starves the rest of the app; a blocked plugin thread only stalls that
/// plugin. The thread is started with <see cref="Thread.UnsafeStart()"/> so the subscribing
/// plugin's <see cref="ExecutionContext"/> (and any <c>AsyncLocal</c> values it holds) does not
/// flow into — and stay pinned by — a host thread. The thread and the bus hook are only created on
/// the first subscription, so a plugin that never subscribes costs nothing.
/// </para>
/// <para>
/// Only events some handler matches are queued, so the queue — bounded both by count
/// (<see cref="PluginBridgeOptions.QueueCapacity"/>) and by approximate memory
/// (<see cref="PluginBridgeOptions.QueueByteCapacity"/>), dropping the oldest when either is
/// exceeded — and <see cref="DroppedEventCount"/> reflect events the plugin actually asked for.
/// The byte bound is what stops a plugin that blocks in a handler, with a catch-all subscription,
/// from retaining thousands of large (shipyard, outfitting) payloads.
/// </para>
/// <para>
/// <b>Developer-mode stamping.</b> An event is stamped <see cref="IJournalEvent.IsSimulated"/> from
/// the developer-mode predicate sampled as the event reaches the completed stage, not from a
/// property of the event itself (the engine's entries carry no provenance). So an event a
/// developer-mode source publishes while another thread is turning developer mode off can be
/// stamped live. The app must therefore stop (dispose) its developer-mode sources before it flips
/// the predicate, which is the order its host rebuild already uses.
/// </para>
/// </remarks>
internal sealed class PluginEvents : IPluginEvents, IDisposable
{
    private readonly JournalEventBus _bus;
    private readonly string _pluginId;
    private readonly bool _withholdSimulated;
    private readonly Func<bool> _isSimulated;
    private readonly Action<PluginHandlerError> _onError;
    private readonly int _capacity;
    private readonly long _byteCapacity;

    private readonly object _gate = new();
    private readonly List<Registration> _registrations = [];
    private readonly Dictionary<string, int> _namedCounts = new(StringComparer.Ordinal);
    private int _anyCount;
    private readonly Queue<(IJournalEvent Event, long Bytes)> _queue = new();
    private long _queuedBytes;
    private IDisposable? _busHook;
    private Thread? _worker;
    private volatile bool _disposed;
    private long _dropped;
    private long _handlerErrors;

    public PluginEvents(
        JournalEventBus bus,
        string pluginId,
        bool withholdSimulated,
        Func<bool> isSimulated,
        Action<PluginHandlerError> onError,
        int capacity,
        long byteCapacity)
    {
        _bus = bus;
        _pluginId = pluginId;
        _withholdSimulated = withholdSimulated;
        _isSimulated = isSimulated;
        _onError = onError;
        _capacity = capacity;
        _byteCapacity = byteCapacity;
    }

    /// <summary>Wanted events dropped because this plugin's queue was full (by count or by bytes).</summary>
    public long DroppedEventCount => Interlocked.Read(ref _dropped);

    /// <summary>Handler invocations that threw.</summary>
    public long HandlerErrorCount => Interlocked.Read(ref _handlerErrors);

    /// <summary>Events queued but not yet handed to the plugin.</summary>
    public int PendingCount { get { lock (_gate) return _queue.Count; } }

    /// <summary>Approximate memory held by the events queued but not yet handed to the plugin.</summary>
    public long PendingBytes { get { lock (_gate) return _queuedBytes; } }

    public void Subscribe(string eventName, Action<IJournalEvent> handler) => On(eventName, handler);

    public void SubscribeAny(Action<IJournalEvent> handler) => OnAny(handler);

    public IDisposable On(string eventName, Action<IJournalEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        return Add(new Registration(this, eventName, handler ?? throw new ArgumentNullException(nameof(handler))));
    }

    public IDisposable OnAny(Action<IJournalEvent> handler)
        => Add(new Registration(this, null, handler ?? throw new ArgumentNullException(nameof(handler))));

    private Registration Add(Registration registration)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _registrations.Add(registration);
            if (registration.EventName is { } name)
                _namedCounts[name] = _namedCounts.GetValueOrDefault(name) + 1;
            else
                _anyCount++;

            if (_worker is null)
            {
                _worker = new Thread(Pump)
                {
                    IsBackground = true,
                    Name = $"EDNexus plugin: {_pluginId}",
                };
                _worker.UnsafeStart();
                _busHook = _bus.SubscribeCompleted(OnPublished);
            }
        }
        return registration;
    }

    private void Remove(Registration registration)
    {
        lock (_gate)
        {
            if (!_registrations.Remove(registration)) return;   // already cleared by Dispose
            if (registration.EventName is { } name)
            {
                var left = _namedCounts[name] - 1;
                if (left == 0) _namedCounts.Remove(name);
                else _namedCounts[name] = left;
            }
            else
            {
                _anyCount--;
            }
        }
    }

    /// <summary>Runs on the journal thread: must stay cheap and must never call plugin code.</summary>
    private void OnPublished(JournalEntry entry)
    {
        lock (_gate)
        {
            if (_disposed || (_anyCount == 0 && !_namedCounts.ContainsKey(entry.Event))) return;
        }

        var simulated = _isSimulated();
        if (simulated && _withholdSimulated) return;

        var journalEvent = new JournalEventAdapter(entry, simulated);
        lock (_gate)
        {
            if (_disposed) return;
            var bytes = journalEvent.EstimatedBytes;
            if (bytes > _byteCapacity)
            {
                // Could never fit, even in an empty queue: dropping the backlog for it would only lose more.
                Interlocked.Increment(ref _dropped);
                return;
            }
            while (_queue.Count > 0 && (_queue.Count >= _capacity || _queuedBytes + bytes > _byteCapacity))
            {
                _queuedBytes -= _queue.Dequeue().Bytes;
                Interlocked.Increment(ref _dropped);
            }
            _queue.Enqueue((journalEvent, bytes));
            _queuedBytes += bytes;
            Monitor.Pulse(_gate);
        }
    }

    private void Pump()
    {
        while (true)
        {
            IJournalEvent next;
            Registration[] handlers;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_disposed) Monitor.Wait(_gate);
                if (_disposed) return;
                var dequeued = _queue.Dequeue();
                _queuedBytes -= dequeued.Bytes;
                next = dequeued.Event;
                handlers = _registrations.ToArray();
            }

            foreach (var registration in handlers)
            {
                // Re-checked per handler, but not under the lock: after Dispose returns, at most one
                // handler that had already passed this check (and Matches) may still start, and one
                // already running cannot be interrupted. WaitForExit is the real guarantee.
                if (_disposed) return;
                if (!registration.Matches(next)) continue;
                try { registration.Handler(next); }
                catch (Exception ex) { Report(next, ex); }
            }
        }
    }

    private void Report(IJournalEvent journalEvent, Exception exception)
    {
        Interlocked.Increment(ref _handlerErrors);
        try
        {
            // Text only: the exception object belongs to the plugin, and a sink that kept it would
            // pin the plugin's load context.
            var (typeName, message) = PluginHost.Summarise(exception);
            _onError(new PluginHandlerError(_pluginId, journalEvent.Event, typeName, message));
        }
        catch { /* a faulty error sink must not kill the plugin's pump */ }
    }

    /// <summary>
    /// Unhooks from the bus, drops every handler and anything still queued, and lets the worker exit.
    /// After this returns, at most one handler that had already passed the worker's disposed check
    /// may still run; nothing else is delivered. It does not wait for that handler: that would hand
    /// plugin code a way to hang whoever is unloading it. <see cref="WaitForExit"/> is the real
    /// guarantee that no plugin code is still running.
    /// </summary>
    public void Dispose()
    {
        IDisposable? hook;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var registration in _registrations) registration.MarkRemoved();
            _registrations.Clear();
            _namedCounts.Clear();
            _anyCount = 0;
            _queue.Clear();
            _queuedBytes = 0;
            hook = _busHook;
            _busHook = null;
            Monitor.PulseAll(_gate);
        }
        hook?.Dispose();
    }

    /// <summary>
    /// Waits for the worker thread to exit. False means a handler is still running: the plugin's
    /// code is live on the stack, so its <c>AssemblyLoadContext</c> cannot be unloaded.
    /// </summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        Thread? worker;
        lock (_gate) worker = _worker;
        return worker is null || worker.Join(timeout);
    }

    private sealed class Registration(PluginEvents owner, string? eventName, Action<IJournalEvent> handler) : IDisposable
    {
        private volatile bool _removed;

        public string? EventName { get; } = eventName;

        public Action<IJournalEvent> Handler { get; } = handler;

        // Ordinal, as IPluginEvents documents — deliberately stricter than the engine bus.
        public bool Matches(IJournalEvent journalEvent)
            => !_removed && (EventName is null || string.Equals(EventName, journalEvent.Event, StringComparison.Ordinal));

        public void MarkRemoved() => _removed = true;

        public void Dispose()
        {
            if (_removed) return;
            _removed = true;
            owner.Remove(this);
        }
    }
}
