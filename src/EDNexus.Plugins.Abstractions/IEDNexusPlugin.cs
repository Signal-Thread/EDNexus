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
    /// Called once when the plugin is being unloaded (app shutdown, plugin disabled, or reload).
    /// Release any resources and stop using the <see cref="IPluginContext"/> passed to
    /// <see cref="Initialize"/> after this returns.
    /// </summary>
    /// <remarks>
    /// Event handlers run on the plugin's own delivery thread, which the host only stops after
    /// <c>Shutdown</c> returns. A handler can therefore be running <b>concurrently with</b>
    /// <c>Shutdown</c> (and a handler that was already running may still be finishing a moment
    /// after it): guard shared state, and do not assume handlers are quiet while you tear down. A
    /// handler that never returns makes the host report the plugin as stuck rather than unloaded.
    /// </remarks>
    void Shutdown();
}
