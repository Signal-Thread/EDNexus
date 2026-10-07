using System.Text.Json;

namespace EDNexus.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON under the per-user app-data folder.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly bool _migrateLegacy;

    // Several owners save the one shared settings object (the app's Apply* calls, the radio's
    // debounced volume write). Serialize them so two writes can't collide on the file.
    private readonly object _writeGate = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        _migrateLegacy = path is null;
    }

    public string Path => _path;

    public static string DefaultPath()
    {
        // Settings live under per-user local app data (LocalAppData\EDNexus on Windows;
        // ~/.local/share/EDNexus on Linux), alongside the logs — deliberately NOT the install
        // directory (read-only for normal users) and NOT Documents, which is commonly
        // cloud-synced (OneDrive Known Folder Move): that would copy the settings — including
        // the Inara API key — off-machine and invite sync conflicts on a frequently
        // rewritten file.
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EDNexus");
        return System.IO.Path.Combine(dir, "settings.json");
    }

    /// <summary>Where settings lived before moving out of Documents (see <see cref="DefaultPath"/>).</summary>
    public static string LegacyDefaultPath()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EDNexus");
        return System.IO.Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        if (_migrateLegacy) TryMigrateLegacyFile(_path, LegacyDefaultPath());

        var settings = LoadExisting(out var safeToPersist) ?? new AppSettings();

        // Assign a stable anonymous id on first load and persist it. Skipped when the existing file
        // couldn't be read at all (as opposed to being absent or corrupt): saving defaults over a file
        // we merely failed to open would destroy it.
        if (string.IsNullOrWhiteSpace(settings.InstallId))
        {
            settings.InstallId = Guid.NewGuid().ToString("N");
            if (safeToPersist) Save(settings);
        }
        return settings;
    }

    private string BackupPath => _path + ".bak";

    /// <summary>
    /// Reads the settings file, never throwing. A file that exists but cannot be parsed is moved aside
    /// to <c>settings.json.corrupt-&lt;timestamp&gt;</c> (so the next save can't overwrite it) and the last
    /// good backup, if any, is used instead. Returns null when nothing usable exists.
    /// </summary>
    /// <param name="safeToPersist">False when the file exists but could not even be opened.</param>
    private AppSettings? LoadExisting(out bool safeToPersist)
    {
        safeToPersist = true;
        if (!File.Exists(_path)) return TryLoadBackup();

        try
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
            if (parsed is not null) return parsed;
            // "null" is valid JSON but not a settings document: treat like any other damage.
            QuarantineDamagedFile("the file held JSON null");
        }
        catch (JsonException ex)
        {
            QuarantineDamagedFile(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable right now (e.g. mid-replace by another instance). Leave the file
            // exactly as it is, and run on the backup or defaults for this session.
            System.Diagnostics.Trace.TraceWarning($"Settings: could not read {_path}: {ex.Message}");
            safeToPersist = false;
        }

        return TryLoadBackup();
    }

    private AppSettings? TryLoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(BackupPath));
            if (restored is not null)
                System.Diagnostics.Trace.TraceWarning($"Settings: restored from backup {BackupPath}");
            return restored;
        }
        catch { return null; }
    }

    /// <summary>Renames an unparseable settings file out of the way, keeping it for recovery.</summary>
    private void QuarantineDamagedFile(string reason)
    {
        try
        {
            var target = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfff}";
            File.Move(_path, target);
            RestrictToOwner(target);
            System.Diagnostics.Trace.TraceError($"Settings: {_path} is unreadable ({reason}); kept a copy at {target} and starting from defaults");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Settings: {_path} is unreadable ({reason}) and could not be set aside: {ex.Message}");
        }
    }

    /// <summary>
    /// One-time move of the settings file from the pre-existing Documents location to
    /// <paramref name="path"/>. Runs only when the store uses the default path and no file exists
    /// there yet. The legacy file (and its folder, when left empty) is removed so the settings
    /// stop syncing to cloud storage. Best-effort: any failure leaves the legacy file in place.
    /// </summary>
    internal static void TryMigrateLegacyFile(string path, string legacyPath)
    {
        try
        {
            if (File.Exists(path) || !File.Exists(legacyPath)) return;

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) CreatePrivateDirectory(dir);

            // Copy via a temp name + rename so a crash mid-copy can't leave a truncated
            // settings.json that would shadow the still-intact legacy file on the next run.
            var tmp = path + ".migrating";
            File.Copy(legacyPath, tmp, overwrite: true);
            RestrictToOwner(tmp); // the Documents copy may have been world-readable
            File.Move(tmp, path);
            File.Delete(legacyPath);

            var legacyDir = System.IO.Path.GetDirectoryName(legacyPath);
            if (!string.IsNullOrEmpty(legacyDir) && Directory.Exists(legacyDir)
                && Directory.GetFileSystemEntries(legacyDir).Length == 0)
                Directory.Delete(legacyDir);
        }
        catch
        {
            // The legacy file survives any failure here, so nothing is lost — at worst the
            // migration retries on the next launch.
        }
    }

    public void Save(AppSettings settings) => TrySave(settings);

    /// <summary>
    /// Like <see cref="Save"/>, but reports whether the write succeeded, so a caller that saves on a
    /// delay can try again rather than silently lose the change. Never throws.
    /// </summary>
    public bool TrySave(AppSettings settings)
    {
        lock (_writeGate)
        {
            string? tmp = null;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) CreatePrivateDirectory(dir);

                // Write the whole document to a sibling temp file, then swap it in, so a crash or power
                // loss mid-write leaves the previous settings intact instead of a truncated file (which
                // would lose the Inara key, the Twitch token and the pending viewer-card cleanups).
                tmp = _path + ".tmp";
                File.Delete(tmp);
                using (var fs = new FileStream(tmp, PrivateWriteOptions()))
                {
                    JsonSerializer.Serialize(fs, settings, Options);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(_path))
                {
                    try
                    {
                        // Keeps exactly one backup: the previous good version.
                        File.Replace(tmp, _path, BackupPath, ignoreMetadataErrors: true);
                        RestrictToOwner(BackupPath);
                    }
                    catch (IOException)
                    {
                        // Replace can be refused (e.g. a scanner briefly holding the file); a plain
                        // overwrite-move is still atomic enough and keeps the save from being lost.
                        File.Move(tmp, _path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tmp, _path);
                }
                tmp = null;
                return true;
            }
            catch
            {
                // Best-effort; a failed save must not crash the app.
                if (tmp is not null) { try { File.Delete(tmp); } catch { } }
                return false;
            }
        }
    }

    // --- Owner-only permissions. settings.json holds secrets (the Inara key, the Twitch EBS token), so on
    // Unix it must not be readable by other accounts, which the default umask would otherwise allow.
    // A no-op on Windows, where the per-user LocalAppData ACL already scopes the folder.

    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static void CreatePrivateDirectory(string dir)
    {
        if (Directory.Exists(dir)) return; // never re-chmod a folder we didn't create (it may be shared)
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, OwnerDirectory);
    }

    private static FileStreamOptions PrivateWriteOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerFile;
        return options;
    }

    private static void RestrictToOwner(string file)
    {
        if (OperatingSystem.IsWindows()) return;
        try { if (File.Exists(file)) File.SetUnixFileMode(file, OwnerFile); } catch { }
    }
}
