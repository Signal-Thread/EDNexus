using System.Diagnostics.CodeAnalysis;
using EDNexus.Ebs.Options;
using EDNexus.Ebs.Security;
using EDNexus.Ebs.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace EDNexus.Ebs.Endpoints;

/// <summary>
/// The EBS-mediated Twitch OAuth flow: the desktop client only ever talks to these endpoints, never
/// to Twitch directly. See <c>README.md</c> for the full request/response contract.
/// </summary>
public static class OAuthEndpoints
{
    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/oauth/authorize", HandleAuthorizeAsync);
        app.MapGet("/oauth/callback", HandleCallbackAsync);
        app.MapPost("/oauth/token", HandleTokenAsync);
        app.MapPost("/oauth/revoke", HandleRevokeAsync);
        return app;
    }

    /// <summary>
    /// Step 1: the desktop app opens the commander's browser here. Records the desktop's own
    /// loopback redirect/state/PKCE challenge, then redirects to Twitch's real consent page with the
    /// EBS's own registered redirect URI and a session id (as Twitch's <c>state</c>) standing in for it.
    /// </summary>
    private static IResult HandleAuthorizeAsync(
        HttpRequest request,
        IBroadcasterTokenStore store,
        IOptions<TwitchEbsOptions> twitchOptions,
        IOptions<EbsOptions> ebsOptions)
    {
        var redirectUri = request.Query["redirect_uri"].ToString();
        var state = request.Query["state"].ToString();
        var codeChallenge = request.Query["code_challenge"].ToString();
        var codeChallengeMethod = request.Query["code_challenge_method"].ToString();

        if (string.IsNullOrWhiteSpace(redirectUri) || string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(codeChallenge))
            return Results.Problem("redirect_uri, state, and code_challenge are all required.", statusCode: StatusCodes.Status400BadRequest);

        if (!string.IsNullOrEmpty(codeChallengeMethod) && !string.Equals(codeChallengeMethod, "S256", StringComparison.Ordinal))
            return Results.Problem("Only the S256 code_challenge_method is supported.", statusCode: StatusCodes.Status400BadRequest);

        if (!TryParseLoopbackRedirect(redirectUri, out _))
            return Results.Problem("redirect_uri must be an http loopback (localhost/127.0.0.1/[::1]) address.", statusCode: StatusCodes.Status400BadRequest);

        var twitch = twitchOptions.Value;
        var sessionId = store.CreateSession(redirectUri, state, codeChallenge, TimeSpan.FromMinutes(Math.Max(1, ebsOptions.Value.OAuthSessionTtlMinutes)));

        var scope = Uri.EscapeDataString(string.Join(' ', twitch.OAuthScopes));
        var twitchAuthorizeUrl =
            $"{twitch.AuthorizationEndpoint}" +
            $"?client_id={Uri.EscapeDataString(twitch.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(twitch.OAuthRedirectUri)}" +
            $"&response_type=code" +
            $"&scope={scope}" +
            $"&state={Uri.EscapeDataString(sessionId)}";

        return Results.Redirect(twitchAuthorizeUrl);
    }

    /// <summary>
    /// Step 2: Twitch redirects the commander's browser back here after they approve/deny the
    /// request. The EBS exchanges the code for Twitch tokens using its client secret (never exposed
    /// to the desktop client), identifies the broadcaster, and redirects the browser back to the
    /// desktop's own loopback listener with a one-time authorization code (not a Twitch token).
    /// </summary>
    private static async Task<IResult> HandleCallbackAsync(
        HttpRequest request,
        IBroadcasterTokenStore store,
        ITwitchOAuthClient twitchClient,
        IOptions<TwitchEbsOptions> twitchOptions,
        IOptions<EbsOptions> ebsOptions,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var sessionId = request.Query["state"].ToString();
        if (string.IsNullOrWhiteSpace(sessionId) || !store.TryConsumeSession(sessionId, out var session))
            return ErrorPage("This login link has expired or was already used. Please restart the login from EDNexus.");

        // /oauth/authorize only stores a redirect_uri that passed this same check. Re-parsing it here
        // means every redirect below is built from the validated Uri, never from the raw stored string.
        if (!TryParseLoopbackRedirect(session.DesktopRedirectUri, out var desktopRedirectUri))
            return ErrorPage("This login session is invalid. Please restart the login from EDNexus.");

        var error = request.Query["error"].ToString();
        if (!string.IsNullOrEmpty(error))
        {
            var description = request.Query["error_description"].ToString();
            return Results.Redirect(BuildDesktopRedirect(desktopRedirectUri, session.DesktopState, error: error, errorDescription: description));
        }

        var code = request.Query["code"].ToString();
        if (string.IsNullOrWhiteSpace(code))
            return Results.Redirect(BuildDesktopRedirect(desktopRedirectUri, session.DesktopState, error: "server_error", errorDescription: "Twitch did not return an authorization code."));

        try
        {
            var twitchToken = await twitchClient.ExchangeAuthorizationCodeAsync(code, twitchOptions.Value.OAuthRedirectUri, ct).ConfigureAwait(false);
            var user = await twitchClient.GetUserAsync(twitchToken.AccessToken, ct).ConfigureAwait(false);
            if (user is null || string.IsNullOrWhiteSpace(user.Id))
                return Results.Redirect(BuildDesktopRedirect(desktopRedirectUri, session.DesktopState, error: "server_error", errorDescription: "Could not retrieve the Twitch user profile."));

            var username = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Login : user.DisplayName;
            var twitchExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(twitchToken.ExpiresIn);

            var pending = new PendingBroadcasterAuth(
                user.Id,
                username,
                twitchToken.AccessToken,
                twitchToken.RefreshToken,
                twitchExpiresAtUtc,
                session.CodeChallenge,
                session.DesktopRedirectUri,
                default);

            var authCode = store.CreateAuthorizationCode(pending, TimeSpan.FromSeconds(Math.Max(10, ebsOptions.Value.OAuthCodeTtlSeconds)));
            return Results.Redirect(BuildDesktopRedirect(desktopRedirectUri, session.DesktopState, code: authCode));
        }
        catch (TwitchOAuthException ex)
        {
            logger.LogWarning(ex, "Twitch rejected the OAuth callback exchange.");
            return Results.Redirect(BuildDesktopRedirect(desktopRedirectUri, session.DesktopState, error: "server_error", errorDescription: "Twitch rejected the login."));
        }
    }

    /// <summary>
    /// Step 3: the desktop's loopback listener has the authorization code; it exchanges it here
    /// (proving possession of the PKCE verifier) for the long-lived, per-broadcaster token.
    /// </summary>
    private static IResult HandleTokenAsync(OAuthTokenExchangeRequest body, IBroadcasterTokenStore store)
    {
        if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.CodeVerifier) || string.IsNullOrWhiteSpace(body.RedirectUri))
            return Results.Problem("code, code_verifier, and redirect_uri are all required.", statusCode: StatusCodes.Status400BadRequest);

        if (!store.TryConsumeAuthorizationCode(body.Code, out var pending))
            return Results.Problem("invalid_grant: the authorization code is unknown, expired, or already used.", statusCode: StatusCodes.Status400BadRequest);

        if (!Pkce.Verify(body.CodeVerifier, pending.CodeChallenge))
            return Results.Problem("invalid_grant: the PKCE code_verifier does not match.", statusCode: StatusCodes.Status400BadRequest);

        // RFC 6749 §4.1.3 / RFC 7636: redirect_uri presented here must match the one bound to the
        // authorization at /oauth/authorize — otherwise the parameter is decorative and this endpoint
        // silently drifts from the OAuth flow it claims to implement.
        if (!string.Equals(body.RedirectUri, pending.RedirectUri, StringComparison.Ordinal))
            return Results.Problem("invalid_grant: redirect_uri does not match the one used to start this authorization.", statusCode: StatusCodes.Status400BadRequest);

        var record = store.IssueToken(pending.ChannelId, pending.Username, pending.TwitchAccessToken, pending.TwitchRefreshToken, pending.TwitchExpiresAtUtc);
        return Results.Ok(new OAuthTokenIssuedResponse(record.Token, record.ChannelId, record.Username));
    }

    /// <summary>Best-effort logout: revokes the broadcaster's long-lived token and their underlying Twitch grant.</summary>
    private static async Task<IResult> HandleRevokeAsync(HttpRequest request, IBroadcasterTokenStore store, ITwitchOAuthClient twitchClient, CancellationToken ct)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.Ok(); // idempotent — nothing to revoke without a token

        var token = header["Bearer ".Length..].Trim();
        if (store.TryGetByToken(token, out var record))
        {
            try { await twitchClient.RevokeTokenAsync(record.TwitchAccessToken, ct).ConfigureAwait(false); }
            catch { /* best-effort */ }
            store.Revoke(token);
        }

        return Results.Ok();
    }

    private static string BuildDesktopRedirect(Uri desktopRedirectUri, string desktopState, string? code = null, string? error = null, string? errorDescription = null)
    {
        var query = new List<KeyValuePair<string, string?>> { new("state", desktopState) };
        if (!string.IsNullOrEmpty(code)) query.Add(new("code", code));
        if (!string.IsNullOrEmpty(error)) query.Add(new("error", error));
        if (!string.IsNullOrEmpty(errorDescription)) query.Add(new("error_description", errorDescription));

        // Serialized from the parsed Uri, so the Location header is exactly the address that passed
        // TryParseLoopbackRedirect. AddQueryString keeps any query string the desktop registered.
        return QueryHelpers.AddQueryString(desktopRedirectUri.AbsoluteUri, query);
    }

    // Exact host matching on purpose (not Uri.IsLoopback). Uri.Host keeps the brackets on IPv6
    // literals, so the IPv6 loopback has to be matched as "[::1]".
    private static bool TryParseLoopbackRedirect(string redirectUri, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && (uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "[::1]");

    private static IResult ErrorPage(string message) => Results.Content(
        $"""
         <!DOCTYPE html>
         <html><head><title>Login failed</title></head>
         <body style="font-family: sans-serif; background:#141414; color:#e0a030; text-align:center; padding-top: 10vh;">
         <h2>Login failed</h2><p>{message}</p>
         </body></html>
         """,
        "text/html",
        System.Text.Encoding.UTF8,
        StatusCodes.Status400BadRequest);
}
