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
/// <em>manifest</em> only, and a manifest is public: anyone can reproduce this value for any
/// published plugin. It therefore proves nothing about the code. A grant is bound to the code by
/// the content hashes in <see cref="PluginConsentRequest"/> (the entry assembly's exact bytes and
/// the plugin folder's other files), which <see cref="PluginConsent.FromGrants"/> requires in
/// addition to this fingerprint. The installer does not consult grants, so that check at load time
/// is the only thing that makes an update (or a hand-replaced file) need a fresh approval.
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

/// <summary>What the commander approved for one plugin, and for which manifest and which code.</summary>
/// <param name="ManifestHash">The <see cref="PluginManifestFingerprint"/> of the approved manifest.</param>
/// <param name="EntryHash">The <see cref="PluginContentHash.OfAssembly"/> of the approved entry assembly image.</param>
/// <param name="FilesHash">The <see cref="PluginContentHash.OfFolder"/> of the approved plugin folder's other files.</param>
/// <param name="Capabilities">The capabilities the commander granted.</param>
/// <remarks>
/// A grant with a missing or empty hash of any kind (including every grant written before the
/// content hashes existed) never matches: <see cref="PluginConsent.FromGrants"/> treats it as no grant.
/// </remarks>
public sealed record PluginGrant(string ManifestHash, string EntryHash, string FilesHash, IReadOnlyList<string> Capabilities);

/// <summary>
/// Where the commander's consent decisions live. This is the seam the plugin-management UI (#61)
/// fills: it shows the manifest, asks, and calls <see cref="PluginGrantStoreExtensions.Grant(IPluginGrantStore, PluginConsentRequest, IEnumerable{string})"/>
/// with the <see cref="PluginConsentRequest"/> it showed (see <see cref="PluginConsentRequest.TryRead"/>).
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

/// <summary>Convenience for recording consent against what the host will see.</summary>
public static class PluginGrantStoreExtensions
{
    /// <summary>
    /// Records that the commander allowed exactly <paramref name="request"/> (this manifest, this
    /// entry assembly, these files) to run with <paramref name="capabilities"/>. Capabilities the
    /// manifest does not declare are dropped. There is deliberately no overload that takes only a
    /// manifest: a grant that is not bound to the code would let any package with a copied
    /// <c>plugin.json</c> inherit it.
    /// </summary>
    /// <exception cref="ArgumentException">The request carries no content hash.</exception>
    public static void Grant(this IPluginGrantStore store, PluginConsentRequest request, IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (string.IsNullOrEmpty(request.EntryAssemblyHash) || string.IsNullOrEmpty(request.FilesHash))
            throw new ArgumentException("A grant must be bound to the plugin's content hashes.", nameof(request));
        var manifest = request.Manifest;
        var granted = manifest.Capabilities.Where(c => capabilities.Contains(c, StringComparer.Ordinal)).ToList();
        store.Set(manifest.Id, new PluginGrant(PluginManifestFingerprint.Compute(manifest), request.EntryAssemblyHash, request.FilesHash, granted));
    }

    /// <summary>
    /// Reads the plugin folder (<see cref="PluginConsentRequest.TryRead"/>) and records a grant for
    /// what is in it now. Prefer the <see cref="PluginConsentRequest"/> overload with the request the
    /// commander was actually shown; this one is for tools that have no prompt.
    /// </summary>
    /// <exception cref="InvalidOperationException">The folder could not be read or hashed.</exception>
    public static void Grant(this IPluginGrantStore store, string pluginDirectory, IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pluginDirectory);
        if (!PluginConsentRequest.TryRead(pluginDirectory, out var request, out var problem))
            throw new InvalidOperationException($"Cannot grant '{pluginDirectory}': {problem}");
        store.Grant(request!, capabilities);
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
            ? new PluginGrant(g.ManifestHash ?? "", g.EntryHash ?? "", g.FilesHash ?? "", [.. g.Capabilities ?? []])
            : null;

    public void Set(string pluginId, PluginGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            var plugins = EnsurePlugins();
            var next = new Dictionary<string, PluginGrantSettings>(plugins.Grants ?? [], StringComparer.Ordinal)
            {
                [pluginId] = new PluginGrantSettings
                {
                    ManifestHash = grant.ManifestHash,
                    EntryHash = grant.EntryHash,
                    FilesHash = grant.FilesHash,
                    Capabilities = [.. grant.Capabilities],
                },
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
/// <item><description><see cref="FromGrants"/> allows exactly the plugins that have a grant whose manifest fingerprint,
/// entry-assembly hash and files hash all equal those of the <see cref="PluginConsentRequest"/> the host
/// just built, with exactly the granted capabilities. Fail closed: no grant, a missing or empty hash of
/// any kind (including a grant from before content hashes existed), any mismatch, or a store that
/// throws all deny. A new, updated, edited, replaced or unknown plugin is denied.</description></item>
/// </list>
/// The callback is evaluated by <see cref="PluginHost"/> on every load, including a reload, against
/// the files as they are on disk at that moment, so consent can neither be skipped on reload nor
/// carried over to changed or different files, even under a verbatim copy of the manifest.
/// </remarks>
public static class PluginConsent
{
    /// <summary>Refuses every plugin: none of their code is loaded.</summary>
    public static Func<PluginConsentRequest, IReadOnlyCollection<string>?> DenyAll { get; } = static _ => null;

    /// <summary>
    /// Allows a plugin only if <paramref name="grants"/> holds a grant for its id made for this exact
    /// manifest, entry assembly and set of files; the plugin then gets the granted capabilities.
    /// </summary>
    public static Func<PluginConsentRequest, IReadOnlyCollection<string>?> FromGrants(IPluginGrantStore grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return request =>
        {
            if (request?.Manifest is not { } manifest
                || string.IsNullOrEmpty(request.EntryAssemblyHash) || string.IsNullOrEmpty(request.FilesHash))
                return null;
            PluginGrant? grant;
            try { grant = grants.Get(manifest.Id); }
            catch { return null; }   // fail closed
            if (grant is null
                || string.IsNullOrEmpty(grant.ManifestHash)
                || string.IsNullOrEmpty(grant.EntryHash)
                || string.IsNullOrEmpty(grant.FilesHash))
                return null;
            if (!string.Equals(grant.ManifestHash, PluginManifestFingerprint.Compute(manifest), StringComparison.Ordinal)
                || !string.Equals(grant.EntryHash, request.EntryAssemblyHash, StringComparison.Ordinal)
                || !string.Equals(grant.FilesHash, request.FilesHash, StringComparison.Ordinal))
                return null;
            return grant.Capabilities ?? [];
        };
    }
}
