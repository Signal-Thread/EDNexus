using System.Text;
using EDNexus.Core.Journal;
using Xunit;

namespace EDNexus.Tests.Journal;

public class JournalWatcherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ednexus-watcher-").FullName;
    private readonly JournalEventBus _bus = new();
    private readonly List<JournalEntry> _seen = new();

    public JournalWatcherTests() => _bus.SubscribeAny(e => _seen.Add(e));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string FirstName = "Journal.2026-08-01T100000.01.log";

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static string Line(string ev, int n = 0) =>
        $$"""{"timestamp":"2026-08-01T10:00:{{n % 60:00}}Z","event":"{{ev}}"}""";

    private void Write(string name, params string[] lines) =>
        File.WriteAllText(PathOf(name), string.Concat(lines.Select(l => l + "\n")));

    private void Append(string name, string raw) => File.AppendAllText(PathOf(name), raw, new UTF8Encoding(false));

    private JournalWatcher NewWatcher() => new(_dir, _bus);

    // --- Malformed input (COR-5). ---

    [Fact]
    public void Malformed_lines_are_skipped_and_do_not_stop_the_pump()
    {
        Write(FirstName,
            Line("Fileheader"),
            "[]", "5", "null", "\"x\"", "{ broken",
            """{"event":"Docked","timestamp":42}""",
            Line("Undocked"));
        var watcher = NewWatcher();

        watcher.Replay();
        Append(FirstName, "[1]\n" + Line("Music") + "\n");
        watcher.PollOnce();

        Assert.Equal(new[] { "Fileheader", "Docked", "Undocked", "Music" }, _seen.Select(e => e.Event));
    }

    [Fact]
    public void A_throwing_bus_observer_does_not_stop_later_lines()
    {
        Write(FirstName, Line("A"), Line("B"));
        _bus.Subscribe("A", _ => throw new InvalidOperationException("boom"));
        var watcher = NewWatcher();

        watcher.Replay();

        Assert.Equal(new[] { "A", "B" }, _seen.Select(e => e.Event));
    }

    // --- Live vs. historical, and tail handling (COR-9). ---

    [Fact]
    public void Replay_is_historical_and_later_appends_are_live()
    {
        Write(FirstName, Line("Old"));
        var watcher = NewWatcher();
        watcher.Replay();
        Append(FirstName, Line("New") + "\n");
        watcher.PollOnce();

        Assert.True(_seen.Single(e => e.Event == "Old").IsHistorical);
        Assert.False(_seen.Single(e => e.Event == "New").IsHistorical);
    }

    [Fact]
    public void A_half_written_last_line_is_held_back_until_it_is_finished()
    {
        var full = Line("Docked");
        Write(FirstName, Line("Fileheader"));
        Append(FirstName, full[..20]); // game is mid-write
        var watcher = NewWatcher();

        watcher.Replay();
        Assert.Equal(new[] { "Fileheader" }, _seen.Select(e => e.Event));

        Append(FirstName, full[20..] + "\n");
        watcher.PollOnce();

        var docked = Assert.Single(_seen, e => e.Event == "Docked");
        Assert.False(docked.IsHistorical);       // finished after the replay: it is live
        watcher.PollOnce();
        Assert.Single(_seen, e => e.Event == "Docked"); // and never published twice
    }

    [Fact]
    public void A_complete_final_line_without_a_newline_is_published_once()
    {
        Write(FirstName, Line("Fileheader"));
        Append(FirstName, Line("Docked")); // complete, just unterminated
        var watcher = NewWatcher();

        watcher.Replay();
        watcher.PollOnce();
        Append(FirstName, "\n");
        watcher.PollOnce();

        Assert.Equal(new[] { "Fileheader", "Docked" }, _seen.Select(e => e.Event));
    }

    [Fact]
    public void A_multi_byte_character_split_across_reads_is_not_corrupted()
    {
        var json = """{"timestamp":"2026-08-01T10:00:00Z","event":"Docked","StationName":"Bürgermeister-Café ☕"}""";
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        var cut = Array.IndexOf(bytes, (byte)0xC3) + 1; // between the two bytes of 'ü'
        File.WriteAllBytes(PathOf(FirstName), bytes[..cut]);
        var watcher = NewWatcher();
        watcher.Replay();

        using (var fs = new FileStream(PathOf(FirstName), FileMode.Append))
            fs.Write(bytes, cut, bytes.Length - cut);
        watcher.PollOnce();

        Assert.Equal("Bürgermeister-Café ☕", Assert.Single(_seen).GetString("StationName"));
    }

    [Fact]
    public void A_shrunken_file_is_re_read_as_history_not_live()
    {
        Write(FirstName, Line("A"), Line("B"), Line("C"));
        var watcher = NewWatcher();
        watcher.Replay();
        _seen.Clear();

        Write(FirstName, Line("A")); // shorter than the saved position
        watcher.PollOnce();

        var again = Assert.Single(_seen);
        Assert.Equal("A", again.Event);
        Assert.True(again.IsHistorical);
    }

    // --- Which file is "latest" (COR-7). ---

    [Fact]
    public void The_latest_journal_is_chosen_by_the_name_not_the_modified_time()
    {
        Write("Journal.2026-08-01T100000.01.log", Line("FromOlder"));
        Write("Journal.2026-08-02T100000.01.log", Line("FromNewer"));
        // A backup/sync/antivirus touch makes the older file look freshest.
        File.SetLastWriteTimeUtc(PathOf("Journal.2026-08-01T100000.01.log"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(PathOf("Journal.2026-08-02T100000.01.log"), DateTime.UtcNow.AddDays(-30));
        var watcher = NewWatcher();

        watcher.Replay();

        Assert.Equal(new[] { "FromNewer" }, _seen.Select(e => e.Event));
    }

    [Fact]
    public void A_touched_older_file_is_not_replayed_live_once_watching()
    {
        Write("Journal.2026-08-02T100000.01.log", Line("Current"));
        Write("Journal.2026-08-01T100000.01.log", Line("Ancient"));
        var watcher = NewWatcher();
        watcher.Replay();
        _seen.Clear();

        File.SetLastWriteTimeUtc(PathOf("Journal.2026-08-01T100000.01.log"), DateTime.UtcNow.AddMinutes(5));
        watcher.PollOnce();

        Assert.Empty(_seen);
    }

    [Fact]
    public void A_new_journal_file_after_a_rollover_is_followed_live()
    {
        Write("Journal.2026-08-01T100000.01.log", Line("Old"));
        var watcher = NewWatcher();
        watcher.Replay();
        _seen.Clear();

        Write("Journal.2026-08-01T110000.01.log", Line("Fileheader"), Line("LoadGame"));
        watcher.PollOnce();

        Assert.Equal(new[] { "Fileheader", "LoadGame" }, _seen.Select(e => e.Event));
        Assert.All(_seen, e => Assert.False(e.IsHistorical));
    }

    [Theory]
    [InlineData("Journal.2026-08-01T100000.01.log", "Journal.2026-08-01T100000.02.log")]
    [InlineData("Journal.2026-08-01T100000.02.log", "Journal.2026-08-01T100001.01.log")]
    [InlineData("Journal.260801100000.01.log", "Journal.2026-08-01T100001.01.log")]   // legacy vs current format
    [InlineData("Journal.2026-08-01T100000.01.log", "Journal.260801100001.01.log")]
    public void Journal_names_order_chronologically(string earlier, string later)
    {
        var mtime = DateTime.UtcNow;
        Assert.True(
            JournalWatcher.SortKey(earlier, mtime).CompareTo(JournalWatcher.SortKey(later, mtime)) < 0);
    }

    [Fact]
    public void A_name_that_does_not_match_the_pattern_falls_back_to_the_modified_time()
    {
        var mtime = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal((mtime, 0), JournalWatcher.SortKey("Journal.weird.log", mtime));
    }

    // --- Sidecar files (COR-6). ---

    [Fact]
    public void A_sidecar_read_mid_rewrite_is_retried_until_it_parses()
    {
        Write(FirstName, Line("Fileheader"));
        var watcher = NewWatcher();
        watcher.Replay();
        _seen.Clear();

        File.WriteAllText(PathOf("Market.json"), "");                 // game just truncated it
        watcher.PollOnce();
        File.WriteAllText(PathOf("Market.json"), """{"event":"Market","timestamp":"2026-08-01T10:00:00Z","MarketID":1""");   // partial
        watcher.PollOnce();
        Assert.Empty(_seen);

        File.WriteAllText(PathOf("Market.json"), """{"event":"Market","timestamp":"2026-08-01T10:00:00Z","MarketID":1}""");
        watcher.PollOnce();

        var market = Assert.Single(_seen);
        Assert.Equal("Market", market.Event);
        Assert.False(market.IsHistorical);
    }

    [Fact]
    public void A_sidecar_that_failed_to_parse_with_unchanged_stamp_is_retried_on_later_ticks()
    {
        Write(FirstName, Line("Fileheader"));
        var watcher = NewWatcher();
        // Present from the start but unreadable at first; the SAME bytes/stamp can't change, so the
        // retry has to come from the watcher, not from a fresh write. Simulate with a locked file.
        var path = PathOf("Cargo.json");
        File.WriteAllText(path, """{"event":"Cargo","Count":0}""");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            watcher.Replay();                       // sharing violation: nothing published yet
            Assert.DoesNotContain(_seen, e => e.Event == "Cargo");
        }

        watcher.PollOnce();

        // Replay-time failure stays historical so it can't trigger live reactions.
        var cargo = Assert.Single(_seen, e => e.Event == "Cargo");
        Assert.True(cargo.IsHistorical);
    }

    [Fact]
    public void An_unchanged_sidecar_is_published_once()
    {
        Write(FirstName, Line("Fileheader"));
        File.WriteAllText(PathOf("Status.json"), """{"event":"Status","Flags":1}""");
        var watcher = NewWatcher();

        watcher.Replay();
        watcher.PollOnce();
        watcher.PollOnce();

        Assert.Single(_seen, e => e.Event == "Status");
    }

    [Fact]
    public void A_rewritten_sidecar_is_published_again()
    {
        Write(FirstName, Line("Fileheader"));
        File.WriteAllText(PathOf("Status.json"), """{"event":"Status","Flags":1}""");
        var watcher = NewWatcher();
        watcher.Replay();

        File.WriteAllText(PathOf("Status.json"), """{"event":"Status","Flags":22}""");
        watcher.PollOnce();

        Assert.Equal(2, _seen.Count(e => e.Event == "Status"));
    }

    [Fact]
    public void An_unparseable_sidecar_is_retried_a_bounded_number_of_times()
    {
        Write(FirstName, Line("Fileheader"));
        var watcher = NewWatcher();
        watcher.Replay();
        File.WriteAllText(PathOf("ShipLocker.json"), "{ not json");

        for (var i = 0; i < 50; i++) watcher.PollOnce(); // must neither throw nor spin forever

        // A genuine rewrite is still picked up afterwards.
        File.WriteAllText(PathOf("ShipLocker.json"), """{"event":"ShipLocker","Items":[]}""");
        watcher.PollOnce();
        Assert.Single(_seen, e => e.Event == "ShipLocker");
    }

    [Fact]
    public async Task RunAsync_keeps_going_after_malformed_input_and_stops_on_cancel()
    {
        Write(FirstName, Line("Fileheader"));
        var watcher = new JournalWatcher(_dir, _bus, TimeSpan.FromMilliseconds(10));
        watcher.Replay();
        using var cts = new CancellationTokenSource();
        var run = watcher.RunAsync(cts.Token);

        Append(FirstName, "[]\n5\n" + Line("Docked") + "\n");
        for (var i = 0; i < 200 && !_seen.Any(e => e.Event == "Docked"); i++) await Task.Delay(10);

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(_seen, e => e.Event == "Docked");
    }
}
