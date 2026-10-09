using EDNexus.Core.Settings;
using Xunit;

namespace EDNexus.Tests.Settings;

public class SettingsStorePersistenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ednexus-settings-").FullName;

    private string Dir => Path.Combine(_root, "EDNexus");
    private string SettingsPath => Path.Combine(Dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private SettingsStore NewStore() => new(SettingsPath);

    private string[] Corrupt() => Directory.GetFiles(Dir, "settings.json.corrupt-*");

    // --- Atomic save + backup (COR-2). ---

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = NewStore();
        var settings = store.Load();
        settings.Reporting.Inara.ApiKey = "secret-key";
        store.Save(settings);

        Assert.Equal("secret-key", NewStore().Load().Reporting.Inara.ApiKey);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = NewStore();
        store.Save(store.Load());
        store.Save(store.Load());

        Assert.Equal(new[] { "settings.json", "settings.json.bak" },
            Directory.GetFiles(Dir).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void The_previous_version_is_kept_as_a_single_backup()
    {
        var store = NewStore();
        var settings = store.Load();
        settings.Reporting.Inara.ApiKey = "first";
        store.Save(settings);
        settings.Reporting.Inara.ApiKey = "second";
        store.Save(settings);

        Assert.Contains("first", File.ReadAllText(SettingsPath + ".bak"));
        Assert.Contains("second", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_leftover_temp_file_from_a_crashed_save_does_not_block_saving()
    {
        var store = NewStore();
        var settings = store.Load();
        File.WriteAllText(SettingsPath + ".tmp", "half-writ");   // as if the process died mid-write

        settings.Reporting.Inara.ApiKey = "after-crash";
        Assert.True(store.TrySave(settings));

        Assert.Equal("after-crash", NewStore().Load().Reporting.Inara.ApiKey);
    }

    [Fact]
    public void A_corrupt_file_is_set_aside_not_overwritten_and_defaults_are_used()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SettingsPath, """{ "Reporting": { "Inara": { "ApiKey": "precious" """);   // truncated

        var loaded = NewStore().Load();

        Assert.False(string.IsNullOrWhiteSpace(loaded.InstallId));
        var kept = Assert.Single(Corrupt());
        Assert.Contains("precious", File.ReadAllText(kept));   // recoverable by hand
        Assert.Contains(loaded.InstallId, File.ReadAllText(SettingsPath)); // fresh defaults saved
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_the_last_good_backup()
    {
        var store = NewStore();
        var settings = store.Load();
        settings.Reporting.Inara.ApiKey = "good";
        store.Save(settings);
        store.Save(settings);                       // now .bak holds a complete copy
        File.WriteAllText(SettingsPath, "{ truncated");

        var loaded = NewStore().Load();

        Assert.Equal("good", loaded.Reporting.Inara.ApiKey);
        Assert.Single(Corrupt());
    }

    [Fact]
    public void A_json_null_file_counts_as_corrupt()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SettingsPath, "null");

        var loaded = NewStore().Load();

        Assert.NotNull(loaded);
        Assert.Single(Corrupt());
    }

    [Fact]
    public void An_unreadable_file_is_left_untouched_and_not_overwritten_by_the_install_id_save()
    {
        Directory.CreateDirectory(Dir);
        var original = """{"Reporting":{"Inara":{"ApiKey":"keep-me"}}}""";
        File.WriteAllText(SettingsPath, original);

        AppSettings loaded;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            loaded = NewStore().Load();                  // locked: cannot be read

        Assert.False(string.IsNullOrWhiteSpace(loaded.InstallId)); // still usable this session
        Assert.Equal(original, File.ReadAllText(SettingsPath));    // but the file was not clobbered
        Assert.Empty(Corrupt());
    }

    [Fact]
    public void A_file_that_could_not_be_read_at_startup_is_not_overwritten_by_a_later_save()
    {
        Directory.CreateDirectory(Dir);
        var original = """{"Reporting":{"Inara":{"ApiKey":"keep-me"}}}""";
        File.WriteAllText(SettingsPath, original);
        var store = NewStore();

        AppSettings loaded;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            loaded = store.Load();                       // locked for the whole startup read (incl. retries)

        loaded.Reporting.Inara.ApiKey = "changed-in-memory";
        var saved = store.TrySave(loaded);               // the lock is gone, but the session ran on defaults

        Assert.False(saved);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".bak"));
    }

    [Fact]
    public async Task A_brief_lock_on_startup_is_waited_out()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SettingsPath, """{"Reporting":{"Inara":{"ApiKey":"keep-me"}}}""");

        var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(150); locked.Dispose(); });

        var loaded = NewStore().Load();
        await release;

        Assert.Equal("keep-me", loaded.Reporting.Inara.ApiKey);
    }

    [Fact]
    public void A_missing_file_yields_defaults_and_persists_the_install_id()
    {
        var loaded = NewStore().Load();

        Assert.False(string.IsNullOrWhiteSpace(loaded.InstallId));
        Assert.Equal(loaded.InstallId, NewStore().Load().InstallId);
    }

    [Fact]
    public async Task Concurrent_saves_never_leave_a_torn_file()
    {
        var store = NewStore();
        var settings = store.Load();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 25; n++)
            {
                settings.Reporting.Inara.ApiKey = $"key-{i}-{n}";
                store.Save(settings);
            }
        })));

        var reloaded = NewStore().Load();
        Assert.StartsWith("key-", reloaded.Reporting.Inara.ApiKey);
        Assert.Empty(Corrupt());
    }

    // --- Owner-only permissions (COR-3). ---

    [Fact]
    public void On_unix_the_settings_file_and_folder_are_owner_only()
    {
        if (OperatingSystem.IsWindows()) return; // Windows relies on the per-user LocalAppData ACL

        var store = NewStore();
        var settings = store.Load();
        settings.Reporting.Inara.ApiKey = "k";
        store.Save(settings);
        store.Save(settings);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsPath + ".bak"));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Dir));
    }

    [Fact]
    public void On_unix_a_previously_world_readable_file_becomes_private_on_the_next_save()
    {
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Dir);
        File.WriteAllText(SettingsPath, "{}");
        File.SetUnixFileMode(SettingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var store = NewStore();
        store.Save(store.Load());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsPath + ".bak"));
    }
}
