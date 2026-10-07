using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EDNexus.Core.Twitch;

/// <summary>The long-lived, per-broadcaster credential minted by the EBS after a successful login.</summary>
public sealed record EbsTokenResponse
{
    /// <summary>The opaque, long-lived bearer token the desktop app persists and uses to authenticate <c>POST /api/update-state</c>.</summary>
    [JsonPropertyName("token")] public string Token { get; init; } = "";

    /// <summary>The broadcaster's Twitch user/channel id, resolved server-side by the EBS.</summary>
    [JsonPropertyName("channelId")] public string ChannelId { get; init; } = "";

    /// <summary>The broadcaster's Twitch display name (falls back to their login name), for "Logged in as ...".</summary>
    [JsonPropertyName("username")] public string Username { get; init; } = "";
}

/// <summary>Thrown when the EBS rejects a login/token/revoke request (bad code, PKCE mismatch, expired session, etc.).</summary>
public sealed class EbsAuthApiException : Exception
{
    public int? StatusCode { get; }

    public EbsAuthApiException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
}

/// <summary>
/// Transport for the desktop↔EBS OAuth calls the login flow needs. Pure transport — no token
/// storage or policy. Note this only ever talks to the EBS, never to <c>*.twitch.tv</c> — the EBS is
/// the one that speaks to Twitch, on the broadcaster's behalf, using its own client secret.
/// </summary>
public interface IEbsAuthApiClient
{
    /// <summary>
    /// Exchanges the authorization code the EBS handed back via the loopback redirect (plus the PKCE
    /// code verifier that proves this is the same client that started the flow) for the long-lived
    /// EBS-issued token.
    /// </summary>
    Task<EbsTokenResponse> ExchangeCodeAsync(string tokenEndpoint, string code, string codeVerifier, string redirectUri, CancellationToken ct = default);

    /// <summary>
    /// Logout: asks the EBS to revoke the token (and its underlying Twitch grant), which also takes
    /// the card off the air. Throws when the EBS is unreachable or answers 5xx, so the caller can
    /// retry; a 4xx is not an error, since the EBS answers 200 even for a token it no longer knows.
    /// </summary>
    Task RevokeAsync(string revokeEndpoint, string token, CancellationToken ct = default);
}

/// <summary>Default <see cref="IEbsAuthApiClient"/> backed by a real (or injected, for tests) <see cref="HttpClient"/>.</summary>
public sealed class EbsAuthApiClient : IEbsAuthApiClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public EbsAuthApiClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
    }

    public async Task<EbsTokenResponse> ExchangeCodeAsync(string tokenEndpoint, string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        // The code and the PKCE verifier are what the token is minted from: never put them on the wire
        // in cleartext, whatever the settings say.
        if (!TwitchOAuthOptions.IsSecureEbsUrl(tokenEndpoint))
            throw new EbsAuthApiException("Refusing to send the sign-in code to a non-https address.");

        var payload = new Dictionary<string, string>
        {
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        };

        using var content = JsonContent(payload);
        using var response = await _http.PostAsync(tokenEndpoint, content, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new EbsAuthApiException($"The EBS rejected the token exchange: HTTP {(int)response.StatusCode} — {text}", (int)response.StatusCode);

        try
        {
            var token = JsonSerializer.Deserialize<EbsTokenResponse>(text);
            if (token is null || string.IsNullOrWhiteSpace(token.Token) || string.IsNullOrWhiteSpace(token.ChannelId))
                throw new EbsAuthApiException("The EBS returned an empty token response.");
            return token;
        }
        catch (JsonException ex)
        {
            throw new EbsAuthApiException($"Unparseable EBS token response: {ex.Message}");
        }
    }

    public async Task RevokeAsync(string revokeEndpoint, string token, CancellationToken ct = default)
    {
        if (!TwitchOAuthOptions.IsSecureEbsUrl(revokeEndpoint))
            throw new EbsAuthApiException("Refusing to send the Twitch token to a non-https address.");

        using var request = new HttpRequestMessage(HttpMethod.Post, revokeEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        // The EBS answers 200 even for a token it no longer knows, so a 4xx has nothing to retry.
        // A 5xx means it failed partway (e.g. clearing the card) and the caller should try again.
        if ((int)response.StatusCode >= 500)
            throw new EbsAuthApiException($"EBS revoke failed: HTTP {(int)response.StatusCode}", (int)response.StatusCode);
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
