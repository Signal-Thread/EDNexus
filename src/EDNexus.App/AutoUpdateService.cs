using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EDNexus.Core.Updates;

namespace EDNexus.App.Services
{
    /// <summary>
    /// Result of an update check as the UI sees it. <see cref="Found"/> is true only when a verified
    /// installer is on disk (<see cref="Path"/>); every other outcome (up to date, unverifiable, Linux
    /// "update available", failure) has <see cref="Found"/> false and a human-readable <see cref="Message"/>.
    /// </summary>
    public sealed record AutoUpdateResult(bool Found, string Message, string? Path, bool Verified);

    /// <summary>
    /// GitHub Releases-based updater (app-side wrapper over <see cref="UpdateChecker"/>). It only ever
    /// surfaces an installer whose SHA-256 matches the checksum published with the release, downloaded
    /// to a per-user directory; Linux is not self-updated (the package manager / Flatpak owns that).
    /// </summary>
    public static class AutoUpdateService
    {
        private static readonly UpdateChecker Checker = CreateChecker();

        /// <summary>Raised (with the installer path) only after an installer has been downloaded AND verified.</summary>
        public static event Action<string>? UpdateDownloaded;

        /// <summary>Path of the verified installer, or null if none.</summary>
        public static string? LastDownloadedPath { get; private set; }

        /// <summary>SHA-256 (hex) the installer at <see cref="LastDownloadedPath"/> was verified against.</summary>
        public static string? LastVerifiedSha256 { get; private set; }

        /// <summary>Message of the most recent check (e.g. "Update v0.2.0 is available: update through your package manager or Flatpak.").</summary>
        public static string? LastMessage { get; private set; }

        /// <summary>True when a verified installer is ready.</summary>
        public static bool HasUpdate => !string.IsNullOrEmpty(LastDownloadedPath);

        /// <summary>Check for updates. Raises <see cref="UpdateDownloaded"/> only for a verified download.</summary>
        public static async Task<AutoUpdateResult> CheckForUpdatesAsync()
        {
            var res = await Checker.CheckAsync().ConfigureAwait(false);
            LastMessage = res.Message;

            if (res.Status == UpdateStatus.Downloaded && res.Path is not null && res.Sha256 is not null)
            {
                LastDownloadedPath = res.Path;
                LastVerifiedSha256 = res.Sha256;
                UpdateDownloaded?.Invoke(res.Path);
                return new AutoUpdateResult(true, res.Message, res.Path, true);
            }

            // A later check that finds nothing usable (up to date, unverified, failed) may have pruned the
            // earlier installer; drop the stale state so the UI does not offer a file that is gone.
            LastDownloadedPath = null;
            LastVerifiedSha256 = null;
            return new AutoUpdateResult(false, res.Message, null, false);
        }

        private static UpdateChecker CreateChecker()
        {
            var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? UpdatePlatform.Windows
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? UpdatePlatform.MacOS
                : UpdatePlatform.Linux;
            // Per-user, app-specific (not the shared %TEMP% with a predictable name).
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EDNexus", "updates");
            return new UpdateChecker(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, GetCurrentVersion(), platform, dir);
        }

        /// <summary>The running app's own version, as stamped by the release build (<c>-p:Version=</c>).</summary>
        private static SemVer GetCurrentVersion()
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var raw = info ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            return SemVer.ParseOrZero(raw);   // SemVer ignores any "+<gitsha>" build metadata
        }
    }
}
