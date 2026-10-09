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
}
