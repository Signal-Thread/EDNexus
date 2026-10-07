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
