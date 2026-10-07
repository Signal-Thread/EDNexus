using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EDNexus.Core.Telemetry;

namespace EDNexus.Core.Journal;

/// <summary>
/// Tails the Elite Dangerous journal folder and republishes everything onto a
/// <see cref="JournalEventBus"/>:
/// <list type="bullet">
/// <item>the newest <c>Journal.*.log</c>, following appends and rolling over to new files;</item>
/// <item>the sidecar status files (<c>Status.json</c>, <c>Cargo.json</c>, <c>Market.json</c>, …)
/// whenever they change on disk.</item>
/// </list>
/// Polling (rather than <see cref="FileSystemWatcher"/>) is deliberate: the game holds the
/// journal open and flushes irregularly, and polling with shared read access is the approach
/// proven robust by existing tools.
/// </summary>
public sealed partial class JournalWatcher
{
    // Sidecar files the game rewrites in place. Each already contains an "event" field.
    private static readonly string[] StatusFiles =
    {
        "Status.json", "Cargo.json", "Market.json", "NavRoute.json", "Backpack.json",
        "ShipLocker.json", "Outfitting.json", "Shipyard.json", "ModulesInfo.json", "FCMaterials.json",
    };

    /// <summary>How many ticks a sidecar file that fails to read/parse is retried before waiting for its next rewrite.</summary>
    private const int MaxStatusAttempts = 10;

    /// <summary>A trailing line this large with no newline is garbage; skip it instead of re-reading it every tick.</summary>
    private const long MaxUnterminatedTailBytes = 16 * 1024 * 1024;

    private readonly string _dir;
    private readonly JournalEventBus _bus;
    private readonly TimeSpan _pollInterval;

    private string? _currentFile;

    // Byte offset of the first byte NOT yet turned into an event: the start of the next (possibly
    // still half-written) line. Tracking bytes rather than decoded text means a partial line is simply
    // re-read next tick, and a multi-byte character split across two reads can't be corrupted.
    private long _position;

    // True while the bytes being read are history rather than live play: during Replay, and when a
    // shrunken file is re-read from the start. Everything published in that window is marked historical
    // so it can't fire voice/Discord/chimes or be re-uploaded to EDDN/Inara.
    private bool _replayingHistory;

    private readonly Dictionary<string, StatusFileState> _statusStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-sidecar-file bookkeeping: what was last handled and how many reads of it have failed.</summary>
    private sealed class StatusFileState
    {
        public (DateTime WriteUtc, long Length) Stamp;
        public bool Done;          // this stamp was parsed and published
        public int Attempts;       // reads of this stamp so far
        public bool Historical;    // first seen during Replay, so a retry must stay historical
    }

    public JournalWatcher(string journalDir, JournalEventBus bus, TimeSpan? pollInterval = null)
    {
        _dir = journalDir;
        _bus = bus;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public string Directory => _dir;

    /// <summary>
    /// Raised when a poll tick fails for a reason other than a transient file-sharing error, so the
    /// host can log it. The loop keeps running afterwards: a bad file must not silently end watching.
    /// </summary>
    public event Action<Exception>? Error;

    /// <summary>
    /// Replays the latest journal file plus current sidecar files (all marked historical) to
    /// rebuild state, then leaves the read position at the end of the last complete line so
    /// <see cref="RunAsync"/> continues seamlessly. Call once before <see cref="RunAsync"/> for a warm start.
    /// </summary>
    public void Replay()
    {
        var latest = LatestJournal();
        if (latest is null) return;

        _currentFile = latest;
        _position = 0;
        _replayingHistory = true;
        try { ReadNewLines(latest); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in "replaying history" mode at offset 0, so RunAsync's first tick retries the
            // read and still marks it historical rather than replaying old events as live ones.
        }

        foreach (var sf in StatusFiles)
            EmitStatusFileSafe(sf, historical: true);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // If Replay() wasn't called, begin live at the end of the newest file so we don't
        // re-emit history as if it were happening now.
        if (_currentFile is null)
        {
            _currentFile = LatestJournal();
            _position = _currentFile is not null ? SafeLength(_currentFile) : 0;
        }

        while (!ct.IsCancellationRequested)
        {
            PollOnce();

            try { await Task.Delay(_pollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One watcher tick: pump the journal, then each sidecar file. Every step is isolated so one
    /// failing file can't stop the others or end the loop.
    /// </summary>
    internal void PollOnce()
    {
        try { PumpJournal(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* transient sharing violation — retry next tick */ }
        catch (Exception ex) { ReportError(ex); }

        foreach (var sf in StatusFiles)
            EmitStatusFileSafe(sf, historical: false);
    }

    // A fault that recurs every 250 ms tick (or on every line of a bad file) is reported once a minute.
    private readonly ErrorThrottle _errorThrottle = new(TimeSpan.FromMinutes(1));

    private void ReportError(Exception ex)
    {
        try
        {
            if (_errorThrottle.ShouldReport(ErrorThrottle.KeyFor(ex, "journal-watcher")))
                Error?.Invoke(ex);
        }
        catch { /* a faulty logger must not kill the watcher */ }
    }

    private void PumpJournal()
    {
        var latest = LatestJournal();
        if (latest is null) return;

        if (!string.Equals(latest, _currentFile, StringComparison.OrdinalIgnoreCase))
        {
            // Drain the last lines of the previous file before switching.
            if (_currentFile is not null)
            {
                try { ReadNewLines(_currentFile); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _currentFile = latest;
            _position = 0;
            _replayingHistory = false; // a freshly created journal is live play
        }

        // _currentFile == latest at this point in both branches.
        ReadNewLines(latest);
    }

    /// <summary>
    /// Publishes every complete line from <see cref="_position"/> to the end of the file and advances
    /// <see cref="_position"/> past them. The position comes from the same stream the lines were read
    /// from (never a separate length query), so lines appended while reading are picked up next tick
    /// rather than skipped, and a half-written last line is left unconsumed until it is finished.
    /// </summary>
    private void ReadNewLines(string path)
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }

        using (fs)
        {
            // Defensive: if the file shrank (shouldn't happen for a journal), restart from 0 — but
            // what's re-read is history we have already seen, so it must not replay as live events.
            if (fs.Length < _position) { _position = 0; _replayingHistory = true; }
            if (fs.Length == _position) { _replayingHistory = false; return; }

            var historical = _replayingHistory;
            var consumed = _position;
            fs.Seek(consumed, SeekOrigin.Begin);

            var chunk = new byte[64 * 1024];
            using var line = new MemoryStream();
            int read;
            while ((read = fs.Read(chunk, 0, chunk.Length)) > 0)
            {
                var chunkStart = fs.Position - read;
                var from = 0;
                for (var i = 0; i < read; i++)
                {
                    if (chunk[i] != (byte)'\n') continue;
                    line.Write(chunk, from, i - from);
                    EmitLine(line, historical);
                    line.SetLength(0);
                    from = i + 1;
                    consumed = chunkStart + from;
                }
                line.Write(chunk, from, read - from);
            }

            // Whatever follows the last newline is either a line still being written (leave it for
            // the next tick) or a complete final line the game didn't terminate. A truncated JSON
            // object never parses, so "parses" is a safe test for "complete".
            if (line.Length > 0)
            {
                if (EndsWithBrace(line) && EmitLine(line, historical)) consumed = fs.Position;
                else if (line.Length > MaxUnterminatedTailBytes) consumed = fs.Position;
            }

            _position = consumed;
            _replayingHistory = false;
        }
    }

    /// <summary>Cheap pre-check that an unterminated tail could be a finished JSON object, to avoid parsing every tick.</summary>
    private static bool EndsWithBrace(MemoryStream line)
    {
        var buf = line.GetBuffer();
        for (var i = (int)line.Length - 1; i >= 0; i--)
        {
            var b = buf[i];
            if (b is (byte)'\r' or (byte)' ' or (byte)'\t') continue;
            return b == (byte)'}';
        }
        return false;
    }

    /// <summary>Decodes and publishes one line. Returns true when it was a well-formed journal entry.</summary>
    private bool EmitLine(MemoryStream line, bool historical)
    {
        try
        {
            var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length)
                .TrimEnd('\r', '\n').TrimStart('﻿');
            if (!JournalEntry.TryParse(text, historical, out var e)) return false;
            _bus.Publish(e);
            return true;
        }
        catch (Exception ex)
        {
            // One bad line (or a throwing observer of the bus) must never stop the pump.
            ReportError(ex);
            return false;
        }
    }

    private void EmitStatusFileSafe(string name, bool historical)
    {
        try { EmitStatusFile(name, historical); }
        catch (Exception ex) { ReportError(ex); }
    }

    private void EmitStatusFile(string name, bool historical)
    {
        var path = Path.Combine(_dir, name);
        (DateTime, long) stamp;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            stamp = (info.LastWriteTimeUtc, info.Length);
        }
        catch { return; }

        if (!_statusStates.TryGetValue(name, out var state))
            _statusStates[name] = state = new StatusFileState { Stamp = default };

        if (state.Stamp != stamp)
        {
            // A new write of the file: start afresh. (A first sighting during Replay is historical.)
            state.Stamp = stamp;
            state.Done = false;
            state.Attempts = 0;
            state.Historical = historical;
        }
        if (state.Done || state.Attempts >= MaxStatusAttempts) return;

        // The stamp counts as handled only once the content has been parsed and published. A read that
        // lands mid-rewrite (empty, partial, locked) is retried on the next ticks, bounded so a file
        // that is genuinely unparseable isn't re-read forever.
        state.Attempts++;
        string content;
        try { content = ReadAllTextShared(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (string.IsNullOrWhiteSpace(content)) return;

        if (!JournalEntry.TryParse(content, state.Historical, out var e)) return;
        state.Done = true;
        _bus.Publish(e);
    }

    private string? LatestJournal()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("Journal.*.log");
            if (files.Length == 0) return null;
            return files
                .Select(f => (File: f, Key: SortKey(f)))
                .OrderBy(x => x.Key.WhenUtc)
                .ThenBy(x => x.Key.Part)
                .ThenBy(x => x.File.Name, StringComparer.Ordinal)
                .Last().File.FullName;
        }
        catch { return null; }
    }

    // Journal.2026-10-06T123456.01.log (current) or Journal.261006123456.01.log (legacy):
    // the game's local start time of the session, then a part number for the same-second rollover.
    [GeneratedRegex(@"^Journal\.(?:(?<y4>\d{4})-(?<mo4>\d{2})-(?<d4>\d{2})T(?<h4>\d{2})(?<mi4>\d{2})(?<s4>\d{2})|(?<y2>\d{2})(?<mo2>\d{2})(?<d2>\d{2})(?<h2>\d{2})(?<mi2>\d{2})(?<s2>\d{2}))(?:\.(?<part>\d+))?\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JournalNameRegex();

    /// <summary>
    /// Chronological ordering key for a journal file: the session start embedded in its name (which a
    /// backup, sync or antivirus touch can't change), falling back to the last-write time for a name
    /// that doesn't follow the game's pattern.
    /// </summary>
    internal static (DateTime WhenUtc, int Part) SortKey(FileInfo file)
        => SortKey(file.Name, file.LastWriteTimeUtc);

    internal static (DateTime WhenUtc, int Part) SortKey(string fileName, DateTime lastWriteUtc)
    {
        var m = JournalNameRegex().Match(fileName);
        if (m.Success)
        {
            var modern = m.Groups["y4"].Success;
            string Get(string modernName, string legacyName) => m.Groups[modern ? modernName : legacyName].Value;
            var year = modern ? int.Parse(Get("y4", "y2"), CultureInfo.InvariantCulture)
                              : 2000 + int.Parse(Get("y4", "y2"), CultureInfo.InvariantCulture);
            try
            {
                var local = new DateTime(
                    year,
                    int.Parse(Get("mo4", "mo2"), CultureInfo.InvariantCulture),
                    int.Parse(Get("d4", "d2"), CultureInfo.InvariantCulture),
                    int.Parse(Get("h4", "h2"), CultureInfo.InvariantCulture),
                    int.Parse(Get("mi4", "mi2"), CultureInfo.InvariantCulture),
                    int.Parse(Get("s4", "s2"), CultureInfo.InvariantCulture),
                    DateTimeKind.Local);
                var part = m.Groups["part"].Success
                    && int.TryParse(m.Groups["part"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 0;
                return (local.ToUniversalTime(), part);
            }
            catch (ArgumentOutOfRangeException) { /* an impossible date: fall through to the mtime */ }
        }
        return (lastWriteUtc, 0);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
