using EDNexus.Core.Twitch;

namespace EDNexus.Tests.Twitch;

/// <summary>Scriptable <see cref="IEbsAuthApiClient"/> double so <see cref="TwitchAuthService"/> can be tested without touching the network.</summary>
internal sealed class FakeEbsAuthApiClient : IEbsAuthApiClient
{
    public Func<string, string, string, EbsTokenResponse>? OnExchange { get; set; }

    public int ExchangeCalls { get; private set; }
    public int RevokeCalls { get; private set; }
    public string? LastRevokedToken { get; private set; }

    public Task<EbsTokenResponse> ExchangeCodeAsync(string tokenEndpoint, string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        ExchangeCalls++;
        var result = OnExchange?.Invoke(code, codeVerifier, redirectUri)
            ?? throw new InvalidOperationException("OnExchange not configured");
        return Task.FromResult(result);
    }

    public Task RevokeAsync(string revokeEndpoint, string token, CancellationToken ct = default)
    {
        RevokeCalls++;
        LastRevokedToken = token;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fake browser launcher that signals <see cref="Opened"/> once <see cref="Open"/> has actually
/// captured the authorize URL, so a paired <see cref="FakeCallbackListener"/> can deterministically
/// wait for it instead of relying on an unordered <c>Task.Yield()</c> race.
/// </summary>
internal sealed class FakeBrowserLauncher : IBrowserLauncher
{
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The most recent URL passed to <see cref="Open"/>.</summary>
    public string? LastUrl { get; private set; }

    /// <summary>Completes once <see cref="Open"/> has been called at least once.</summary>
    public Task Opened => _opened.Task;

    public void Open(string url)
    {
        LastUrl = url;
        _opened.TrySetResult();
    }
}

/// <summary>
/// Fake loopback listener: returns a pre-scripted callback payload instead of opening a real socket.
/// When constructed with a <see cref="FakeBrowserLauncher"/>, it awaits the launcher's
/// <see cref="FakeBrowserLauncher.Opened"/> signal before evaluating the result factory — this is an
/// explicit synchronization point (not a bare <c>Task.Yield()</c>), so tests can reliably read the
/// PKCE <c>state</c> off the just-captured authorize URL and echo it back, with no race between the
/// service opening the browser and the listener resolving.
/// </summary>
internal sealed class FakeCallbackListener : IOAuthCallbackListener
{
    private readonly FakeBrowserLauncher? _browser;
    private readonly Func<IReadOnlyDictionary<string, string>>? _result;
    private readonly OperationCanceledException? _cancel;

    /// <summary>When set, <see cref="Listen"/> fails as a port that cannot be bound would.</summary>
    public Exception? ListenFailure { get; set; }

    /// <summary>The redirect the fake reports as bound; defaults to the one it was asked for.</summary>
    public Uri? BoundRedirect { get; set; }

    /// <summary>True once <see cref="Listen"/> has returned, i.e. the listener is bound.</summary>
    public bool Listening { get; private set; }

    /// <summary>The redirect <see cref="Listen"/> was last asked for.</summary>
    public Uri? RequestedRedirect { get; private set; }

    /// <summary>The state <see cref="Listen"/> was told to expect.</summary>
    public string? ExpectedState { get; private set; }

    /// <summary>True once the session handed out has been disposed, i.e. the port is released.</summary>
    public bool Released { get; private set; }

    public FakeCallbackListener(FakeBrowserLauncher browser, Func<IReadOnlyDictionary<string, string>> result)
    {
        _browser = browser;
        _result = result;
    }

    public FakeCallbackListener(IReadOnlyDictionary<string, string> result) => _result = () => result;
    public FakeCallbackListener(OperationCanceledException toThrow) => _cancel = toThrow;

    public IOAuthCallbackSession Listen(Uri redirectUri, string expectedState, CancellationToken ct)
    {
        if (ListenFailure is not null) throw ListenFailure;

        RequestedRedirect = redirectUri;
        ExpectedState = expectedState;
        Listening = true;
        return new Session(this, BoundRedirect ?? redirectUri, ct);
    }

    private sealed class Session(FakeCallbackListener owner, Uri redirectUri, CancellationToken ct) : IOAuthCallbackSession
    {
        public Uri RedirectUri { get; } = redirectUri;

        public Task<IReadOnlyDictionary<string, string>> Callback => WaitAsync();

        private async Task<IReadOnlyDictionary<string, string>> WaitAsync()
        {
            if (owner._cancel is not null)
                throw owner._cancel;

            if (owner._browser is not null)
                await owner._browser.Opened.WaitAsync(ct).ConfigureAwait(false);

            return owner._result!.Invoke();
        }

        public void Dispose() => owner.Released = true;
    }
}

/// <summary>Records whether the listener was already bound when the browser was asked to open, and can fail doing so.</summary>
internal sealed class ProbingBrowserLauncher(FakeCallbackListener listener, bool fail = false) : IBrowserLauncher
{
    public bool ListeningWhenOpened { get; private set; }
    public string? LastUrl { get; private set; }

    public void Open(string url)
    {
        ListeningWhenOpened = listener.Listening;
        LastUrl = url;
        if (fail) throw new InvalidOperationException("no default browser");
    }
}
