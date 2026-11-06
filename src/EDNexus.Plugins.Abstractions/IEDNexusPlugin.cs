namespace EDNexus.Plugins.Abstractions;

/// <summary>
/// The entry point every EDNexus plugin implements. The host discovers exactly one
/// implementation per plugin assembly, constructs an <see cref="IPluginContext"/> for it, and
/// drives its lifecycle by calling <see cref="Initialize"/> once at load and <see cref="Shutdown"/>
/// once at unload.
/// </summary>
public interface IEDNexusPlugin
{
    /// <summary>
    /// Called once when the plugin is loaded. <paramref name="context"/> is valid for the
    /// lifetime of the plugin session; subscribe to events and register UI here.
    /// </summary>
    void Initialize(IPluginContext context);

    /// <summary>
    /// Called once when the plugin is being unloaded (app shutdown, plugin disabled, reload, or the
    /// app rebuilding its engine). Release any resources and stop using the
    /// <see cref="IPluginContext"/> passed to <see cref="Initialize"/> after this returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This must return promptly.</b> The host runs it on a worker thread of its own (not the UI
    /// thread, and not the thread that called <see cref="Initialize"/>) and waits only a bounded time
    /// for it (5 seconds by default). If it has not returned by then the host reports the plugin as
    /// stuck, leaves it running, disposes the context under it and carries on; a late return or a
    /// late exception is discarded. Because a plugin can be unloaded and loaded again in one process
    /// (an engine rebuild), a new instance of the plugin class is constructed for each load.
    /// </para>
    /// <para>
    /// Event handlers run on the plugin's own delivery thread, which the host only stops after
    /// <c>Shutdown</c> returns (or is abandoned). A handler can therefore be running <b>concurrently with</b>
    /// <c>Shutdown</c> (and a handler that was already running may still be finishing a moment
    /// after it): guard shared state, and do not assume handlers are quiet while you tear down. A
    /// handler that never returns makes the host report the plugin as stuck rather than unloaded.
    /// </para>
    /// </remarks>
    void Shutdown();
}
