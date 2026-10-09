using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting;

/// <summary>
/// Reads a plugin assembly's metadata <em>without loading it</em> (no plugin code runs, nothing
/// is locked beyond the read itself).
/// </summary>
internal static class PluginAssemblyInspector
{
    private static readonly string ContractAssemblyName = typeof(IEDNexusPlugin).Assembly.GetName().Name!;

    /// <summary>
    /// The version of <c>EDNexus.Plugins.Abstractions</c> the assembly at <paramref name="path"/> was
    /// compiled against (the SDK assembly's version tracks the SDK contract version
    /// <c>major.minor</c>), or <see langword="null"/> when it does not reference it or is not a
    /// readable .NET assembly (the normal load path then reports that properly).
    /// </summary>
    public static Version? GetReferencedSdkVersion(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return null;
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                if (string.Equals(reader.GetString(reference.Name), ContractAssemblyName, StringComparison.OrdinalIgnoreCase))
                    return reference.Version;
            }
            return null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
