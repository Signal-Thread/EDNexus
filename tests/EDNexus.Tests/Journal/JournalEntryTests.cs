using EDNexus.Core.Journal;
using Xunit;

namespace EDNexus.Tests.Journal;

public class JournalEntryTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("[1,2]")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("true")]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("   ")]
    public void Lines_that_are_not_journal_objects_are_rejected_without_throwing(string line)
    {
        Assert.False(JournalEntry.TryParse(line, historical: false, out _));
    }

    [Theory]
    [InlineData("""{"event":5}""")]
    [InlineData("""{"event":null}""")]
    [InlineData("""{"timestamp":"2026-01-01T00:00:00Z"}""")]
    public void Objects_without_a_string_event_are_rejected(string line)
    {
        Assert.False(JournalEntry.TryParse(line, historical: false, out _));
    }

    [Theory]
    [InlineData("""{"event":"Docked","timestamp":12345}""")]
    [InlineData("""{"event":"Docked","timestamp":null}""")]
    [InlineData("""{"event":"Docked","timestamp":{"a":1}}""")]
    [InlineData("""{"event":"Docked","timestamp":"not a date"}""")]
    public void A_bad_timestamp_leaves_the_entry_valid_with_a_default_time(string line)
    {
        Assert.True(JournalEntry.TryParse(line, historical: false, out var entry));
        Assert.Equal("Docked", entry.Event);
        Assert.Equal(default, entry.Timestamp);
    }

    [Fact]
    public void A_well_formed_line_parses()
    {
        Assert.True(JournalEntry.TryParse(
            """{"timestamp":"2026-08-01T10:00:00Z","event":"Docked"}""", historical: true, out var entry));
        Assert.Equal("Docked", entry.Event);
        Assert.True(entry.IsHistorical);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero), entry.Timestamp);
    }
}
