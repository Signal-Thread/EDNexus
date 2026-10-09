using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting;

/// <summary>What happened to one plugin folder during <see cref="PluginHost.LoadAll"/>.</summary>
public enum PluginLoadStatus
{
    /// <summary>The plugin was loaded, instantiated and initialised, and is running.</summary>
    Loaded,

    /// <summary>
    /// The plugin folder is not a loadable plugin: a missing or invalid manifest, an id that does
    /// not match its folder, a duplicate id, or an entry assembly / type that is missing or does
    /// not implement <see cref="IEDNexusPlugin"/>. No plugin code ran.
    /// </summary>
    Rejected,

    /// <summary>
    /// The manifest is valid but targets an SDK or app version this EDNexus cannot run. No plugin
    /// code ran.
    /// </summary>
    Incompatible,

    /// <summary>
    /// Plugin code was loaded and threw (while loading, in its constructor, or in
    /// <see cref="IEDNexusPlugin.Initialize"/>). After a failed <c>Initialize</c> the host calls
    /// <see cref="IEDNexusPlugin.Shutdown"/> (best effort); in every case it disposes the context and
    /// unloads the load context. This does not guarantee plugin code has stopped: threads, timers
    /// or static subscriptions it started can keep running (and keep it in memory). A plugin that
    /// hangs (never returns from its constructor, <c>Initialize</c> or <c>Shutdown</c>) is never
    /// reported at all: it blocks the pass that called it (see #62).
    /// </summary>
    Failed,

    /// <summary>
    /// The plugin passed every static check but the host's consent callback did not allow it to run
    /// (see <see cref="PluginHost"/>). No plugin code ran.
    /// </summary>
    Denied,
}

/// <summary>The outcome for one folder under the plugins root.</summary>
/// <remarks>
/// Failures are recorded as text only — never as the exception object — because an exception
/// thrown by plugin code can hold types from the plugin's load context and would stop it from
/// ever unloading.
/// </remarks>
public sealed class PluginLoadResult
{
    internal PluginLoadResult(
        string directory,
        PluginManifest? manifest,
        PluginLoadStatus status,
        IReadOnlyList<string> reasons,
        LoadedPlugin? plugin)
    {
        Directory = directory;
        Manifest = manifest;
        Status = status;
        Reasons = reasons;
        Plugin = plugin;
    }

    /// <summary>Full path of the plugin folder.</summary>
    public string Directory { get; }

    /// <summary>The folder name (the plugin id, for a well-formed install).</summary>
    public string FolderName => Path.GetFileName(Directory);

    /// <summary>The parsed manifest, or <see langword="null"/> when it was missing or invalid.</summary>
    public PluginManifest? Manifest { get; }

    /// <summary>What happened.</summary>
    public PluginLoadStatus Status { get; }

    /// <summary>Why the plugin is not loaded (empty when <see cref="Status"/> is <see cref="PluginLoadStatus.Loaded"/>).</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary><see cref="Reasons"/> joined for a single log line.</summary>
    public string ReasonSummary => string.Join("; ", Reasons);

    /// <summary>The running plugin when <see cref="Status"/> is <see cref="PluginLoadStatus.Loaded"/>.</summary>
    public LoadedPlugin? Plugin { get; }

    /// <inheritdoc />
    public override string ToString()
        => Reasons.Count == 0 ? $"{FolderName}: {Status}" : $"{FolderName}: {Status} ({ReasonSummary})";
}

/// <summary>Everything <see cref="PluginHost.LoadAll"/> did, in folder-name order.</summary>
/// <param name="Root">The plugins root that was scanned.</param>
/// <param name="Recovery">
/// What <see cref="PluginInstaller.RecoverInterrupted"/> repaired before discovery. Recovery runs
/// only on a host's first pass, so later passes report an empty result (see <see cref="PluginHost.Recovery"/>).
/// </param>
/// <param name="Errors">
/// Problems not tied to one plugin: recovery errors and a plugins root that could not be read.
/// </param>
/// <param name="Plugins">One result per plugin folder (dot-prefixed folders are not plugins and are skipped).</param>
public sealed record PluginDiscoveryReport(
    string Root,
    PluginRecoveryResult Recovery,
    IReadOnlyList<string> Errors,
    IReadOnlyList<PluginLoadResult> Plugins)
{
    /// <summary>
    /// The plugins from this pass that are still running (evaluated on each enumeration, so a
    /// plugin unloaded since the pass is left out; <see cref="Plugins"/> keeps its load-time status).
    /// </summary>
    public IEnumerable<LoadedPlugin> Loaded
        => Plugins.Where(p => p.Plugin is { IsLoaded: true }).Select(p => p.Plugin!);
}

/// <summary>What <see cref="PluginHost.Unload"/> did.</summary>
public sealed class PluginUnloadResult
{
    internal PluginUnloadResult(string id, IReadOnlyList<string> errors, WeakReference loadContext, bool stuck = false)
    {
        Id = id;
        Errors = errors;
        LoadContext = loadContext;
        Stuck = stuck;
    }

    /// <summary>The plugin that was unloaded.</summary>
    public string Id { get; }

    /// <summary>
    /// Problems during unload (e.g. <see cref="IEDNexusPlugin.Shutdown"/> threw). The load context
    /// is released regardless.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// True when a plugin event handler was still running when the unload wait expired
    /// (<see cref="PluginHost.UnloadTimeout"/>). The plugin's code is still executing, so its load
    /// context stays alive (and its assemblies locked) until that handler returns; the app should
    /// treat the plugin as <b>not</b> cleanly unloaded, e.g. refuse to replace its files. Also
    /// described in <see cref="Errors"/>.
    /// </summary>
    public bool Stuck { get; }

    /// <summary>
    /// The unloaded load context. It is collected once nothing references the plugin's types any
    /// more (after a GC); tests use this to prove the plugin does not leak.
    /// </summary>
    internal WeakReference LoadContext { get; }
}
