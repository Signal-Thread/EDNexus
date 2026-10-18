namespace EDNexus.Core.Twitch;

/// <summary>
/// The warning the app shows when publishing the stream card is failing for a reason the commander
/// cannot fix from their side. Kept apart from the UI so the wording and the rule for when it appears
/// and clears are tested.
/// </summary>
public static class StreamCardWarning
{
    /// <summary>
    /// What to show after a publish, or <c>null</c> for nothing. Only a <see cref="StreamStatePublishStatus.BackendRejectedByTwitch"/>
    /// raises one: a plain failure (the network dropped, a rate limit) already has its own status line
    /// and clears itself on the next success, whereas a credential the backend operator must fix would
    /// otherwise go unnoticed, since the card keeps looking fine on the commander's own machine.
    /// </summary>
    public static string? For(StreamStatePublishResult result) =>
        result.Status == StreamStatePublishStatus.BackendRejectedByTwitch
            ? "Twitch is rejecting the EDNexus backend's credentials, so viewers are not getting live updates. " +
              "This is a problem with the backend's configuration, not with your sign-in; it keeps retrying and " +
              "the card will start updating again as soon as it is fixed."
            : null;

    /// <summary>
    /// True when this result shows the problem is over: the card reached viewers (or was taken down
    /// cleanly). A failure of some other kind proves nothing either way, so it leaves a shown warning alone.
    /// </summary>
    public static bool Clears(StreamStatePublishResult result) => result.IsSuccess;
}
