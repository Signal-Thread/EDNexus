using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EDNexus.Core.Trade;

/// <summary>A keyed store of raw responses with a time-to-live, so repeat queries skip the network.</summary>
public interface IResponseCache
{
    /// <summary>Return the cached body for <paramref name="key"/> if present and not expired, else null.</summary>
    string? Get(string key);

    /// <summary>Store <paramref name="body"/> under <paramref name="key"/>, stamped at the current time.</summary>
    void Put(string key, string body);

    /// <summary>
    /// Return the cached body for <paramref name="key"/> even if its TTL has expired, or null when
    /// there is no entry at all. A last resort for sources that should keep showing something rather
    /// than nothing when the live fetch fails and the ordinary TTL-bound <see cref="Get"/> has already
    /// aged the entry out — e.g. Galnet news while offline. Caches that never remember beyond their TTL
    /// (or that have no on-disk backing, like an in-memory test double) can leave this at the default,
    /// which simply reports no stale value.
    /// </summary>
    string? GetStale(string key) => null;

    /// <summary>
    /// Discard the entry for <paramref name="key"/>, if any — used when a cached body turns out to be
    /// unusable (e.g. written by an older build with a different shape). The default does nothing.
    /// </summary>
    void Remove(string key) { }
}

/// <summary>Helpers for reading typed values out of an <see cref="IResponseCache"/>.</summary>
public static class ResponseCacheExtensions
{
    /// <summary>
    /// Reads <paramref name="key"/> and deserialises it as <typeparamref name="T"/>. The cache is
    /// best-effort, so anything that cannot yield a value — a miss, a body from an older shape that no
    /// longer deserialises, or a JSON <c>null</c> — is a miss: the unusable entry is removed and
    /// <c>null</c> returned so the caller refetches, instead of throwing on every lookup until the
    /// file is deleted by hand.
    /// </summary>
    public static T? GetTyped<T>(this IResponseCache? cache, string key, JsonSerializerOptions options) where T : class
    {
        if (cache is null) return null;

        string? body;
        try { body = cache.Get(key); }
        catch { return null; }
        if (body is null) return null;

        try
        {
            if (JsonSerializer.Deserialize<T>(body, options) is { } value) return value;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // fall through: treat as a miss
        }

        try { cache.Remove(key); } catch { /* best effort */ }
        return null;
    }
}

/// <summary>
/// An on-disk <see cref="IResponseCache"/>: one file per key (named by a hash of the key) holding a
/// small JSON envelope of when it was cached plus the body. Entries older than the TTL are treated
/// as misses. The clock is injectable so expiry is deterministic under test.
/// </summary>
/// <remarks>
/// The cache is strictly best-effort: it never throws for I/O problems (unwritable or missing folder,
/// locked or corrupt files), writes entries atomically (temp file then move) so a crash cannot leave a
/// half-written file, and bounds its disk use by deleting entries older than a retention window and
/// the oldest beyond a maximum count — on construction and every so often while writing.
/// </remarks>
public sealed class DiskResponseCache : IResponseCache
{
    private const int TrimEveryPuts = 50;

    private readonly string _dir;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _retention;
    private readonly int _maxEntries;
    private int _putsSinceTrim;

    /// <param name="directory">Where entries are stored; created on demand.</param>
    /// <param name="ttl">How long an entry counts as fresh for <see cref="Get"/>.</param>
    /// <param name="now">Clock, injectable for tests.</param>
    /// <param name="retention">
    /// How long an entry is kept on disk at all (it may still serve <see cref="GetStale"/> after its TTL).
    /// Defaults to twice the TTL, but at least 30 days.
    /// </param>
    /// <param name="maxEntries">The most entries kept; the oldest beyond this are deleted.</param>
    public DiskResponseCache(string directory, TimeSpan ttl, Func<DateTimeOffset>? now = null,
        TimeSpan? retention = null, int maxEntries = 2000)
    {
        _dir = directory;
        _ttl = ttl;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        var twiceTtl = ttl + ttl;
        _retention = retention ?? (twiceTtl > TimeSpan.FromDays(30) ? twiceTtl : TimeSpan.FromDays(30));
        _maxEntries = Math.Max(1, maxEntries);

        // A cache folder we cannot create must not take the engine down; Put retries and swallows.
        TryCreateDirectory();
        Trim();
    }

    public string? Get(string key)
    {
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String || !at.TryGetDateTimeOffset(out var cachedAt))
            {
                Discard(path);
                return null;
            }
            if (_now() - cachedAt >= _ttl) return null;
            return ReadBody(root);
        }
        catch (Exception ex) when (IsBestEffort(ex))
        {
            if (ex is JsonException) Discard(path);   // a corrupt cache file is just a miss
            return null;
        }
    }

    public void Put(string key, string body)
    {
        var path = PathFor(key);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            TryCreateDirectory();
            File.WriteAllText(temp, JsonSerializer.Serialize(new { at = _now(), body }));
            File.Move(temp, path, overwrite: true);   // atomic replace: readers never see a partial file
        }
        catch (Exception ex) when (IsBestEffort(ex))
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            return;
        }

        if (Interlocked.Increment(ref _putsSinceTrim) >= TrimEveryPuts)
        {
            Interlocked.Exchange(ref _putsSinceTrim, 0);
            Trim();
        }
    }

    public string? GetStale(string key)
    {
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ReadBody(doc.RootElement) : null;
        }
        catch (Exception ex) when (IsBestEffort(ex))
        {
            if (ex is JsonException) Discard(path);   // a corrupt cache file is just a miss
            return null;
        }
    }

    public void Remove(string key) => Discard(PathFor(key));

    // The body must be a JSON string; anything else (a hand-edited or foreign file) is a miss, not a throw.
    private static string? ReadBody(JsonElement root)
        => root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null;

    private static bool IsBestEffort(Exception ex)
        => ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException;

    private void TryCreateDirectory()
    {
        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) when (IsBestEffort(ex)) { /* unavailable: every operation degrades to a miss */ }
    }

    private static void Discard(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (IsBestEffort(ex)) { /* best effort */ }
    }

    /// <summary>Deletes expired entries and orphaned temp files, then the oldest entries beyond the cap.</summary>
    private void Trim()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;

            var cutoff = _now().UtcDateTime - _retention;
            var tempCutoff = _now().UtcDateTime - TimeSpan.FromHours(1);
            var live = new List<(string Path, DateTime Written)>();

            foreach (var file in new DirectoryInfo(_dir).EnumerateFiles())
            {
                try
                {
                    var written = file.LastWriteTimeUtc;
                    if (file.Name.EndsWith(".tmp", StringComparison.Ordinal))
                    {
                        if (written < tempCutoff) file.Delete();
                    }
                    else if (file.Name.EndsWith(".json", StringComparison.Ordinal))
                    {
                        if (written < cutoff) file.Delete();
                        else live.Add((file.FullName, written));
                    }
                }
                catch (Exception ex) when (IsBestEffort(ex)) { /* in use or already gone */ }
            }

            foreach (var old in live.OrderBy(f => f.Written).Take(Math.Max(0, live.Count - _maxEntries)))
                Discard(old.Path);
        }
        catch (Exception ex) when (IsBestEffort(ex))
        {
            // Housekeeping only: never fail because of it.
        }
    }

    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_dir, hash + ".json");
    }
}
