using EDNexus.Plugins.Abstractions;

namespace HelloCommander;

/// <summary>
/// "Hello Commander": counts hyperspace jumps and logs a greeting for each one. It is deliberately
/// tiny and doubles as the copy-paste starting point for a plugin; see docs/PLUGINS.md.
/// </summary>
/// <remarks>
/// The class needs a public parameterless constructor (the host constructs it), and must not touch
/// the <see cref="IPluginContext"/> until <see cref="Initialize"/> hands it over.
/// </remarks>
public sealed class HelloCommanderPlugin : IEDNexusPlugin
{
    private const string LifetimeKey = "lifetime-jumps";

    private IPluginContext? _context;
    private IDisposable? _subscription;
    private long _sessionJumps;
    private long _lifetimeJumps;
    private volatile bool _storageUnavailable;

    /// <inheritdoc />
    public void Initialize(IPluginContext context)
    {
        _context = context;
        _lifetimeJumps = LoadLifetimeJumps(context);

        // Handlers run on this plugin's own background thread, one event at a time, in journal
        // order. On() (unlike Subscribe()) returns a handle so we can unsubscribe in Shutdown.
        _subscription = context.Events.On("FSDJump", OnJump);

        context.Log.Info($"Hello Commander loaded (lifetime total so far: {_lifetimeJumps}).");
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        // A handler may still be running while this executes, which is why the counters are only
        // touched through Interlocked. Do not use the context after this method returns.
        _subscription?.Dispose();
        _subscription = null;
        _context?.Log.Info($"Hello Commander shutting down after {Interlocked.Read(ref _sessionJumps)} jump(s).");
    }

    private void OnJump(IJournalEvent e)
    {
        var context = _context;
        if (context is null) return;

        var jumps = Interlocked.Increment(ref _sessionJumps);

        // Replayed (historical) events count towards the session, but must not inflate the saved
        // lifetime total, or every restart would count the same jumps again.
        var lifetime = e.IsHistorical
            ? Interlocked.Read(ref _lifetimeJumps)
            : SaveLifetimeJumps(context, Interlocked.Increment(ref _lifetimeJumps));

        // The state is a read-only snapshot as of the last completed event. It can be AHEAD of the
        // event being handled (the plugin's queue may lag), so prefer the event's own StarSystem and
        // fall back to the state. Name is null until the game has said who the commander is.
        var state = context.State.Snapshot();
        var cmdr = state.Name ?? "Unknown";
        var system = e.GetString("StarSystem") ?? state.StarSystem ?? "an unknown system";

        context.Log.Info($"CMDR {cmdr} - {jumps} jumps, now in {system} (lifetime {lifetime})");
    }

    /// <summary>Reads the saved lifetime total; 0 if unset or if storage is not available.</summary>
    private long LoadLifetimeJumps(IPluginContext context)
    {
        try
        {
            return long.TryParse(context.Storage.GetString(LifetimeKey), out var saved) ? saved : 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not granted (or the host has no storage yet): carry on without persistence.
            NoteStorageUnavailable(context, ex);
            return 0;
        }
    }

    private long SaveLifetimeJumps(IPluginContext context, long total)
    {
        if (_storageUnavailable) return total;
        try
        {
            context.Storage.SetString(LifetimeKey, total.ToString());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            NoteStorageUnavailable(context, ex);
        }
        return total;
    }

    private void NoteStorageUnavailable(IPluginContext context, Exception ex)
    {
        // Log once, then stop trying. A handler that throws on every event would get the plugin
        // quarantined, and retrying a refused call on every jump is just noise.
        _storageUnavailable = true;
        context.Log.Warn($"Storage unavailable, the lifetime total will not be saved: {ex.Message}");
    }
}
