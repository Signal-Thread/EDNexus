namespace EDNexus.Core.Settings;

/// <summary>Options for the colonisation card.</summary>
public sealed class ColonisationSettings
{
    /// <summary>
    /// When true (the default, which is how the card has always behaved), docking at a construction
    /// depot looks the build up on the shared Raven Colonial tracker to show what the squadron still
    /// owes. That sends the depot's system name and market id to a third-party service, so a commander
    /// who would rather it didn't can turn it off; the card then shows only the local depot snapshot.
    /// </summary>
    public bool SharedProjectLookup { get; set; } = true;

    /// <summary>
    /// When true, each live <c>ColonisationContribution</c> (a delivery you make at a construction depot)
    /// is also reported to the shared Raven Colonial project for that depot, so squadmates see the
    /// remaining need fall. Default <b>false</b>: it sends your commander name and what you delivered.
    /// Raven Colonial's endpoint is unauthenticated (anyone can post under any name) and simply adds up
    /// what it is told, so it double-counts if another tool also reports your deliveries to the same
    /// project. Only takes effect while <see cref="SharedProjectLookup"/> is also on, since both talk to
    /// the same service.
    /// </summary>
    public bool ShareDeliveries { get; set; }
}
