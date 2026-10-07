using EDNexus.Core.Settings;

namespace EDNexus.Core.Twitch;

/// <summary>
/// A snapshot of the current Twitch session, safe to read from a UI thread without touching
/// <see cref="AppSettings"/> directly.
/// </summary>
public sealed record TwitchSessionState(bool LoggedIn, string? Username, string? ChannelId);

/// <summary>
/// Owns the desktop side of the EBS-mediated Twitch login flow: builds the EBS's authorization URL,
/// opens the commander's browser, runs a temporary loopback listener for the redirect, exchanges the
/// resulting code (with PKCE) for the EBS's long-lived opaque token, and persists it via
/// <see cref="SettingsStore"/> (the same convention as the Inara API key).
/// </summary>
/// <remarks>
/// Standalone by design — this does not depend on the journal bus, <c>EngineHost</c>, or any UI. The
/// desktop app never talks to <c>*.twitch.tv</c> directly: the EBS is the registered Twitch
/// application and performs the real Twitch OAuth handshake server-side, so there is no client-side
/// Twitch token refresh here — the EBS keeps the underlying Twitch grant alive on its own. If the EBS
/// ever reports the token as no longer valid (e.g. the commander revoked access on Twitch), the
/// caller should treat that the same as "logged out" and prompt <see cref="LoginAsync"/> again.
/// </remarks>
public sealed class TwitchAuthService
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly TwitchOAuthOptions _options;
    private readonly IEbsAuthApiClient _api;
    private readonly IBrowserLauncher _browser;
    private readonly IOAuthCallbackListener _listener;
    private readonly EbsCleanupQueue? _cleanup;

    /// <param name="cleanup">
    /// Where a revoke the EBS did not acknowledge goes to be retried. Without one, a failed revoke
    /// leaves the token valid on the EBS and the card on the air.
    /// </param>
    public TwitchAuthService(
        AppSettings settings,
        SettingsStore store,
        TwitchOAuthOptions options,
        IEbsAuthApiClient? api = null,
        IBrowserLauncher? browser = null,
        IOAuthCallbackListener? listener = null,
        EbsCleanupQueue? cleanup = null)
    {
        _cleanup = cleanup;
        _settings = settings;
        _store = store;
        _options = options;
        _api = api ?? new EbsAuthApiClient();
        _browser = browser ?? new SystemBrowserLauncher();
        _listener = listener ?? new LoopbackOAuthCallbackListener();
    }

    private TwitchSettings Twitch => _settings.Twitch;

    /// <summary>True once a broadcaster identity and EBS-issued token are on file.</summary>
    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(Twitch.Token) && !string.IsNullOrWhiteSpace(Twitch.ChannelId);

    /// <summary>The current EBS-issued bearer token, if logged in.</summary>
    public string? Token => IsLoggedIn ? Twitch.Token : null;

    /// <summary>A UI/consumer-friendly snapshot of the current session.</summary>
    public TwitchSessionState State => new(IsLoggedIn, Twitch.Username, Twitch.ChannelId);

    /// <summary>
    /// Runs the full desktop↔EBS login flow: opens the browser to the EBS's authorization page
    /// (which in turn drives the real Twitch consent flow), waits on a temporary loopback listener
    /// for the redirect, exchanges the resulting code (with PKCE) for the EBS's long-lived token, and
    /// persists the result. Never throws for flow failures (cancellation, denial, timeout, transport
    /// errors) — those come back as a non-success <see cref="TwitchAuthResult"/>.
    /// </summary>
    public async Task<TwitchAuthResult> LoginAsync(CancellationToken ct = default)
    {
        // The code, the PKCE verifier and the token the EBS answers with would all cross in cleartext.
        if (!TwitchOAuthOptions.IsSecureEbsUrl(_options.EbsBaseUrl))
            return TwitchAuthResult.Failed(TwitchAuthStatus.Error, TwitchOAuthOptions.InsecureEbsUrlMessage);

        var verifier = PkceUtility.GenerateCodeVerifier();
        var challenge = PkceUtility.ComputeCodeChallenge(verifier);
        var state = PkceUtility.GenerateState();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.LoginTimeout);

        // Listening is confirmed before the browser opens: a redirect that arrives before anything
        // is bound is lost, and a bind failure has to be reported rather than leave the commander
        // approving a login that can never complete.
        IOAuthCallbackSession session;
        try
        {
            session = _listener.Listen(new Uri(_options.RedirectUri), state, timeoutCts.Token);
        }
        catch (Exception ex)
        {
            return TwitchAuthResult.Failed(TwitchAuthStatus.Error, $"Could not start the local OAuth callback listener: {ex.Message}");
        }

        // The redirect actually bound, which is not the configured one if its port was taken.
        string redirectUri;
        IReadOnlyDictionary<string, string> callback;
        // Disposed as soon as the callback is in (or the attempt fails), so the port is free again
        // whatever went wrong — including the browser failing to open.
        using (session)
        {
            redirectUri = session.RedirectUri.ToString();
            try
            {
                _browser.Open(BuildAuthorizeUrl(redirectUri, challenge, state));
            }
            catch (Exception ex)
            {
                return TwitchAuthResult.Failed(TwitchAuthStatus.Error, $"Could not open the default browser: {ex.Message}");
            }

            try
            {
                callback = await session.Callback.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ct.IsCancellationRequested
                    ? TwitchAuthResult.Failed(TwitchAuthStatus.Cancelled)
                    : TwitchAuthResult.Failed(TwitchAuthStatus.Timeout);
            }
            catch (Exception ex)
            {
                return TwitchAuthResult.Failed(TwitchAuthStatus.Error, ex.Message);
            }
        }

        if (callback.TryGetValue("error", out var error))
        {
            var denied = string.Equals(error, "access_denied", StringComparison.OrdinalIgnoreCase);
            var message = callback.GetValueOrDefault("error_description", error);
            return TwitchAuthResult.Failed(denied ? TwitchAuthStatus.Denied : TwitchAuthStatus.Error, message);
        }

        if (!callback.TryGetValue("state", out var returnedState) || !string.Equals(returnedState, state, StringComparison.Ordinal))
            return TwitchAuthResult.Failed(TwitchAuthStatus.Error, "State mismatch on the OAuth callback — aborting to be safe.");

        if (!callback.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            return TwitchAuthResult.Failed(TwitchAuthStatus.Error, "The EBS did not return an authorization code.");

        try
        {
            var token = await _api.ExchangeCodeAsync(_options.TokenEndpoint, code, verifier, redirectUri, ct).ConfigureAwait(false);
            Persist(token);
            return TwitchAuthResult.Ok(token.Username, token.ChannelId);
        }
        catch (EbsAuthApiException ex)
        {
            return TwitchAuthResult.Failed(TwitchAuthStatus.Error, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return TwitchAuthResult.Failed(TwitchAuthStatus.Cancelled);
        }
    }

    /// <summary>
    /// Asks the EBS to revoke the current token, which also takes the card off the air, and clears
    /// the persisted session. Local state is cleared either way; a revoke the EBS did not
    /// acknowledge is handed to the cleanup queue to retry.
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(Twitch.Token))
        {
            var token = Twitch.Token!;
            // Never sent to an insecure address in the first place, so there is nothing to revoke
            // there and nothing worth queueing (the queue would refuse it too).
            if (TwitchOAuthOptions.IsSecureEbsUrl(_options.RevokeEndpoint))
            {
                try { await _api.RevokeAsync(_options.RevokeEndpoint, token, ct).ConfigureAwait(false); }
                catch { _cleanup?.Enqueue(EbsCleanupKind.Revoke, _options.RevokeEndpoint, token); }
            }
        }
        ClearSession();
    }

    private void Persist(EbsTokenResponse token)
    {
        Twitch.Token = token.Token;
        Twitch.ChannelId = token.ChannelId;
        Twitch.Username = token.Username;
        _store.Save(_settings);
    }

    private void ClearSession()
    {
        Twitch.Token = null;
        Twitch.ChannelId = null;
        Twitch.Username = null;
        _store.Save(_settings);
    }

    private string BuildAuthorizeUrl(string redirectUri, string codeChallenge, string state) =>
        $"{_options.AuthorizeEndpoint}" +
        $"?redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&response_type=code" +
        $"&state={Uri.EscapeDataString(state)}" +
        $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
        $"&code_challenge_method=S256";
}
