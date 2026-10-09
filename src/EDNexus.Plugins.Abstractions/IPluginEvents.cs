namespace EDNexus.Plugins.Abstractions;

/// <summary>
/// The event feed a plugin subscribes to. Mirrors the host's internal journal event bus, but
/// only ever hands out read-only <see cref="IJournalEvent"/> instances, and isolates handler
/// exceptions from the rest of the host and other plugins the same way the internal bus does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dispatch model.</b> Handlers never run on the host's journal thread. The host queues each
/// observed event per plugin and delivers it on a background worker dedicated to that plugin, one
/// event at a time and in journal order, so a slow plugin only delays itself. By the time a
/// handler runs, the host has already folded the event into <see cref="IPluginContext.State"/>
/// (which may by then reflect later events too). If a plugin falls far enough behind, the oldest
/// undelivered events are dropped rather than letting the backlog grow without bound: the limit is
/// on both the number of events and on the memory they hold, so a plugin that stalls while
/// subscribed to everything loses its oldest events well before it can hold gigabytes of payloads.
/// </para>
/// <para>
/// A handler may still be running while the plugin's <see cref="IEDNexusPlugin.Shutdown"/> runs;
/// see its remarks.
/// </para>
/// <para>
/// A handler that throws is reported to the host against the owning plugin and does not affect
/// delivery to its other handlers or to other plugins. Handlers registered through
/// <see cref="Subscribe"/>/<see cref="SubscribeAny"/> stay registered until the plugin is unloaded;
/// use <see cref="On"/>/<see cref="OnAny"/> to get a subscription that can be removed earlier.
/// </para>
/// </remarks>
public interface IPluginEvents
{
    /// <summary>
    /// Registers <paramref name="handler"/> to run whenever an event named <paramref name="eventName"/>
    /// is observed (e.g. <c>"FSDJump"</c>). Matching is case-sensitive and exact, matching the
    /// journal's own <c>event</c> field.
    /// </summary>
    void Subscribe(string eventName, Action<IJournalEvent> handler);

    /// <summary>
    /// Registers <paramref name="handler"/> to run for every event observed, regardless of name.
    /// Useful for plugins that log, mirror, or filter events generically rather than reacting to
    /// a fixed set of event names.
    /// </summary>
    void SubscribeAny(Action<IJournalEvent> handler);

    /// <summary>
    /// Like <see cref="Subscribe"/>, but returns a handle whose <see cref="IDisposable.Dispose"/>
    /// removes the handler. Disposing is idempotent; an event already queued for the plugin when
    /// the handle is disposed is not delivered to the removed handler.
    /// </summary>
    /// <exception cref="NotSupportedException">The host predates removable subscriptions.</exception>
    IDisposable On(string eventName, Action<IJournalEvent> handler)
        => throw new NotSupportedException("This host does not support removable plugin subscriptions.");

    /// <summary>
    /// Like <see cref="SubscribeAny"/>, but returns a handle whose <see cref="IDisposable.Dispose"/>
    /// removes the handler. Disposing is idempotent.
    /// </summary>
    /// <exception cref="NotSupportedException">The host predates removable subscriptions.</exception>
    IDisposable OnAny(Action<IJournalEvent> handler)
        => throw new NotSupportedException("This host does not support removable plugin subscriptions.");
}
