namespace EDNexus.Plugins.Abstractions;

/// <summary>
/// Simple key/value persistence scoped to a single plugin, so plugins can save state across
/// sessions without touching the filesystem directly or colliding with the host's own data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keys</b> are 1-100 characters from <c>A-Z a-z 0-9 . _ -</c> and are case-insensitive:
/// <c>Foo</c> and <c>foo</c> are the same key on every platform. Any other key throws
/// <see cref="ArgumentException"/>.
/// </para>
/// <para>
/// <b>Values</b> must be valid Unicode text (a lone surrogate throws <see cref="ArgumentException"/>)
/// of at most 1 MiB (1,048,576 bytes) as UTF-8; larger values throw <see cref="ArgumentException"/>.
/// Each plugin may hold at most 256 keys and 8 MiB in total; going over throws
/// <see cref="IOException"/>.
/// </para>
/// <para>
/// <b>Failures.</b> <see cref="GetString"/> returns <see langword="null"/> only when the key is unset;
/// a value that exists but cannot be read throws <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/>, so a plugin never mistakes a transient failure for
/// "no data" and overwrites it. Writes throw <see cref="IOException"/> on disk or quota errors, and
/// every call throws <see cref="UnauthorizedAccessException"/> if the <c>storage</c> capability was not
/// granted or the plugin has been unloaded.
/// </para>
/// <para>Schema versioning is left to the plugin (store a version key alongside its data).</para>
/// </remarks>
public interface IPluginStorage
{
    /// <summary>Reads a previously saved string value, or <see langword="null"/> if <paramref name="key"/> is unset.</summary>
    string? GetString(string key);

    /// <summary>Saves a string value under <paramref name="key"/>, overwriting any existing value.</summary>
    void SetString(string key, string value);

    /// <summary>Removes any value saved under <paramref name="key"/>. A no-op if none exists.</summary>
    void Remove(string key);
}
