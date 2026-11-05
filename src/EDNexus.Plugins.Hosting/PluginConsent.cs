using System.Security.Cryptography;
using System.Text;
using EDNexus.Core.Settings;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting;

/// <summary>
/// A stable fingerprint of a plugin's manifest contents, used to key the commander's consent so that
/// an approval given for one <c>plugin.json</c> is never applied to a different one that happens to
/// have the same id.
/// </summary>
/// <remarks>
/// Covers every manifest field (id, name, version, SDK and app version, author, description, entry
/// assembly and type, and the declared capabilities, order-insensitively), so an update that changes
/// the version or asks for another capability needs a fresh approval. It is a hash of the
/// <em>manifest</em> only: the host's consent callback receives nothing else, so a plugin DLL
/// replaced on disk <em>without</em> bumping any manifest field is not detected by it. Install and
/// update go through the installer, which is where that is guarded.
/// </remarks>
public static class PluginManifestFingerprint
{
    /// <summary>The lowercase hex SHA-256 of <paramref name="manifest"/>'s contents.</summary>
    public static string Compute(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Field(hash, manifest.Id);
        Field(hash, manifest.Name);
        Field(hash, manifest.Version);
        Field(hash, manifest.SdkVersion);
        Field(hash, manifest.Author);
        Field(hash, manifest.Description);
        Field(hash, manifest.MinAppVersion);
        Field(hash, manifest.EntryAssembly);
        Field(hash, manifest.EntryType);
        var capabilities = manifest.Capabilities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Field(hash, capabilities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var capability in capabilities)
            Field(hash, capability);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // Length-prefixed (and null-distinguished) so that no two different manifests share an input.
    private static void Field(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            hash.AppendData(BitConverter.GetBytes(-1));
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}

/// <summary>What the commander approved for one plugin, and for which manifest.</summary>
/// <param name="ManifestHash">The <see cref="PluginManifestFingerprint"/> of the approved manifest.</param>
/// <param name="Capabilities">The capabilities the commander granted.</param>
public sealed record PluginGrant(string ManifestHash, IReadOnlyList<string> Capabilities);

/// <summary>
/// Where the commander's consent decisions live. This is the seam the plugin-management UI (#61)
/// fills: it shows the manifest, asks, and calls <see cref="PluginGrantStoreExtensions.Grant"/>.
/// Until something does, nothing is granted and no plugin runs.
/// </summary>
/// <remarks>Implementations are called from lifecycle threads and must be thread-safe; <see cref="Get"/> must be cheap and in-memory.</remarks>
public interface IPluginGrantStore
{
    /// <summary>The recorded grant for <paramref name="pluginId"/>, or null if there is none.</summary>
    PluginGrant? Get(string pluginId);

    /// <summary>Records (replacing any earlier) the grant for <paramref name="pluginId"/>. May do disk I/O.</summary>
    void Set(string pluginId, PluginGrant grant);

    /// <summary>Forgets the grant for <paramref name="pluginId"/>; returns whether there was one. May do disk I/O.</summary>
    bool Remove(string pluginId);
}

/// <summary>Convenience for recording consent against a manifest.</summary>
public static class PluginGrantStoreExtensions
{
    /// <summary>
    /// Records that the commander allowed <paramref name="manifest"/> (these exact contents) to run
    /// with <paramref name="capabilities"/>. Capabilities the manifest does not declare are dropped.
    /// </summary>
    public static void Grant(this IPluginGrantStore store, PluginManifest manifest, IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(capabilities);
        var granted = manifest.Capabilities.Where(c => capabilities.Contains(c, StringComparer.Ordinal)).ToList();
        store.Set(manifest.Id, new PluginGrant(PluginManifestFingerprint.Compute(manifest), granted));
    }
}

/// <summary>An in-memory grant store (tests, and hosts that don't persist consent).</summary>
public sealed class InMemoryPluginGrantStore : IPluginGrantStore
{
    private readonly Dictionary<string, PluginGrant> _grants = new(StringComparer.Ordinal);

    public PluginGrant? Get(string pluginId) { lock (_grants) return _grants.GetValueOrDefault(pluginId); }

    public void Set(string pluginId, PluginGrant grant) { lock (_grants) _grants[pluginId] = grant; }

    public bool Remove(string pluginId) { lock (_grants) return _grants.Remove(pluginId); }
}

/// <summary>
/// Persists grants in <see cref="PluginSettings.Grants"/> and asks <paramref name="save"/> (e.g.
/// <c>SettingsStore.TrySave</c>) to write them. Copy-on-write like
/// <see cref="AppSettingsPluginEnablementStore"/>: a save running on another thread never serialises
/// a dictionary that is being changed, and <paramref name="save"/> runs outside this store's lock.
/// </summary>
public sealed class AppSettingsPluginGrantStore(AppSettings settings, Action<AppSettings> save) : IPluginGrantStore
{
    private readonly object _gate = new();

    public PluginGrant? Get(string pluginId)
        => settings.Plugins?.Grants is { } grants && grants.TryGetValue(pluginId, out var g) && g is not null
            ? new PluginGrant(g.ManifestHash ?? "", [.. g.Capabilities ?? []])
            : null;

    public void Set(string pluginId, PluginGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            var plugins = EnsurePlugins();
            var next = new Dictionary<string, PluginGrantSettings>(plugins.Grants ?? [], StringComparer.Ordinal)
            {
                [pluginId] = new PluginGrantSettings { ManifestHash = grant.ManifestHash, Capabilities = [.. grant.Capabilities] },
            };
            plugins.Grants = next;
        }
        save(settings);
    }

    public bool Remove(string pluginId)
    {
        bool removed;
        lock (_gate)
        {
            var plugins = EnsurePlugins();
            var next = new Dictionary<string, PluginGrantSettings>(plugins.Grants ?? [], StringComparer.Ordinal);
            removed = next.Remove(pluginId);
            if (removed)
                plugins.Grants = next;
        }
        if (removed)
            save(settings);
        return removed;
    }

    private PluginSettings EnsurePlugins()
    {
        var plugins = settings.Plugins;
        if (plugins is null)
            settings.Plugins = plugins = new PluginSettings();
        return plugins;
    }
}

/// <summary>Ready-made consent callbacks for <see cref="PluginHost"/>.</summary>
/// <remarks>
/// <b>Policy.</b> The desktop app runs no plugin code without an explicit, persisted grant:
/// <list type="bullet">
/// <item><description><see cref="DenyAll"/> is the floor, and what an app with no consent UI uses.</description></item>
/// <item><description><see cref="FromGrants"/> allows exactly the plugins that have a grant whose fingerprint equals
/// the fingerprint of the manifest the host just read, with exactly the granted capabilities. A new,
/// updated, edited or unknown plugin is denied; so is a store that throws.</description></item>
/// </list>
/// The callback is evaluated by <see cref="PluginHost"/> on every load, including a reload, against
/// the manifest as it is on disk at that moment, so consent can neither be skipped on reload nor
/// carried over to changed files.
/// </remarks>
public static class PluginConsent
{
    /// <summary>Refuses every plugin: none of their code is loaded.</summary>
    public static Func<PluginManifest, IReadOnlyCollection<string>?> DenyAll { get; } = static _ => null;

    /// <summary>
    /// Allows a plugin only if <paramref name="grants"/> holds a grant for its id made for these exact
    /// manifest contents; the plugin then gets the granted capabilities.
    /// </summary>
    public static Func<PluginManifest, IReadOnlyCollection<string>?> FromGrants(IPluginGrantStore grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return manifest =>
        {
            PluginGrant? grant;
            try { grant = grants.Get(manifest.Id); }
            catch { return null; }   // fail closed
            if (grant is null || string.IsNullOrEmpty(grant.ManifestHash))
                return null;
            if (!string.Equals(grant.ManifestHash, PluginManifestFingerprint.Compute(manifest), StringComparison.Ordinal))
                return null;
            return grant.Capabilities ?? [];
        };
    }
}
