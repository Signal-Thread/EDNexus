using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EDNexus.Core.Settings;

namespace EDNexus.Core.Updates;

/// <summary>Operating system family the updater is running on; only Windows self-installs.</summary>
public enum UpdatePlatform { Windows, MacOS, Linux }

/// <summary>Outcome class of an update check.</summary>
public enum UpdateStatus
{
    /// <summary>The running version is the latest (or newer).</summary>
    UpToDate,
    /// <summary>A verified installer was downloaded and is ready to run.</summary>
    Downloaded,
    /// <summary>A newer release exists but this platform updates through its package manager / Flatpak.</summary>
    AvailableManualInstall,
    /// <summary>A newer release exists but its download could not be verified, so nothing is offered for install.</summary>
    Unverified,
    /// <summary>A newer release exists but publishes no installer for this platform.</summary>
    NoInstaller,
    /// <summary>The check itself failed (network, API, malformed data).</summary>
    Failed,
}

/// <summary>Result of <see cref="UpdateChecker.CheckAsync"/>. <see cref="Path"/>/<see cref="Sha256"/> are set only for <see cref="UpdateStatus.Downloaded"/>.</summary>
public sealed record UpdateCheckResult(UpdateStatus Status, string Message, string? Version = null, string? Path = null, string? Sha256 = null);

/// <summary>One downloadable file attached to a release.</summary>
internal sealed record ReleaseAsset(string Name, Uri Url);

/// <summary>
/// GitHub Releases updater core: finds the latest release, and (on Windows only) downloads the
/// installer into a per-user directory and offers it only if it matches the release's published
/// <c>.sha256</c> sidecar.
/// <para>
/// <b>Trust model.</b> The checksum is published in the same release as the installer, so it proves the
/// download was not corrupted or swapped in transit/CDN (integrity), but NOT who built it (authenticity):
/// anyone able to edit the release can replace both files. Authenticode-signing the installer (and having
/// the updater require a trusted publisher) is what would provide authenticity.
/// </para>
/// Instance-based with an injectable <see cref="HttpClient"/> so it is unit-testable; the app uses one
/// shared instance (see <c>AutoUpdateService</c>).
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>Default upper bound on an installer download (the self-contained build is ~100 MB).</summary>
    public const long DefaultMaxInstallerBytes = 500L * 1024 * 1024;

    private const long MaxChecksumBytes = 4 * 1024;
    private const long MaxMetadataBytes = 8L * 1024 * 1024;
    private static readonly Uri GitHubBase = new("https://github.com");
    private static readonly Regex InstallerName = new(@"^EDNexus-[0-9A-Za-z._+\-]+-setup\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Hex = new(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly string[] ChecksumSuffixes = { ".sha256", ".sha256sum", ".sha256.txt" };

    private readonly HttpClient _http;
    private readonly SemVer _current;
    private readonly UpdatePlatform _platform;
    private readonly string _downloadDir;
    private readonly string _repo;
    private readonly TimeSpan _retryDelay;
    private readonly long _maxInstallerBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="http">Client used for every request (headers are set per request, never on the client).</param>
    /// <param name="current">The running app's version.</param>
    /// <param name="platform">Platform to pick an installer for.</param>
    /// <param name="downloadDir">Per-user, app-specific directory to download into.</param>
    /// <param name="repo"><c>owner/name</c> of the GitHub repository.</param>
    /// <param name="retryDelay">Pause before the single retry of a transient API failure.</param>
    /// <param name="maxInstallerBytes">Refuse installers larger than this.</param>
    public UpdateChecker(HttpClient http, SemVer current, UpdatePlatform platform, string downloadDir,
        string repo = "Signal-Thread/EDNexus", TimeSpan? retryDelay = null, long maxInstallerBytes = DefaultMaxInstallerBytes)
    {
        _http = http;
        _current = current;
        _platform = platform;
        _downloadDir = downloadDir;
        _repo = repo;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        _maxInstallerBytes = maxInstallerBytes;
    }

    /// <summary>
    /// Checks for a newer release and (Windows) downloads and verifies its installer. Concurrent calls are
    /// serialised so two checks never write the same file.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CheckCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Trace.TraceWarning($"AutoUpdate check failed: {ex}");
            return new UpdateCheckResult(UpdateStatus.Failed, $"Update check failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken ct)
    {
        var release = await FetchLatestReleaseAsync(ct).ConfigureAwait(false);
        if (release.Error is not null)
            return new UpdateCheckResult(UpdateStatus.Failed, release.Error);

        var tag = release.Tag!;
        if (!SemVer.TryParse(tag, out var latest))
            return new UpdateCheckResult(UpdateStatus.Failed, $"Unrecognised release version '{tag}'");

        if (!(latest > _current))
        {
            Trace.TraceInformation($"AutoUpdate: already up to date (current={_current}, latest={latest}) - skipping download.");
            return new UpdateCheckResult(UpdateStatus.UpToDate, $"Already up to date ({tag})", tag);
        }

        if (_platform != UpdatePlatform.Windows)
        {
            // Linux builds ship as a Flatpak / tarball and macOS has no published installer: updating is the
            // package manager's (or the user's) job, so we only report that something newer exists.
            var how = _platform == UpdatePlatform.Linux
                ? "update through your package manager or Flatpak"
                : "download it from the GitHub releases page";
            return new UpdateCheckResult(UpdateStatus.AvailableManualInstall, $"Update {tag} is available: {how}.", tag);
        }

        var installer = release.Assets.FirstOrDefault(a => InstallerName.IsMatch(a.Name));
        if (installer is null)
            return new UpdateCheckResult(UpdateStatus.NoInstaller, $"Update {tag} is available but has no Windows installer", tag);

        var sidecar = release.Assets.FirstOrDefault(a => ChecksumSuffixes.Any(s =>
            string.Equals(a.Name, installer.Name + s, StringComparison.OrdinalIgnoreCase)));
        if (sidecar is null)
            return Unverified(tag, "no checksum was published with the release");

        string? expected;
        try
        {
            var bytes = await DownloadSmallAsync(sidecar.Url, MaxChecksumBytes, ct).ConfigureAwait(false);
            expected = bytes is null ? null : ParseChecksum(System.Text.Encoding.UTF8.GetString(bytes), installer.Name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Trace.TraceWarning($"AutoUpdate: checksum download failed: {ex.Message}");
            expected = null;
        }
        if (expected is null)
            return Unverified(tag, "its checksum could not be read");

        Directory.CreateDirectory(_downloadDir);
        var dest = Path.Combine(_downloadDir, installer.Name);
        PruneDownloadDir(keep: installer.Name);

        // Reuse an earlier verified download instead of fetching ~100 MB on every launch.
        if (File.Exists(dest) && HashMatches(dest, expected))
        {
            Trace.TraceInformation($"AutoUpdate: reusing verified download {dest}");
            return new UpdateCheckResult(UpdateStatus.Downloaded, $"Update {tag} downloaded and verified", tag, dest, expected.ToLowerInvariant());
        }
        TryDelete(dest);

        var partial = dest + ".partial";
        try
        {
            var ok = await DownloadToFileAsync(installer.Url, partial, ct).ConfigureAwait(false);
            if (!ok)
            {
                TryDelete(partial);
                return new UpdateCheckResult(UpdateStatus.Failed, $"Update {tag} could not be downloaded", tag);
            }
            if (!HashMatches(partial, expected))
            {
                TryDelete(partial);
                Trace.TraceWarning($"AutoUpdate: checksum mismatch for {installer.Name}; download discarded");
                return Unverified(tag, "the download did not match its published checksum");
            }
            File.Move(partial, dest, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        Trace.TraceInformation($"EDNexus update downloaded and verified: {dest}");
        return new UpdateCheckResult(UpdateStatus.Downloaded, $"Update {tag} downloaded and verified", tag, dest, expected.ToLowerInvariant());
    }

    /// <summary>True when the file at <paramref name="path"/> still has SHA-256 <paramref name="expectedHex"/>.</summary>
    public static bool HashMatches(string path, string expectedHex)
    {
        try
        {
            return string.Equals(Hashing.ComputeSha256Hex(path), expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static UpdateCheckResult Unverified(string tag, string reason)
        => new(UpdateStatus.Unverified, $"Update {tag} could not be verified ({reason}). It was not installed.", tag);

    /// <summary>
    /// Reads a <c>sha256sum</c>-style sidecar (<c>hash</c> or <c>hash  [*]file</c>) and returns the
    /// hash, or null if it is malformed or names a different file.
    /// </summary>
    internal static string? ParseChecksum(string text, string installerName)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 or > 2) return null;
        if (!Sha256Hex.IsMatch(tokens[0])) return null;
        if (tokens.Length == 2)
        {
            var named = tokens[1].TrimStart('*');
            if (!string.Equals(Path.GetFileName(named), installerName, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return tokens[0];
    }

    // ---- release discovery ------------------------------------------------------------------

    private sealed record ReleaseInfo(string? Tag, IReadOnlyList<ReleaseAsset> Assets, string? Error);

    private static ReleaseInfo Fail(string error) => new(null, Array.Empty<ReleaseAsset>(), error);

    private async Task<ReleaseInfo> FetchLatestReleaseAsync(CancellationToken ct)
    {
        var apiUrl = new Uri($"https://api.github.com/repos/{_repo}/releases/latest");
        // Anonymous only: the repo is public, and a stale ambient GITHUB_TOKEN would 401 (and leak to GitHub for nothing).
        var resp = await GetAsync(apiUrl, json: true, ct).ConfigureAwait(false);
        Trace.TraceInformation($"AutoUpdate: API response {(int)resp.StatusCode} {resp.StatusCode}");

        // A transient failure (5xx) gets one retry before we give up.
        if (!resp.IsSuccessStatusCode && !IsFallbackStatus(resp.StatusCode))
        {
            resp.Dispose();
            await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
            resp = await GetAsync(apiUrl, json: true, ct).ConfigureAwait(false);
            Trace.TraceInformation($"AutoUpdate: retry response {(int)resp.StatusCode} {resp.StatusCode}");
        }

        using (resp)
        {
            if (resp.IsSuccessStatusCode)
                return await ParseApiReleaseAsync(resp, ct).ConfigureAwait(false);

            if (IsFallbackStatus(resp.StatusCode))
            {
                Trace.TraceWarning($"AutoUpdate: API returned {(int)resp.StatusCode}; falling back to HTML parsing of the releases page.");
                return await FetchFromHtmlAsync(ct).ConfigureAwait(false);
            }
            return Fail($"Update server returned {(int)resp.StatusCode} {resp.StatusCode}");
        }
    }

    // 404 (restricted API), 403/429 (rate limited): the public releases page still works.
    private static bool IsFallbackStatus(HttpStatusCode code)
        => code is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    private async Task<ReleaseInfo> ParseApiReleaseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var bytes = await ReadCappedAsync(resp, MaxMetadataBytes, ct).ConfigureAwait(false);
        if (bytes is null) return Fail("Release metadata was too large");

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
            return Fail("Release has no tag");

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                if (!a.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) continue;
                if (!a.TryGetProperty("browser_download_url", out var u) || u.GetString() is not { } url) continue;
                if (!TryValidateAssetUrl(url, tag, out var uri)) continue;
                assets.Add(new ReleaseAsset(name, uri));
            }
        }
        return new ReleaseInfo(tag, assets, null);
    }

    private async Task<ReleaseInfo> FetchFromHtmlAsync(CancellationToken ct)
    {
        using var page = await GetAsync(new Uri($"https://github.com/{_repo}/releases/latest"), json: false, ct).ConfigureAwait(false);
        Trace.TraceInformation($"AutoUpdate: HTML page response {(int)page.StatusCode} {page.StatusCode}");
        if (!page.IsSuccessStatusCode)
            return Fail($"Releases page returned {(int)page.StatusCode} {page.StatusCode}");

        // "releases/latest" redirects to "releases/tag/vX.Y.Z", the only place the fallback can read the version from.
        var tag = ExtractTagFromUrl(page.RequestMessage?.RequestUri?.ToString());
        if (tag is null)
            return Fail("Could not determine the latest release version");

        var bytes = await ReadCappedAsync(page, MaxMetadataBytes, ct).ConfigureAwait(false);
        if (bytes is null) return Fail("Releases page was too large");
        var html = System.Text.Encoding.UTF8.GetString(bytes);
        return new ReleaseInfo(tag, ParseHtmlAssets(html, tag), null);
    }

    /// <summary>Pulls "v0.0.12" out of a resolved ".../releases/tag/v0.0.12" URL, or null if it doesn't match.</summary>
    internal static string? ExtractTagFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = Regex.Match(url, @"/releases/tag/([^/?#]+)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// Extracts release-asset links from the releases page: each href is HTML-decoded, resolved against
    /// github.com, and kept only if it is an https github.com download URL for this repo and <paramref name="tag"/>.
    /// The query string is dropped (it is a signature GitHub re-issues on redirect, not part of the file name).
    /// </summary>
    internal List<ReleaseAsset> ParseHtmlAssets(string html, string tag)
    {
        var result = new List<ReleaseAsset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(html, "href=\"([^\"]+/releases/download/[^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value);
            if (!TryValidateAssetUrl(href, tag, out var uri)) continue;
            var name = Uri.UnescapeDataString(uri.Segments[^1]);
            if (name.Length == 0 || name != Path.GetFileName(name)) continue;
            if (seen.Add(name)) result.Add(new ReleaseAsset(name, uri));
        }
        return result;
    }

    /// <summary>Accepts only https://github.com/{repo}/releases/download/{tag}/{file}; strips query/fragment.</summary>
    private bool TryValidateAssetUrl(string raw, string tag, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(GitHubBase, raw, out var resolved)) return false;
        if (resolved.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.Equals(resolved.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = $"/{_repo}/releases/download/{tag}/";
        var path = Uri.UnescapeDataString(resolved.AbsolutePath);
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.IndexOf('/', prefix.Length) >= 0) return false;
        uri = new Uri(resolved.GetLeftPart(UriPartial.Path));
        return true;
    }

    // ---- HTTP helpers -----------------------------------------------------------------------

    private async Task<HttpResponseMessage> GetAsync(Uri url, bool json, CancellationToken ct)
    {
        // Per-request headers: never mutate the shared client (the old code grew its User-Agent on every check).
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("EDNexus-Updater");
        if (json) req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private async Task<byte[]?> DownloadSmallAsync(Uri url, long max, CancellationToken ct)
    {
        using var resp = await GetAsync(url, json: false, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await ReadCappedAsync(resp, max, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the body, or returns null if it exceeds <paramref name="max"/> bytes.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage resp, long max, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is { } len && len > max) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > max) return null;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private async Task<bool> DownloadToFileAsync(Uri url, string path, CancellationToken ct)
    {
        using var resp = await GetAsync(url, json: false, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Trace.TraceWarning($"AutoUpdate download failed ({resp.StatusCode}) for {url}");
            return false;
        }
        if (resp.Content.Headers.ContentLength is { } len && len > _maxInstallerBytes)
        {
            Trace.TraceWarning($"AutoUpdate: refusing {len}-byte download (cap {_maxInstallerBytes})");
            return false;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > _maxInstallerBytes)
            {
                Trace.TraceWarning($"AutoUpdate: download exceeded the {_maxInstallerBytes}-byte cap; aborted");
                return false;
            }
            await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
        }
        return true;
    }

    // ---- file helpers -----------------------------------------------------------------------

    /// <summary>Removes leftovers (older installers, interrupted .partial files) other than <paramref name="keep"/>.</summary>
    private void PruneDownloadDir(string keep)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_downloadDir))
                if (!string.Equals(Path.GetFileName(f), keep, StringComparison.OrdinalIgnoreCase))
                    TryDelete(f);
        }
        catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
