namespace EDNexus.Plugins.Hosting;

/// <summary>
/// Implemented by the <see cref="EDNexus.Plugins.Abstractions.IPluginContext"/> a context factory
/// returns when plugin code can still be running after the context is disposed — in practice, the
/// event handlers a bridge session delivers on its own thread (see
/// <see cref="Bridge.PluginBridgeSession"/>). <see cref="PluginHost"/> disposes the context (which
/// stops delivery), then calls <see cref="WaitForExit"/> so a handler blocked inside plugin code is
/// reported as <b>stuck</b> instead of the plugin being declared cleanly unloaded.
/// </summary>
public interface IPluginSessionControl
{
    /// <summary>
    /// After the context has been disposed, waits up to <paramref name="timeout"/> for every
    /// in-flight plugin callback to return. Returns <see langword="false"/> when one is still
    /// running: the plugin's code is then live on a thread's stack and its load context cannot be
    /// fully unloaded. Must not throw for a slow plugin.
    /// </summary>
    bool WaitForExit(TimeSpan timeout);
}
