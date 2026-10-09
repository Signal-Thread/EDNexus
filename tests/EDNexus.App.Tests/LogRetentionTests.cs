using EDNexus.App;

namespace EDNexus.App.Tests;

public sealed class LogRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ednexus-logtest-" + Guid.NewGuid().ToString("N"));

    public LogRetentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Make(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Prune_KeepsTheNewestByName_AndDeletesTheRest()
    {
        for (var day = 1; day <= 14; day++) Make($"ednexus-202601{day:00}_120000.log");

        var removed = LogRetention.Prune(_dir, keep: 10);

        Assert.Equal(4, removed);
        var left = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(10, left.Count);
        Assert.Equal("ednexus-20260105_120000.log", left[0]);
        Assert.Equal("ednexus-20260114_120000.log", left[^1]);
    }

    [Fact]
    public void Prune_LeavesUnrelatedFilesAlone()
    {
        for (var day = 1; day <= 12; day++) Make($"ednexus-202601{day:00}_120000.log");
        var other = Make("last_crash.txt");

        LogRetention.Prune(_dir, keep: 3);

        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Prune_UnderTheLimit_DeletesNothing()
    {
        Make("ednexus-20260101_000000.log");
        Make("ednexus-20260102_000000.log");

        Assert.Equal(0, LogRetention.Prune(_dir, keep: 10));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void Prune_MissingFolder_DoesNotThrow()
        => Assert.Equal(0, LogRetention.Prune(Path.Combine(_dir, "nope")));

    [Fact]
    public void Prune_FileHeldOpenByAnotherInstance_IsSkippedNotFatal()
    {
        var oldest = Make("ednexus-20260101_000000.log");
        Make("ednexus-20260102_000000.log");
        Make("ednexus-20260103_000000.log");

        // Another running EDNexus holds its log open without FileShare.Delete.
        using var held = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.Read);

        var removed = LogRetention.Prune(_dir, keep: 2);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(0, removed);
            Assert.True(File.Exists(oldest));
        }
        else
        {
            Assert.Equal(1, removed);   // POSIX lets an open file be unlinked
        }
    }
}
