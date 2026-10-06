namespace EDNexus.Ebs.Security;

/// <summary>
/// Signs and verifies the compact HS256 JWTs used by the Twitch Extensions platform: both the
/// tokens Twitch issues to the extension frontend (which the EBS must verify) and the tokens the
/// EBS itself mints to authenticate its own calls to the Helix PubSub API (role "external").
/// </summary>
public interface ITwitchExtensionJwtService
{
    /// <summary>
    /// Validates a JWT issued by Twitch for the extension (or minted by
    /// <see cref="CreateExternalServiceToken"/>) using the base64-encoded Extension Secret. The token is
    /// valid only when every one of these checks passes:
    /// <list type="number">
    /// <item><description>The header's <c>alg</c> is <c>HS256</c> and the HMAC-SHA256 signature matches
    /// (compared in constant time). No payload claim is read before this passes.</description></item>
    /// <item><description><c>exp</c> is present and no further in the past than
    /// <see cref="EDNexus.Ebs.Options.TwitchEbsOptions.ClockSkewSeconds"/>.</description></item>
    /// <item><description><c>nbf</c>, if present, is a numeric date no further in the future than that same
    /// skew.</description></item>
    /// <item><description><c>channel_id</c> is a non-empty string.</description></item>
    /// <item><description><c>role</c> is exactly one of Twitch's extension roles — <c>broadcaster</c>,
    /// <c>moderator</c>, <c>viewer</c>, or <c>external</c> (case-sensitive) — and, when
    /// <paramref name="allowedRoles"/> is supplied, also one of those.</description></item>
    /// </list>
    /// No other claim (e.g. <c>iat</c>, <c>user_id</c>, <c>opaque_user_id</c>, <c>pubsub_perms</c>) is
    /// checked.
    /// </summary>
    /// <param name="token">The compact JWT string, without the "Bearer " prefix.</param>
    /// <param name="allowedRoles">
    /// Optional roles the caller accepts, matched exactly. <see langword="null"/> accepts any of the four
    /// extension roles; an empty collection rejects every token.
    /// </param>
    TwitchJwtValidationResult Validate(string token, IReadOnlyCollection<string>? allowedRoles = null);

    /// <summary>
    /// Mints a short-lived "external" role JWT, signed with the Extension Secret, suitable for
    /// authorizing a call to <c>POST https://api.twitch.tv/helix/extensions/pubsub</c> on behalf of
    /// the given broadcaster channel.
    /// </summary>
    /// <param name="channelId">The broadcaster channel id the message will be sent to.</param>
    string CreateExternalServiceToken(string channelId);
}
