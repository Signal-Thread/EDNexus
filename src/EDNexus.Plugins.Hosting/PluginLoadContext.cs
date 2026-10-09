using System.Reflection;
using System.Runtime.Loader;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting;

/// <summary>
/// The collectible <see cref="AssemblyLoadContext"/> one plugin lives in. Its private dependencies
/// resolve from its own folder (via its <c>.deps.json</c>, or every assembly beside the entry
/// assembly when it has none), so two plugins can ship different versions of the same library.
/// The SDK contract assembly is always the host's copy, even when the plugin bundles one, so
/// <see cref="IEDNexusPlugin"/> in the plugin and in the host are the same type. Anything else the
/// plugin does not ship falls through to the default context (the BCL and the framework) — except
/// the host's own assemblies (<c>EDNexus.*</c>, such as <c>EDNexus.Core</c>, and
/// <c>EliteDangerous.*</c>): a plugin that references one without shipping its own copy fails
/// loudly instead of silently reaching into the host's internals. This isolates dependency
/// versions; it is not a security boundary.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly Assembly ContractAssembly = typeof(IEDNexusPlugin).Assembly;
    private static readonly string ContractAssemblyName = ContractAssembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;

    /// <param name="id">The plugin id (used in the context name for diagnostics).</param>
    /// <param name="entryAssemblyPath">Full path of the plugin's entry assembly (must exist).</param>
    public PluginLoadContext(string id, string entryAssemblyPath)
        : base("EDNexus plugin " + id, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    /// <summary>Whether <paramref name="name"/> must come from the host rather than the plugin folder.</summary>
    internal static bool IsShared(AssemblyName name)
        => string.Equals(name.Name, ContractAssemblyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="name"/> belongs to the host (not the framework) and so is not
    /// something a plugin may bind to without shipping its own copy.
    /// </summary>
    internal static bool IsHostInternal(AssemblyName name)
        => name.Name is { } n
           && (n.StartsWith("EDNexus.", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("EliteDangerous.", StringComparison.OrdinalIgnoreCase));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Hand back the host's already-loaded contract assembly directly rather than deferring to
        // the default context, so a plugin built against a newer assembly version of a compatible
        // SDK (the manifest gate has already checked the SDK version) still binds to it.
        if (IsShared(assemblyName))
            return ContractAssembly;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null)
            return LoadFromAssemblyPath(path);

        if (IsHostInternal(assemblyName))
            throw new FileLoadException(
                $"'{assemblyName.Name}' is part of EDNexus itself and is not available to plugins: plugins may only reference "
                + $"{ContractAssemblyName} (ship your own copy beside the plugin if you need a library with this name).",
                assemblyName.Name);

        return null;   // the framework (BCL): the default context
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
