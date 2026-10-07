using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using EDNexus.Ebs.Options;

namespace EDNexus.Ebs.Security;

/// <summary>
/// A minimal, dependency-free HS256 JWT signer scoped to exactly the claim shape Twitch's Extensions
/// platform uses. Twitch extension secrets are HMAC keys (not RSA/ECDSA), so a small hand-rolled
/// implementation avoids pulling in a general-purpose JWT library for a single algorithm.
/// </summary>
public sealed class TwitchExtensionJwtService : ITwitchExtensionJwtService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly TwitchEbsOptions _options;
    private readonly TimeProvider _timeProvider;

    public TwitchExtensionJwtService(IOptions<TwitchEbsOptions> options, TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string CreateExternalServiceToken(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        var key = Convert.FromBase64String(_options.ExtensionSecret);
        var now = _timeProvider.GetUtcNow();
        var exp = now.AddSeconds(Math.Max(30, _options.OutboundTokenLifetimeSeconds));
        var userId = string.IsNullOrWhiteSpace(_options.OwnerUserId) ? FallbackUserId : _options.OwnerUserId.Trim();
        return CreateExternalServiceToken(channelId, userId, key, now, exp);
    }

    /// <summary>
    /// The <c>user_id</c> claim used when <see cref="TwitchEbsOptions.OwnerUserId"/> is not configured.
    /// Twitch documents the claim as the extension owner's user id; this placeholder is the behaviour
    /// the service shipped with and is kept so an unconfigured deployment does not change.
    /// </summary>
    internal const string FallbackUserId = "ednexus_ebs";

    /// <summary>Signing entry point that takes the raw key bytes and timestamps directly (used by tests).</summary>
    internal static string CreateExternalServiceToken(string channelId, string userId, byte[] key, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var header = new { alg = "HS256", typ = "JWT" };
        var payload = new Dictionary<string, object>
        {
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["user_id"] = userId,
            ["role"] = "external",
            ["channel_id"] = channelId,
            ["pubsub_perms"] = new { send = new[] { "broadcast" } },
        };

        var headerSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header, SerializerOptions));
        var payloadSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions));
        var signingInput = Encoding.ASCII.GetBytes($"{headerSegment}.{payloadSegment}");
        var signature = Base64UrlEncode(HMACSHA256.HashData(key, signingInput));

        return $"{headerSegment}.{payloadSegment}.{signature}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
