namespace EDNexus.Ebs.Security;

/// <summary>
/// The subset of claims Twitch embeds in an Extension JWT that the EBS cares about, as returned by a
/// successful <see cref="ITwitchExtensionJwtService.Validate"/> (see there for exactly what is checked).
/// See https://dev.twitch.tv/docs/extensions/reference/#jwt-schema for the full schema.
/// </summary>
/// <param name="ChannelId">The broadcaster's channel/user id the extension is active on. Validation requires a non-empty value.</param>
/// <param name="UserId">The identified viewer/broadcaster user id, if identity was shared. Not validated; null when absent.</param>
/// <param name="OpaqueUserId">A pseudonymous id for the user. Not validated; null when absent (e.g. on "external" tokens).</param>
/// <param name="Role">Exactly one of "broadcaster", "moderator", "viewer", or "external"; validation rejects anything else.</param>
/// <param name="ExpiresAtUnixSeconds">The <c>exp</c> claim, seconds since the Unix epoch.</param>
public sealed record TwitchExtensionClaims(
    string ChannelId,
    string? UserId,
    string? OpaqueUserId,
    string Role,
    long ExpiresAtUnixSeconds)
{
    /// <summary>True when the token's role identifies the channel's broadcaster.</summary>
    public bool IsBroadcaster => string.Equals(Role, "broadcaster", StringComparison.OrdinalIgnoreCase);
}
