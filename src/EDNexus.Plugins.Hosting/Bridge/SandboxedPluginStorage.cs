using System.Collections.Concurrent;
using System.Text;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting.Bridge;

/// <summary>
/// File-backed <see cref="IPluginStorage"/> for one plugin, confined to
/// <c>&lt;pluginsRoot&gt;/&lt;id&gt;/data/</c> (never the install directory). The <c>data</c> folder is
/// created on first write, but only under a plugin folder that already exists.
/// </summary>
/// <remarks>
/// <para>
/// Keys are restricted to a conservative character set and mapped to <c>k_&lt;key&gt;.txt</c>, so a key can
/// never contain a separator, a drive, <c>..</c> or a Windows device name; the resolved path is also
/// checked to still sit inside the data directory. Keys are case-insensitive (the filename is the
/// lower-cased key) so behaviour is identical on case-sensitive and case-insensitive filesystems.
/// </para>
/// <para>
/// Errors: an invalid key, an oversize value or a value that is not valid Unicode (a lone surrogate)
/// throws <see cref="ArgumentException"/>; exceeding the per-plugin quota, a missing plugin folder, or
/// any other IO failure throws <see cref="IOException"/>. Reading distinguishes "unset" (null) from
/// "present but unreadable" (<see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>), so a
/// read-modify-write cannot clobber good data after a transient lock.
/// </para>
/// <para>
/// Trust assumption: containment is lexical. A symlink or junction planted inside the plugins root
/// by someone who can already write there crosses no trust boundary (the same position
/// <c>PluginHost</c> takes, and the installer refuses links in packages), so links are not chased.
/// </para>
/// <para>
/// Writers for the same data directory are serialised process-wide, whichever instance they use.
/// </para>
/// </remarks>
internal sealed class SandboxedPluginStorage : IPluginStorage
{
    /// <summary>Longest accepted key, in characters.</summary>
    public const int MaxKeyLength = 100;

    /// <summary>Largest accepted value, in UTF-8 bytes.</summary>
    public const int MaxValueBytes = 1024 * 1024;

    /// <summary>Most keys one plugin may hold.</summary>
    public const int MaxKeyCount = 256;

    /// <summary>Most UTF-8 bytes one plugin may hold across all its values.</summary>
    public const long MaxTotalBytes = 8L * 1024 * 1024;

    private const string Prefix = "k_";
    private const string Suffix = ".txt";

    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private readonly string _pluginDirectory;
    private readonly string _dataDirectory;
    private readonly object _gate;
    private bool _swept;

    /// <param name="pluginsRoot">The plugins root; the plugin's folder is <c>&lt;root&gt;/&lt;pluginId&gt;</c>.</param>
    /// <param name="pluginId">A valid plugin id (validated by <see cref="PluginPaths.PluginDirectory"/>).</param>
    public SandboxedPluginStorage(string pluginsRoot, string pluginId)
    {
        _pluginDirectory = Path.GetFullPath(PluginPaths.PluginDirectory(pluginsRoot, pluginId));
        _dataDirectory = Path.Combine(_pluginDirectory, "data");
        _gate = Gates.GetOrAdd(_dataDirectory, static _ => new object());
    }

    /// <summary>The absolute directory this plugin's values live in (may not exist yet).</summary>
    public string DataDirectory => _dataDirectory;

    public string? GetString(string key)
    {
        var path = PathFor(key);
        lock (_gate)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            using (stream)
            {
                // Bounded read: never allocate more than the cap, however big the file has become.
                var buffer = new byte[MaxValueBytes + 1];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                    total += read;
                if (total > MaxValueBytes)
                    throw new IOException($"Stored value for '{key}' exceeds the {MaxValueBytes}-byte limit.");
                return Encoding.UTF8.GetString(buffer, 0, total);
            }
        }
    }

    public void SetString(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = PathFor(key);
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Value is not valid Unicode text (it contains an unpaired surrogate).", nameof(value));
        }
        if (bytes.Length > MaxValueBytes)
            throw new ArgumentException($"Value exceeds the {MaxValueBytes}-byte limit.", nameof(value));

        lock (_gate)
        {
            if (!Directory.Exists(_pluginDirectory))
                throw new DirectoryNotFoundException("The plugin's folder no longer exists; its storage is unavailable.");
            Directory.CreateDirectory(_dataDirectory);
            SweepTempFiles();
            EnforceQuota(path, bytes.Length);

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
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

    /// <summary>Deletes temp files left by a crash between write and move (once per instance).</summary>
    private void SweepTempFiles()
    {
        if (_swept) return;
        _swept = true;
        try
        {
            foreach (var stale in Directory.EnumerateFiles(_dataDirectory, Prefix + "*.tmp"))
            {
                try { File.Delete(stale); } catch { /* best effort */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void EnforceQuota(string targetPath, int newBytes)
    {
        long total = newBytes;
        var count = 1;
        foreach (var file in new DirectoryInfo(_dataDirectory).EnumerateFiles(Prefix + "*" + Suffix))
        {
            if (string.Equals(file.FullName, targetPath, StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            total += file.Length;
        }
        if (count > MaxKeyCount)
            throw new IOException($"Storage quota exceeded: at most {MaxKeyCount} keys per plugin.");
        if (total > MaxTotalBytes)
            throw new IOException($"Storage quota exceeded: at most {MaxTotalBytes} bytes per plugin.");
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
        // Lower-cased so "Foo" and "foo" are one key on every filesystem. Safe: the charset is ASCII.
        var full = Path.GetFullPath(Path.Combine(_dataDirectory, Prefix + key.ToLowerInvariant() + Suffix));
        if (!string.Equals(Path.GetDirectoryName(full), _dataDirectory, StringComparison.Ordinal))
            throw new ArgumentException("Key resolves outside the plugin's data directory.", nameof(key));
        return full;
    }
}

/// <summary>
/// Wraps a plugin's storage so the session can cut it off on dispose: handlers can still run after
/// <see cref="PluginBridgeSession.Dispose"/> returns, and a late write must not recreate a folder the
/// host has since uninstalled or replaced.
/// </summary>
internal sealed class RevocablePluginStorage(IPluginStorage inner, string pluginId) : IPluginStorage
{
    private volatile IPluginStorage? _inner = inner;

    /// <summary>Makes every later call throw <see cref="UnauthorizedAccessException"/>.</summary>
    public void Revoke() => _inner = null;

    public string? GetString(string key) => Live().GetString(key);
    public void SetString(string key, string value) => Live().SetString(key, value);
    public void Remove(string key) => Live().Remove(key);

    private IPluginStorage Live() => _inner ?? throw CapabilityDenied.For(pluginId, PluginCapabilities.Storage);
}
