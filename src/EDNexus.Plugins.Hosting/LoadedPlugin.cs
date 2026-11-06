using System.Runtime.CompilerServices;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting;

/// <summary>
/// A plugin that <see cref="PluginHost"/> has loaded into its own collectible load context,
/// instantiated and initialised. Hold on to this (not to <see cref="Instance"/>) in UI and
/// management code: after <see cref="PluginHost.Unload"/> it drops every reference to the
/// plugin's code so the load context can be collected.
/// </summary>
public sealed class LoadedPlugin
{
    private readonly object _gate = new();
    private IEDNexusPlugin? _instance;
    private IPluginContext? _context;
    private PluginLoadContext? _loadContext;

    internal LoadedPlugin(
        PluginManifest manifest,
        string directory,
        string entryAssemblyPath,
        PluginLoadContext loadContext,
        IEDNexusPlugin instance,
        IPluginContext context)
    {
        Manifest = manifest;
        Directory = directory;
        EntryAssemblyPath = entryAssemblyPath;
        _loadContext = loadContext;
        _instance = instance;
        _context = context;
    }

    /// <summary>The plugin's validated manifest.</summary>
    public PluginManifest Manifest { get; }

    /// <summary>The plugin id (<see cref="PluginManifest.Id"/>).</summary>
    public string Id => Manifest.Id;

    /// <summary>Full path of the plugin folder.</summary>
    public string Directory { get; }

    /// <summary>Full path of the entry assembly that was loaded.</summary>
    public string EntryAssemblyPath { get; }

    /// <summary>The running plugin, or <see langword="null"/> once unloaded.</summary>
    public IEDNexusPlugin? Instance
    {
        get { lock (_gate) return _instance; }
    }

    /// <summary>Whether the plugin is still loaded.</summary>
    public bool IsLoaded
    {
        get { lock (_gate) return _loadContext is not null; }
    }

    /// <summary>The name of the plugin's load context, for diagnostics.</summary>
    internal string? LoadContextName
    {
        get { lock (_gate) return _loadContext?.Name; }
    }

    /// <summary>
    /// Calls <see cref="IEDNexusPlugin.Shutdown"/> (contained, on a worker, waiting at most
    /// <paramref name="shutdownTimeout"/>), disposes the plugin's context when it is
    /// <see cref="IDisposable"/>, drops every reference to plugin code and unloads the load
    /// context. Idempotent, and never throws for plugin misbehaviour: problems are returned in
    /// <see cref="PluginUnloadResult.Errors"/>. When the context is an
    /// <see cref="IPluginSessionControl"/>, waits up to <paramref name="handlerExitTimeout"/> after
    /// disposing it for in-flight handlers. A <c>Shutdown</c> that does not return in time, or a
    /// handler that does not exit, is reported as stuck instead of claiming a clean unload; the
    /// context is disposed and the load context released either way.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal PluginUnloadResult Unload(TimeSpan shutdownTimeout, TimeSpan handlerExitTimeout)
    {
        IEDNexusPlugin? instance;
        IPluginContext? context;
        PluginLoadContext? loadContext;
        lock (_gate)
        {
            instance = _instance;
            context = _context;
            loadContext = _loadContext;
            _instance = null;
            _context = null;
            _loadContext = null;
        }

        var errors = new List<string>();
        var stuck = false;
        var weak = new WeakReference(loadContext);
        try
        {
            if (instance is not null)
                stuck = PluginHost.TryShutdown(instance, shutdownTimeout, errors);
        }
        catch (Exception ex)
        {
            errors.Add(PluginHost.Describe("unloading failed", ex));
        }
        finally
        {
            // Whatever Shutdown did, the context and load context are always released.
            try
            {
                stuck |= PluginHost.DisposeContext(context, handlerExitTimeout, errors);
            }
            catch (Exception ex)
            {
                errors.Add(PluginHost.Describe("disposing the plugin context failed", ex));
            }
            finally
            {
                PluginHost.TryUnloadContext(loadContext, errors);
            }
        }
        return new PluginUnloadResult(Id, errors, weak, stuck);
    }
}
