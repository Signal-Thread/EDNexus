using EDNexus.Core.Journal;

namespace EDNexus.Core.Dev;

/// <summary>
/// Developer-mode coordinator: holds the per-card <see cref="JournalSampleSource"/>s and, on demand,
/// publishes a fresh batch of random-but-valid events onto a bus so the whole UI can be exercised
/// without the game running. Intended for the app's dev toggle; it never touches state directly.
/// </summary>
public sealed class DeveloperMode
{
    public IReadOnlyList<JournalSampleSource> Sources { get; } = new JournalSampleSource[]
    {
        new LocationSampleSource(),
        new SurfacePositionSampleSource(),
        new ShipSampleSource(),
        new MaterialsSampleSource(),
        new CargoSampleSource(),
        new ColonisationSampleSource(),
        new ShoppingListSampleSource(),
        new MarketSampleSource(),
        new ExobiologySampleSource(),
        new MissionsSampleSource(),
        new CommunityGoalSampleSource(),
        new RanksSampleSource(),
        new MiningSampleSource(),
        new OverlayVoiceSampleSource(),
        new RadioSampleSource(),
    };

    /// <summary>
    /// Publish a random state onto <paramref name="bus"/>. With <paramref name="cardKey"/> null every
    /// card is reshuffled; otherwise only the matching card's source runs.
    /// </summary>
    public void Randomize(JournalEventBus bus, Random rng, string? cardKey = null)
    {
        foreach (var source in Sources)
        {
            if (cardKey is not null && !string.Equals(source.CardKey, cardKey, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var line in source.Sample(rng))
                if (JournalEntry.TryParse(line, historical: false, out var entry))
                    bus.Publish(entry);
        }
    }
}
