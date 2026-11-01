using System.Text;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting.Bridge;

/// <summary>
/// File-backed <see cref="IPluginStorage"/> for one plugin, confined to
/// <c>&lt;pluginsRoot&gt;/&lt;id&gt;/data/</c> (never the install directory). Created on first write.
/// </summary>
/// <remarks>
/// Keys are restricted to a conservative character set and mapped to <c>k_&lt;key&gt;.txt</c>, so a key can
/// never contain a separator, a drive, <c>..</c> or a Windows device name; the resolved path is also
/// checked to still sit inside the data directory. An invalid key or oversize value throws
/// <see cref="ArgumentException"/>. Unreadable files read as unset rather than throwing; write
/// failures (disk full, locked file) surface as <see cref="IOException"/> from
/// <see cref="SetString"/> so the plugin can react.
/// </remarks>
internal sealed class SandboxedPluginStorage : IPluginStorage
{
    /// <summary>Longest accepted key, in characters.</summary>
    public const int MaxKeyLength = 100;

    /// <summary>Largest accepted value, in UTF-8 bytes.</summary>
    public const int MaxValueBytes = 1024 * 1024;

    private const string Prefix = "k_";
    private const string Suffix = ".txt";

    private readonly string _dataDirectory;
    private readonly object _gate = new();

    /// <param name="pluginsRoot">The plugins root; the plugin's folder is <c>&lt;root&gt;/&lt;pluginId&gt;</c>.</param>
    /// <param name="pluginId">A valid plugin id (validated by <see cref="PluginPaths.PluginDirectory"/>).</param>
    public SandboxedPluginStorage(string pluginsRoot, string pluginId)
    {
        _dataDirectory = Path.GetFullPath(Path.Combine(PluginPaths.PluginDirectory(pluginsRoot, pluginId), "data"));
    }

    /// <summary>The absolute directory this plugin's values live in (may not exist yet).</summary>
    public string DataDirectory => _dataDirectory;

    public string? GetString(string key)
    {
        var path = PathFor(key);
        try
        {
            lock (_gate)
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxValueBytes) return null;
                return File.ReadAllText(path, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return null;
        }
    }

    public void SetString(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = PathFor(key);
        var bytes = new UTF8Encoding(false).GetBytes(value);
        if (bytes.Length > MaxValueBytes)
            throw new ArgumentException($"Value exceeds the {MaxValueBytes}-byte limit.", nameof(value));
        lock (_gate)
        {
            Directory.CreateDirectory(_dataDirectory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    public void Remove(string key)
    {
        var path = PathFor(key);
        try
        {
            lock (_gate) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Nothing sensible for a plugin to do; the documented contract is a no-op.
        }
    }

    private string PathFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is 0 or > MaxKeyLength)
            throw new ArgumentException($"Key must be 1-{MaxKeyLength} characters.", nameof(key));
        foreach (var c in key)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!ok) throw new ArgumentException("Key may contain only letters, digits, '.', '_' and '-'.", nameof(key));
        }
        var full = Path.GetFullPath(Path.Combine(_dataDirectory, Prefix + key + Suffix));
        if (!string.Equals(Path.GetDirectoryName(full), _dataDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            throw new ArgumentException("Key resolves outside the plugin's data directory.", nameof(key));
        return full;
    }
}
