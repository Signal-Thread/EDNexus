using System.IO;

namespace EDNexus.App;

/// <summary>
/// Keeps the per-launch trace logs from piling up: <c>Program.Main</c> starts a fresh
/// <c>ednexus-&lt;timestamp&gt;.log</c> every launch, so without pruning the folder grows forever.
/// </summary>
public static class LogRetention
{
    /// <summary>How many launch logs (the current one included) are kept.</summary>
    public const int DefaultKeep = 10;

    /// <summary>
    /// Delete all but the newest <paramref name="keep"/> files matching <paramref name="pattern"/> in
    /// <paramref name="directory"/>. Best-effort: a log another running instance still holds open, or
    /// one the user has made read-only, is simply left behind. Returns how many files were removed.
    /// </summary>
    public static int Prune(string directory, int keep = DefaultKeep, string pattern = "ednexus-*.log")
    {
        if (keep < 1) keep = 1;
        var removed = 0;
        try
        {
            // The timestamp in the name sorts chronologically, and unlike LastWriteTime it survives
            // being copied or restored from a backup.
            var stale = new DirectoryInfo(directory)
                .EnumerateFiles(pattern)
                .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Skip(keep);

            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use by another instance, or not ours to delete — leave it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder vanished or is unreadable: logging is best-effort, so is its housekeeping.
        }

        return removed;
    }
}
