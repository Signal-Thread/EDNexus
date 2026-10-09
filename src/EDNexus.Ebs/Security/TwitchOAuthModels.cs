using System.Text.Json.Serialization;

namespace EDNexus.Ebs.Security;

/// <summary>Token grant returned by Twitch's <c>/oauth2/token</c> endpoint (authorization-code exchange or refresh).</summary>
public sealed record TwitchTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("scope")] public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "";
}

/// <summary>A single entry from Twitch's Helix <c>GET /users</c> response.</summary>
public sealed record TwitchUser
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("login")] public string Login { get; init; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = "";
}

/// <summary>Thrown when Twitch rejects a token/user request the EBS makes on the broadcaster's behalf (bad code, revoked refresh token, etc.).</summary>
public sealed class TwitchOAuthException : Exception
{
    /// <summary>The HTTP status Twitch answered with, or null when the failure was not an HTTP rejection (an unparseable or empty body).</summary>
    public int? StatusCode { get; }

    public TwitchOAuthException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;

    /// <summary>
    /// True only when Twitch said the grant itself is dead: a <c>400</c>/<c>401</c> for the refresh
    /// token (<c>Invalid refresh token</c>, <c>invalid_grant</c>) — the commander revoked access or the
    /// token expired. Everything else is transient and the grant must be left alone: a <c>5xx</c>, a
    /// <c>429</c>, a timeout or an unparseable body says nothing about the grant, and so does
    /// <c>invalid client</c>/<c>invalid client secret</c>, which is THIS service's misconfiguration
    /// (a rotated secret) and would otherwise log every broadcaster out at once.
    /// </summary>
    public bool IsGrantRejection =>
        StatusCode is 400 or 401
        && !Message.Contains("invalid client", StringComparison.OrdinalIgnoreCase);
}
