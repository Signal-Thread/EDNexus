using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>
/// Plugin-controlled text must not forge log lines (<see cref="TextRules.ForDisplay"/>), and one
/// plugin must not be able to grow the log without bound (<see cref="TracePluginLog"/>'s budget).
/// The log's clock and sink are injected: nothing sleeps and nothing reads <see cref="System.Diagnostics.Trace"/>.
/// </summary>
public sealed class PluginLogHardeningTests
{
    // ---- text sanitising ----------------------------------------------------------------------------

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\r\nb")]
    [InlineData("a\u2028b")]   // LINE SEPARATOR (Zl): neither a control nor a format character
    [InlineData("a\u2029b")]   // PARAGRAPH SEPARATOR (Zp)
    [InlineData("a\u0085b")]   // NEXT LINE (a C1 control character, so it was already covered)
    [InlineData("a\u000Bb")]
    [InlineData("a\u000Cb")]
    [InlineData("a\u001Bb")]   // ESC
    [InlineData("a\u202Eb")]   // right-to-left override (Cf)
    public void ForDisplay_NeutralisesEveryLineBreakingOrInvisibleCharacter(string text)
    {
        var shown = TextRules.ForDisplay(text);

        Assert.Equal("a?b".Length + (text.Contains("\r\n") ? 1 : 0), shown.Length);
        Assert.All(shown, c => Assert.True(c is 'a' or 'b' or '?', $"U+{(int)c:X4} survived"));
    }

    [Fact]
    public void ForDisplay_KeepsOrdinaryTextIncludingNonAsciiAndSurrogatePairs()
    {
        const string text = "Sol — Σίριος 🚀 ok (v1.0)";

        Assert.Equal(text, TextRules.ForDisplay(text));
    }

    [Fact]
    public void ForDisplay_StillCutsToTheLimit()
    {
        var shown = TextRules.ForDisplay(new string('x', 600));

        Assert.Equal(500 + 1, shown.Length);
        Assert.EndsWith("…", shown);
    }

    [Fact]
    public void PluginLogText_IsTheSameSanitiser_ForCallersOutsideTheAssembly()
    {
        Assert.Equal("folder?[FORGED] line?x", PluginLogText.Sanitise("folder\n[FORGED] line\u2028x"));
        Assert.Equal("", PluginLogText.Sanitise(null));
        Assert.Equal(5 + 1, PluginLogText.Sanitise("abcdefgh", 5).Length);
    }

    // ---- the plugin log: forging, length ------------------------------------------------------------

    private sealed class Rig
    {
        public TimeSpan Now;
        public readonly List<string> Lines = [];
        public TracePluginLog Log;

        public Rig(string id = "com.test.logs")
        {
            Log = new TracePluginLog(id, () => Now, Lines.Add);
        }

        public int Notices => Lines.Count(l => l.Contains("suppressed"));
    }

    [Fact]
    public void ALogLine_CannotBeSplitByThePluginsMessageOrException()
    {
        var rig = new Rig();

        rig.Log.Info("first\n[2026-01-01] Plugin com.other [error]: forged\u2028and\u2029more\r\n");
        rig.Log.Error("bad\nthing", new InvalidOperationException("boom\nPlugin com.other [error]: forged"));

        Assert.Equal(2, rig.Lines.Count);
        Assert.All(rig.Lines, line =>
        {
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
            Assert.DoesNotContain('\u2028', line);
            Assert.DoesNotContain('\u2029', line);
        });
        Assert.StartsWith("Plugin com.test.logs [info]: first?", rig.Lines[0]);
    }

    [Fact]
    public void ALogLine_IsCutToTheLineCap_WhateverThePluginSends()
    {
        var rig = new Rig();

        rig.Log.Info(new string('x', 5_000_000));
        rig.Log.Error(new string('y', 100_000), new InvalidOperationException(new string('z', 100_000)));

        Assert.Equal(2, rig.Lines.Count);
        Assert.All(rig.Lines, line =>
        {
            Assert.Equal(TracePluginLog.MaxLineLength + 1, line.Length);   // the cap plus the cut marker
            Assert.EndsWith("…", line);
            Assert.StartsWith("Plugin com.test.logs [", line);
        });
    }

    [Fact]
    public void NormalLogging_IsUnaffected_ByTheLineCap_AndTheBudget()
    {
        var rig = new Rig();

        rig.Log.Debug("debug line");
        rig.Log.Info("init com.test.logs");
        rig.Log.Warn("careful");
        rig.Log.Error("failed", new InvalidOperationException("why"));

        Assert.Equal(
            [
                "Plugin com.test.logs [debug]: debug line",
                "Plugin com.test.logs [info]: init com.test.logs",
                "Plugin com.test.logs [warn]: careful",
                "Plugin com.test.logs [error]: failed (System.InvalidOperationException: why)",
            ],
            rig.Lines);
    }

    // ---- the plugin log: the budget -----------------------------------------------------------------

    [Fact]
    public void ABurstUpToTheCapacity_IsWrittenInFull_AndTheNextLineIsDropped()
    {
        var rig = new Rig();

        for (var i = 0; i < TracePluginLog.BurstLines; i++) rig.Log.Info("line " + i);
        Assert.Equal(TracePluginLog.BurstLines, rig.Lines.Count);

        rig.Log.Info("one too many");
        rig.Log.Info("and another");

        Assert.Equal(TracePluginLog.BurstLines, rig.Lines.Count);
        Assert.DoesNotContain(rig.Lines, l => l.Contains("one too many"));
    }

    [Fact]
    public void WhenTheBucketHasRecovered_ExactlyOneNoticeReportsTheDroppedCount_ThenLoggingIsNormalAgain()
    {
        var rig = new Rig();
        for (var i = 0; i < TracePluginLog.BurstLines + 70; i++) rig.Log.Info("flood " + i);   // 70 dropped, all at one instant
        Assert.Equal(TracePluginLog.BurstLines, rig.Lines.Count);
        Assert.Equal(0, rig.Notices);

        rig.Now += TimeSpan.FromSeconds(TracePluginLog.BurstLines / TracePluginLog.RefillLinesPerSecond);   // fully refilled
        rig.Log.Info("back");
        rig.Log.Info("and again");

        var tail = rig.Lines.Skip(TracePluginLog.BurstLines).ToList();
        Assert.Equal(3, tail.Count);
        Assert.StartsWith("Plugin com.test.logs [warn]: 70 log lines suppressed", tail[0]);
        Assert.EndsWith("[info]: back", tail[1]);
        Assert.EndsWith("[info]: and again", tail[2]);
        Assert.Equal(1, rig.Notices);
    }

    [Fact]
    public void ARefillOfOneLine_LetsOneThrough_WithoutFlushingTheNoticeYet()
    {
        var rig = new Rig();
        for (var i = 0; i < TracePluginLog.BurstLines + 5; i++) rig.Log.Info("flood");
        var before = rig.Lines.Count;

        rig.Now += TimeSpan.FromSeconds(1 / TracePluginLog.RefillLinesPerSecond);   // one token
        rig.Log.Info("trickle");
        rig.Log.Info("dropped again");

        Assert.Equal(before + 1, rig.Lines.Count);
        Assert.EndsWith("trickle", rig.Lines[^1]);
        Assert.Equal(0, rig.Notices);
    }

    [Fact]
    public void ASustainedFlood_IsBounded_AccountsForEveryLine_AndNoticesAreRare()
    {
        const int total = 100_000;
        var step = TimeSpan.FromMilliseconds(1);   // 1000 lines/s for 100 s
        var rig = new Rig();

        for (var i = 0; i < total; i++)
        {
            rig.Now += step;
            rig.Log.Info("spam");
        }
        // Let it recover so the final pending count is reported, then account for every line.
        rig.Now += TimeSpan.FromMinutes(5);
        rig.Log.Info("done");

        var written = rig.Lines.Count(l => l.EndsWith("spam"));
        var reported = rig.Lines.Where(l => l.Contains("suppressed")).Sum(l => long.Parse(l.Split("[warn]: ")[1].Split(' ')[0]));
        // 100 s at 1 line/s plus the burst: nowhere near the 100 000 sent.
        Assert.InRange(written, TracePluginLog.BurstLines, TracePluginLog.BurstLines + 105);
        Assert.Equal(total, written + reported);
        // One notice per interval at most while flooding (100 s / 30 s), plus the final flush.
        Assert.InRange(rig.Notices, 1, 5);
        Assert.True(rig.Lines.Count < 150, $"{rig.Lines.Count} lines for a {total}-line flood");
    }

    [Fact]
    public void WhileTheFloodContinues_ANoticeIsDuePeriodically_EvenThoughTheBucketNeverRecovers()
    {
        var rig = new Rig();

        // 10 lines a second against a refill of 1 a second: the burst is spent in about 3 seconds and
        // the bucket then never holds RecoveredLines again. The first dropped line starts a 30 s notice
        // timer, so over 70 s the notices come at about 33 s and 63 s.
        for (var i = 0; i < 700; i++)
        {
            rig.Now += TimeSpan.FromMilliseconds(100);
            rig.Log.Info("flood");
        }

        Assert.Equal(2, rig.Notices);
        var written = rig.Lines.Count(l => l.EndsWith("flood"));
        Assert.InRange(written, TracePluginLog.BurstLines, TracePluginLog.BurstLines + 75);
    }

    [Fact]
    public void TheBudget_IsPerPlugin()
    {
        var noisy = new Rig("com.test.noisy");
        var quiet = new Rig("com.test.quiet");

        for (var i = 0; i < TracePluginLog.BurstLines * 3; i++) noisy.Log.Info("spam");
        quiet.Log.Info("still here");

        Assert.Equal(TracePluginLog.BurstLines, noisy.Lines.Count);
        Assert.Equal(["Plugin com.test.quiet [info]: still here"], quiet.Lines);
    }

    [Fact]
    public void AClockThatGoesBackwards_NeverCreatesTokens()
    {
        var rig = new Rig { Now = TimeSpan.FromMinutes(10) };
        rig.Log = new TracePluginLog("com.test.logs", () => rig.Now, rig.Lines.Add);
        for (var i = 0; i < TracePluginLog.BurstLines; i++) rig.Log.Info("x");

        rig.Now = TimeSpan.FromMinutes(1);
        rig.Log.Info("dropped");

        Assert.Equal(TracePluginLog.BurstLines, rig.Lines.Count);
    }

    [Fact]
    public void ASinkThatThrows_NeverReachesThePlugin()
    {
        var log = new TracePluginLog("com.test.logs", () => TimeSpan.Zero, _ => throw new IOException("log file is gone"));

        log.Info("hello");
        log.Error("bad", new InvalidOperationException("x"));
    }

    [Fact]
    public void ConcurrentLogging_NeverWritesMoreThanTheBurst_WhenTimeStandsStill()
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var log = new TracePluginLog("com.test.logs", () => TimeSpan.Zero, lines.Enqueue);

        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            for (var i = 0; i < 100; i++) log.Info("spam");
        });

        Assert.Equal(TracePluginLog.BurstLines, lines.Count);
    }
}
