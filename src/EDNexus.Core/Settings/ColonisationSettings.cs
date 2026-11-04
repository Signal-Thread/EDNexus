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

    /// <summary>
    /// When true, each live change to the cargo in the commander's <em>own</em> fleet carrier (a transfer
    /// to or from the ship, or a trade at the carrier's market) is reported to Raven Colonial, so a
    /// squadron can see what is stocked. Default <b>false</b>. It sends the carrier's market id and the
    /// commodities and tons that moved, authenticated with <see cref="RavenApiKey"/>. If another tool
    /// (the RavenColonial EDMC plugin, SrvSurvey) also reports the same carrier, every movement is counted
    /// twice; EDNexus watches for that, switches this off and says so. Independent of
    /// <see cref="SharedProjectLookup"/>: it neither looks up nor sends anything about construction sites.
    /// </summary>
    public bool ShareCarrierCargo { get; set; }

    /// <summary>
    /// The commander's Raven Colonial API key, sent in the <c>rcc-key</c> header when sharing carrier
    /// cargo. Kept in the settings file like the Inara key (and, like it, redacted from crash reports).
    /// Empty until the commander enters one.
    /// </summary>
    public string RavenApiKey { get; set; } = "";

    /// <summary>
    /// True when <paramref name="key"/> could be an API key: printable ASCII with no spaces, which is
    /// all an HTTP header value can safely carry. Says nothing about whether Raven Colonial accepts it.
    /// </summary>
    public static bool IsPlausibleRavenApiKey(string? key)
    {
        var trimmed = key?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.All(c => c is > ' ' and <= '~');
    }

    /// <summary>True when carrier cargo is both opted in and has a key to send it with: the one switch the sync reads.</summary>
    public bool CarrierSharingActive => ShareCarrierCargo && !string.IsNullOrWhiteSpace(RavenApiKey);
}
