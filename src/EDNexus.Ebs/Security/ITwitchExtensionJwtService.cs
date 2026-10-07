namespace EDNexus.Ebs.Security;

/// <summary>
/// Signs the compact HS256 JWT the EBS mints to authenticate its own calls to the Twitch Helix
/// PubSub API (role "external"). There is deliberately no inbound verification: the desktop client
/// authenticates with the EBS-issued broadcaster token (<see cref="EDNexus.Ebs.Services.IBroadcasterTokenStore"/>),
/// and the extension frontend's Twitch JWTs are not accepted by any endpoint.
/// </summary>
public interface ITwitchExtensionJwtService
{
    /// <summary>
    /// Mints a short-lived "external" role JWT, signed with the Extension Secret, suitable for
    /// authorizing a call to <c>POST https://api.twitch.tv/helix/extensions/pubsub</c> on behalf of
    /// the given broadcaster channel.
    /// </summary>
    /// <param name="channelId">The broadcaster channel id the message will be sent to.</param>
    string CreateExternalServiceToken(string channelId);
}
