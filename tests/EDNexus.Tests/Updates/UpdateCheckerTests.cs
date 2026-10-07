using System.Net;
using System.Security.Cryptography;
using System.Text;
using EDNexus.Core.Updates;

namespace EDNexus.Tests.Updates;

public class SemVerTests
{
    [Theory]
    [InlineData("v0.0.12", "0.0.11")]
    [InlineData("0.1.0", "0.1.0-beta")]
    [InlineData("0.1.0-beta.2", "0.1.0-beta.1")]
    [InlineData("0.1.0-beta.10", "0.1.0-beta.9")]
    [InlineData("0.1.0-beta", "0.1.0-alpha")]
    [InlineData("0.1.0-alpha.1", "0.1.0-alpha")]
    [InlineData("0.1.0-alpha", "0.1.0-1")]
    [InlineData("1.0", "0.9.9")]
    [InlineData("0.1.0", "0.0.9")]
    public void Orders_Correctly(string greater, string lesser)
    {
        Assert.True(SemVer.TryParse(greater, out var g));
        Assert.True(SemVer.TryParse(lesser, out var l));
        Assert.True(g > l);
        Assert.True(l < g);
    }

    [Fact]
    public void Prerelease_IsNotTreatedAsZero()
    {
        // Regression: "0.1.0-beta" used to parse to 0.0.0, so a beta build always looked outdated.
        Assert.True(SemVer.TryParse("0.1.0-beta", out var beta));
        Assert.True(beta > SemVer.ParseOrZero("0.0.12"));
        Assert.True(beta.IsPrerelease);
    }

    [Fact]
    public void BuildMetadata_IsIgnored()
        => Assert.Equal(SemVer.ParseOrZero("1.2.3"), SemVer.ParseOrZero("1.2.3+abcdef"));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.x")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3-")]
    public void Garbage_DoesNotParse(string raw) => Assert.False(SemVer.TryParse(raw, out _));
}

public sealed class UpdateCheckerTests : IDisposable
{
    private const string Repo = "Signal-Thread-LLC/EDNexus";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ednexus-update-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string AssetUrl(string tag, string name) => $"https://github.com/{Repo}/releases/download/{tag}/{name}";

    /// <summary>Scripted GitHub: serves <c>/releases/latest</c> JSON and any registered asset URLs.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            // Redirect tracking: expose the final URL like HttpClient would after following redirects.
            var key = request.RequestUri!.GetLeftPart(UriPartial.Path);
            var resp = Routes.TryGetValue(key, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound);
            resp.RequestMessage ??= request;
            return Task.FromResult(resp);
        }

        public void Json(string tag, params (string name, string url)[] assets)
        {
            var arr = string.Join(",", assets.Select(a => $"{{\"name\":\"{a.name}\",\"browser_download_url\":\"{a.url}\"}}"));
            var json = $"{{\"tag_name\":\"{tag}\",\"assets\":[{arr}]}}";
            Routes[$"https://api.github.com/repos/{Repo}/releases/latest"] =
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        public void Bytes(string url, byte[] data, string? contentType = null)
            => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

        public int Hits(string url) => Requests.Count(r => r.RequestUri!.GetLeftPart(UriPartial.Path).Equals(url, StringComparison.OrdinalIgnoreCase));
    }

    private UpdateChecker Make(FakeGitHub gh, string current = "0.1.0", UpdatePlatform platform = UpdatePlatform.Windows, long max = 1024 * 1024)
        => new(new HttpClient(gh), SemVer.ParseOrZero(current), platform, _dir, Repo, TimeSpan.Zero, max);

    private (FakeGitHub gh, byte[] installer, string exeUrl) ReleaseWith(string tag, string sidecarName, Func<byte[], string> sidecarBody, byte[]? served = null)
    {
        var ver = tag.TrimStart('v');
        var exeName = $"EDNexus-{ver}-setup.exe";
        var installer = Encoding.UTF8.GetBytes("MZ pretend installer " + tag);
        var gh = new FakeGitHub();
        var exeUrl = AssetUrl(tag, exeName);
        var sumUrl = AssetUrl(tag, sidecarName.Replace("{exe}", exeName));
        gh.Json(tag, (exeName, exeUrl), (sidecarName.Replace("{exe}", exeName), sumUrl));
        gh.Bytes(exeUrl, served ?? installer);
        gh.Bytes(sumUrl, Encoding.UTF8.GetBytes(sidecarBody(installer)));
        return (gh, installer, exeUrl);
    }

    [Fact]
    public async Task MatchingChecksum_DownloadsAndVerifies_EvenWithMixedCaseSidecarName()
    {
        // The old code lowercased one side of the sidecar-name compare, so this never matched.
        var (gh, installer, _) = ReleaseWith("v0.2.0", "{exe}.SHA256", b => Sha(b));
        var res = await Make(gh).CheckAsync();

        Assert.Equal(UpdateStatus.Downloaded, res.Status);
        Assert.Equal(Sha(installer), res.Sha256);
        Assert.NotNull(res.Path);
        Assert.StartsWith(_dir, res.Path);
        Assert.Equal(installer, await File.ReadAllBytesAsync(res.Path!));
    }

    [Fact]
    public async Task SidecarWithFilenameColumn_IsAccepted()
    {
        var (gh, _, _) = ReleaseWith("v0.2.0", "{exe}.sha256", b => Sha(b).ToUpperInvariant() + "  *EDNexus-0.2.0-setup.exe\n");
        Assert.Equal(UpdateStatus.Downloaded, (await Make(gh).CheckAsync()).Status);
    }

    [Fact]
    public async Task ChecksumMismatch_IsRejected_AndFileDeleted()
    {
        var (gh, _, _) = ReleaseWith("v0.2.0", "{exe}.sha256", b => Sha(b), served: Encoding.UTF8.GetBytes("tampered"));
        var res = await Make(gh).CheckAsync();

        Assert.Equal(UpdateStatus.Unverified, res.Status);
        Assert.Null(res.Path);
        Assert.Contains("could not be verified", res.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task MissingSidecar_IsNotDownloadedOrOffered()
    {
        var gh = new FakeGitHub();
        var exeUrl = AssetUrl("v0.2.0", "EDNexus-0.2.0-setup.exe");
        gh.Json("v0.2.0", ("EDNexus-0.2.0-setup.exe", exeUrl));
        gh.Bytes(exeUrl, new byte[] { 1, 2, 3 });

        var res = await Make(gh).CheckAsync();

        Assert.Equal(UpdateStatus.Unverified, res.Status);
        Assert.Null(res.Path);
        Assert.Equal(0, gh.Hits(exeUrl));
        Assert.Contains("could not be verified", res.Message);
    }

    [Fact]
    public async Task MalformedOrForeignChecksum_IsRejected()
    {
        var (gh, _, _) = ReleaseWith("v0.2.0", "{exe}.sha256", _ => "not-a-hash");
        Assert.Equal(UpdateStatus.Unverified, (await Make(gh).CheckAsync()).Status);

        var (gh2, _, _) = ReleaseWith("v0.2.0", "{exe}.sha256", b => Sha(b) + "  some-other-file.exe");
        Assert.Equal(UpdateStatus.Unverified, (await Make(gh2).CheckAsync()).Status);
    }

    [Fact]
    public async Task UpToDate_DoesNotDownload()
    {
        var (gh, _, exeUrl) = ReleaseWith("v0.2.0", "{exe}.sha256", b => Sha(b));
        var res = await Make(gh, current: "0.2.0").CheckAsync();

        Assert.Equal(UpdateStatus.UpToDate, res.Status);
        Assert.Equal(0, gh.Hits(exeUrl));
    }

    [Fact]
    public async Task BetaBuild_IsOutdatedOnlyByAHigherRelease()
    {
        var (gh, _, _) = ReleaseWith("v0.1.0", "{exe}.sha256", b => Sha(b));
        Assert.Equal(UpdateStatus.Downloaded, (await Make(gh, current: "0.1.0-beta").CheckAsync()).Status);

        var (gh2, _, _) = ReleaseWith("v0.1.0-beta", "{exe}.sha256", b => Sha(b));
        Assert.Equal(UpdateStatus.UpToDate, (await Make(gh2, current: "0.1.0").CheckAsync()).Status);
    }

    [Fact]
    public async Task Linux_ReportsAvailability_WithoutDownloading()
    {
        var (gh, _, exeUrl) = ReleaseWith("v0.2.0", "{exe}.sha256", b => Sha(b));
        var res = await Make(gh, platform: UpdatePlatform.Linux).CheckAsync();

        Assert.Equal(UpdateStatus.AvailableManualInstall, res.Status);
        Assert.Contains("Flatpak", res.Message);
        Assert.Equal(0, gh.Hits(exeUrl));
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task VerifiedDownload_IsReusedOnNextCheck()
    {
        var (gh, _, exeUrl) = ReleaseWith("v0.2.0", "{exe}.sha256", b => Sha(b));
        var checker = Make(gh);
        Assert.Equal(UpdateStatus.Downloaded, (await checker.CheckAsync()).Status);
        Assert.Equal(UpdateStatus.Downloaded, (await checker.CheckAsync()).Status);
        Assert.Equal(1, gh.Hits(exeUrl));
    }

    [Fact]
    public async Task OversizeInstaller_IsRefused()
    {
        var (gh, _, _) = ReleaseWith("v0.2.0", "{exe}.sha256", b => Sha(b));
        var res = await Make(gh, max: 4).CheckAsync();

        Assert.Equal(UpdateStatus.Failed, res.Status);
        Assert.Null(res.Path);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Requests_NeverCarryAuthorization_AndUserAgentDoesNotGrow()
    {
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "stale-token");
        try
        {
            var (gh, _, _) = ReleaseWith("v0.2.0", "{exe}.sha256", b => Sha(b));
            var checker = Make(gh);
            await checker.CheckAsync();
            await checker.CheckAsync();

            Assert.All(gh.Requests, r => Assert.Null(r.Headers.Authorization));
            Assert.All(gh.Requests, r => Assert.Single(r.Headers.UserAgent));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        }
    }

    [Fact]
    public async Task AssetsFromOtherHosts_AreIgnored()
    {
        var gh = new FakeGitHub();
        gh.Json("v0.2.0", ("EDNexus-0.2.0-setup.exe", "https://evil.example/Signal-Thread-LLC/EDNexus/releases/download/v0.2.0/EDNexus-0.2.0-setup.exe"));
        var res = await Make(gh).CheckAsync();
        Assert.Equal(UpdateStatus.NoInstaller, res.Status);
    }

    [Fact]
    public async Task Api404_FallsBackToReleasesPage_WithHtmlDecodedHrefs()
    {
        var gh = new FakeGitHub();
        var installer = Encoding.UTF8.GetBytes("fallback installer");
        var exeUrl = AssetUrl("v0.2.0", "EDNexus-0.2.0-setup.exe");
        var sumUrl = exeUrl + ".sha256";
        gh.Routes[$"https://github.com/{Repo}/releases/latest"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{Repo}/releases/tag/v0.2.0"),
            Content = new StringContent(
                $"<a href=\"/{Repo}/releases/download/v0.2.0/EDNexus-0.2.0-setup.exe?x=1&amp;y=2\">exe</a>" +
                $"<a href=\"/{Repo}/releases/download/v0.2.0/EDNexus-0.2.0-setup.exe.sha256\">sum</a>" +
                "<a href=\"https://evil.example/x/releases/download/v0.2.0/EDNexus-9-setup.exe\">evil</a>" +
                $"<a href=\"/{Repo}/releases/download/v0.1.0/EDNexus-0.1.0-setup.exe\">old tag</a>"),
        };
        gh.Bytes(exeUrl, installer);
        gh.Bytes(sumUrl, Encoding.UTF8.GetBytes(Sha(installer)));

        var res = await Make(gh).CheckAsync();

        Assert.Equal(UpdateStatus.Downloaded, res.Status);
        Assert.Equal("EDNexus-0.2.0-setup.exe", Path.GetFileName(res.Path));
    }

    [Fact]
    public void ParseChecksum_Rules()
    {
        var h = new string('a', 64);
        Assert.Equal(h, UpdateChecker.ParseChecksum(h + "\n", "x.exe"));
        Assert.Equal(h, UpdateChecker.ParseChecksum($"{h}  x.exe", "x.exe"));
        Assert.Null(UpdateChecker.ParseChecksum($"{h}  y.exe", "x.exe"));
        Assert.Null(UpdateChecker.ParseChecksum("abc", "x.exe"));
        Assert.Null(UpdateChecker.ParseChecksum("", "x.exe"));
    }
}
