using EDNexus.Ebs.Services;
using Microsoft.Data.Sqlite;

namespace EDNexus.Ebs.Tests;

public sealed class EbsDatabaseMigrationTests
{
    private static int UserVersion(EbsDatabase db)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public void Fresh_file_is_stamped_with_the_current_schema_version()
    {
        using var dir = new TempEbsDataDirectory();
        Assert.Equal(EbsDatabase.SchemaVersion, UserVersion(dir.OpenDatabase()));
    }

    private static bool HasUpdatedAtIndex(EbsDatabase db)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'channel_state' AND name = 'ix_channel_state_updated_at';";
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    [Fact]
    public void A_fresh_file_has_the_updated_at_index_the_prune_relies_on()
    {
        using var dir = new TempEbsDataDirectory();
        Assert.True(HasUpdatedAtIndex(dir.OpenDatabase()));
    }

    [Fact]
    public void A_version_1_file_is_upgraded_in_place_keeping_its_rows()
    {
        using var dir = new TempEbsDataDirectory();
        var path = System.IO.Path.Combine(dir.Path, EbsDatabase.FileName);
        using (var connection = new SqliteConnection(EbsDatabase.BuildConnectionString(path)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE broadcaster_tokens (
                    channel_id TEXT PRIMARY KEY, token_hash TEXT NOT NULL UNIQUE, username TEXT NOT NULL,
                    twitch_access_token TEXT NOT NULL, twitch_refresh_token TEXT NOT NULL, twitch_expires_at TEXT NOT NULL,
                    is_twitch_grant_valid INTEGER NOT NULL, created_at TEXT NOT NULL, last_refreshed_at TEXT NULL);
                CREATE TABLE channel_state (channel_id TEXT PRIMARY KEY, state_json TEXT NOT NULL, updated_at TEXT NOT NULL);
                INSERT INTO channel_state VALUES ('channel-1', '{"system":"Sol"}', '2026-01-01T00:00:00.0000000+00:00');
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        var db = dir.OpenDatabase();

        Assert.Equal(EbsDatabase.SchemaVersion, UserVersion(db));
        Assert.True(HasUpdatedAtIndex(db));
        Assert.True(dir.CreateChannelStateStore().TryGet("channel-1", out var state));
        Assert.Equal("Sol", state.GetProperty("system").GetString());
    }

    [Fact]
    public void Reopening_an_up_to_date_file_is_a_no_op()
    {
        using var dir = new TempEbsDataDirectory();
        dir.OpenDatabase();
        Assert.Equal(EbsDatabase.SchemaVersion, UserVersion(dir.OpenDatabase()));
    }

    [Fact]
    public void A_file_from_a_newer_build_is_refused()
    {
        using var dir = new TempEbsDataDirectory();
        var db = dir.OpenDatabase();
        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA user_version = {EbsDatabase.SchemaVersion + 1};";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => dir.OpenDatabase());
    }

    [Fact]
    public async Task Containers_overlapping_on_one_volume_all_migrate_without_error()
    {
        // A rolling deploy starts the new container while the old one (or a second new one) is opening
        // the same file. Every opener must come up, not just the one that wins the race.
        using var dir = new TempEbsDataDirectory();
        using var gate = new ManualResetEventSlim();

        var openers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            return UserVersion(dir.OpenDatabase());
        })).ToArray();

        gate.Set();
        var versions = await Task.WhenAll(openers);

        Assert.All(versions, v => Assert.Equal(EbsDatabase.SchemaVersion, v));
    }
}
