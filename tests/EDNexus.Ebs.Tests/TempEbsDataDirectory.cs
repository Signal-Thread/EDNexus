using System.Text;
using EDNexus.Ebs.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace EDNexus.Ebs.Tests;

/// <summary>
/// A throwaway EBS data directory (SQLite database + Data Protection key ring). Every
/// <c>Create*</c> call builds a brand-new store instance over the same files, which is how the
/// tests simulate a process restart.
/// </summary>
public sealed class TempEbsDataDirectory : IDisposable
{
    public TempEbsDataDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ednexus-ebs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string KeysPath => System.IO.Path.Combine(Path, "keys");

    public EbsDatabase OpenDatabase() => new(System.IO.Path.Combine(Path, EbsDatabase.FileName));

    /// <summary>A Data Protection provider over this directory's key ring — the same keys every time, like a restarted host.</summary>
    public IDataProtectionProvider CreateDataProtection(string? keysPath = null) =>
        DataProtectionProvider.Create(new DirectoryInfo(keysPath ?? KeysPath), b => b.SetApplicationName("EDNexus.Ebs"));

    public SqliteBroadcasterTokenStore CreateTokenStore(TimeProvider? time = null, string? keysPath = null) =>
        new(OpenDatabase(), CreateDataProtection(keysPath), time);

    public SqliteChannelStateStore CreateChannelStateStore(TimeProvider? time = null, TimeSpan? maxAge = null) =>
        new(OpenDatabase(), time, maxAge);

    /// <summary>Every byte SQLite has written for the database (main file plus WAL/SHM), as Latin-1 text for substring searches.</summary>
    public string ReadAllDatabaseBytes()
    {
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Path, EbsDatabase.FileName + "*"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            text.Append(Encoding.Latin1.GetString(memory.ToArray()));
        }

        return text.ToString();
    }

    public void Dispose()
    {
        // Pooled connections keep the file open on Windows, which would block the delete. Only THIS
        // directory's pool is cleared: ClearAllPools() would close (and so checkpoint, and delete the
        // -wal file of) every other test's live database, racing ReadAllDatabaseBytes in tests that
        // xUnit runs in parallel.
        using (var own = new SqliteConnection(EbsDatabase.BuildConnectionString(System.IO.Path.Combine(Path, EbsDatabase.FileName))))
            SqliteConnection.ClearPool(own);
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { }
    }
}
