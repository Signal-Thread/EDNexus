using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using EDNexus.Ebs.Options;

namespace EDNexus.Ebs.Security;

/// <summary>
/// A minimal, dependency-free HS256 JWT signer/verifier scoped to exactly the claim shape Twitch's
/// Extensions platform uses. Twitch extension secrets are HMAC keys (not RSA/ECDSA), so a small
/// hand-rolled implementation avoids pulling in a general-purpose JWT library for a single algorithm.
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
    public TwitchJwtValidationResult Validate(string token, IReadOnlyCollection<string>? allowedRoles = null)
    {
        if (string.IsNullOrWhiteSpace(_options.ExtensionSecret))
        {
            return TwitchJwtValidationResult.Failure("Extension secret is not configured.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(_options.ExtensionSecret);
        }
        catch (FormatException)
        {
            return TwitchJwtValidationResult.Failure("Extension secret is not valid base64.");
        }

        return Validate(token, key, _options.ClockSkewSeconds, _timeProvider.GetUtcNow(), allowedRoles);
    }

    /// <summary>Validation entry point that takes the raw key bytes directly (used by tests).</summary>
    internal static TwitchJwtValidationResult Validate(
        string token,
        byte[] key,
        int clockSkewSeconds,
        DateTimeOffset now,
        IReadOnlyCollection<string>? allowedRoles = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return TwitchJwtValidationResult.Failure("Token is empty.");
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return TwitchJwtValidationResult.Failure("Token does not have the expected header.payload.signature shape.");
        }

        byte[] headerBytes, payloadBytes, signatureBytes;
        try
        {
            headerBytes = Base64UrlDecode(parts[0]);
            payloadBytes = Base64UrlDecode(parts[1]);
            signatureBytes = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return TwitchJwtValidationResult.Failure("Token segments are not valid base64url.");
        }

        using var header = JsonDocument.Parse(headerBytes);
        if (!header.RootElement.TryGetProperty("alg", out var algElement) ||
            !string.Equals(algElement.GetString(), "HS256", StringComparison.Ordinal))
        {
            return TwitchJwtValidationResult.Failure("Unsupported or missing JWT algorithm; only HS256 is accepted.");
        }

        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var expectedSignature = HMACSHA256.HashData(key, signingInput);
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, signatureBytes))
        {
            return TwitchJwtValidationResult.Failure("Signature verification failed.");
        }

        // Nothing in the payload is read until the signature above has verified.
        using var payload = JsonDocument.Parse(payloadBytes);
        var root = payload.RootElement;

        if (!root.TryGetProperty("exp", out var expElement) || !expElement.TryGetInt64(out var exp))
        {
            return TwitchJwtValidationResult.Failure("Token is missing the 'exp' claim.");
        }

        var expiry = DateTimeOffset.FromUnixTimeSeconds(exp);
        if (now > expiry.AddSeconds(clockSkewSeconds))
        {
            return TwitchJwtValidationResult.Failure("Token has expired.");
        }

        if (root.TryGetProperty("nbf", out var nbfElement))
        {
            if (nbfElement.ValueKind != JsonValueKind.Number || !nbfElement.TryGetInt64(out var nbf))
            {
                return TwitchJwtValidationResult.Failure("Token has a malformed 'nbf' claim.");
            }

            // Compared in whole Unix seconds, like nbf itself, so an out-of-range value can't throw the
            // way DateTimeOffset.FromUnixTimeSeconds would.
            if (nbf > now.ToUnixTimeSeconds() + clockSkewSeconds)
            {
                return TwitchJwtValidationResult.Failure("Token is not yet valid.");
            }
        }

        var channelId = GetStringClaim(root, "channel_id");
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return TwitchJwtValidationResult.Failure("Token is missing the 'channel_id' claim.");
        }

        var role = GetStringClaim(root, "role");
        if (role is not ("broadcaster" or "moderator" or "viewer" or "external"))
        {
            return TwitchJwtValidationResult.Failure(
                "Unsupported or missing 'role' claim; only broadcaster, moderator, viewer, and external are accepted.");
        }

        if (allowedRoles is not null && !allowedRoles.Contains(role, StringComparer.Ordinal))
        {
            return TwitchJwtValidationResult.Failure($"Token role '{role}' is not one of the allowed roles.");
        }

        var claims = new TwitchExtensionClaims(
            channelId,
            GetStringClaim(root, "user_id"),
            GetStringClaim(root, "opaque_user_id"),
            role,
            exp);
        return TwitchJwtValidationResult.Success(claims);
    }

    /// <inheritdoc />
    public string CreateExternalServiceToken(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        var key = Convert.FromBase64String(_options.ExtensionSecret);
        var now = _timeProvider.GetUtcNow();
        var exp = now.AddSeconds(Math.Max(30, _options.OutboundTokenLifetimeSeconds));
        return CreateExternalServiceToken(channelId, key, now, exp);
    }

    /// <summary>Signing entry point that takes the raw key bytes and timestamps directly (used by tests).</summary>
    internal static string CreateExternalServiceToken(string channelId, byte[] key, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var header = new { alg = "HS256", typ = "JWT" };
        var payload = new Dictionary<string, object>
        {
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["user_id"] = "ednexus_ebs",
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

    /// <summary>Reads a string claim, treating an absent or non-string value as missing.</summary>
    private static string? GetStringClaim(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }
}
