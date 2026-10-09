using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace EDNexus.Core.Twitch;

/// <summary>
/// Opens the temporary loopback server the EBS redirects the commander's browser back to, and hands
/// out the one login attempt's result. One instance can serve any number of attempts, but each
/// <see cref="Listen"/> serves exactly one.
/// </summary>
public interface IOAuthCallbackListener
{
    /// <summary>
    /// Starts listening near <paramref name="redirectUri"/> and returns once the listener is actually
    /// bound, so the caller can open the browser knowing the redirect will be received. Throws when no
    /// listener could be bound. Requests that do not carry <paramref name="expectedState"/> are
    /// answered with an error page and otherwise ignored — a stray request from another local program
    /// or web page must not be able to end the login.
    /// </summary>
    /// <param name="redirectUri">The loopback redirect the login is configured with.</param>
    /// <param name="expectedState">The OAuth <c>state</c> the genuine redirect will echo back.</param>
    /// <param name="ct">Cancelling it (e.g. on timeout) stops the listener.</param>
    IOAuthCallbackSession Listen(Uri redirectUri, string expectedState, CancellationToken ct);
}

/// <summary>A bound loopback listener waiting for the redirect. Dispose releases the port.</summary>
public interface IOAuthCallbackSession : IDisposable
{
    /// <summary>
    /// The redirect URI that was actually bound — the requested one, unless its port was taken and
    /// another was used. This is what the authorization request and the code exchange must quote.
    /// </summary>
    Uri RedirectUri { get; }

    /// <summary>
    /// Completes with the query parameters (<c>code</c>/<c>state</c>, or <c>error</c>/<c>error_description</c>
    /// on denial) of the first request that carries the expected <c>state</c>. Faults with
    /// <see cref="OperationCanceledException"/> when the cancellation token given to
    /// <see cref="IOAuthCallbackListener.Listen"/> fires.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> Callback { get; }
}

/// <summary>
/// Real implementation backed by <see cref="HttpListener"/> — a temporary local HTTP server bound
/// only to loopback, torn down as soon as the callback (or a cancellation) arrives.
/// </summary>
/// <remarks>
/// The EBS accepts any port on <c>localhost</c>/<c>127.0.0.1</c>/<c>[::1]</c> as the redirect, so when
/// the preferred port is taken (a second EDNexus, or another program) an ephemeral one is used
/// instead of failing the login.
/// </remarks>
public sealed class LoopbackOAuthCallbackListener : IOAuthCallbackListener
{
    /// <summary>Ephemeral ports tried after the requested one is refused.</summary>
    private const int FallbackAttempts = 3;

    public IOAuthCallbackSession Listen(Uri redirectUri, string expectedState, CancellationToken ct)
    {
        Exception? firstFailure = null;

        for (var attempt = 0; attempt <= FallbackAttempts; attempt++)
        {
            Uri candidate;
            try
            {
                candidate = attempt == 0
                    ? redirectUri
                    : new UriBuilder(redirectUri) { Port = FreeLoopbackPort() }.Uri;
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
                break;
            }

            var listener = new HttpListener();
            listener.Prefixes.Add(new UriBuilder(candidate) { Path = "/", Query = null, Fragment = null }.Uri.ToString());
            try
            {
                listener.Start();
                return new Session(listener, candidate, expectedState, ct);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
                try { listener.Close(); } catch { /* never started */ }
            }
        }

        throw new InvalidOperationException(
            $"Could not bind the local OAuth callback listener on {redirectUri.Authority} — is another instance of EDNexus already running?",
            firstFailure);
    }

    /// <summary>A port the OS has just confirmed is free on loopback (it can still be taken before it is rebound; the caller retries).</summary>
    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private sealed class Session : IOAuthCallbackSession
    {
        private readonly HttpListener _listener;
        private readonly string _expectedState;
        private readonly CancellationToken _ct;
        private readonly CancellationTokenRegistration _registration;
        private int _disposed;

        public Session(HttpListener listener, Uri redirectUri, string expectedState, CancellationToken ct)
        {
            _listener = listener;
            RedirectUri = redirectUri;
            _expectedState = expectedState;
            _ct = ct;
            _registration = ct.Register(Stop);
            // Already listening, so the redirect cannot be missed however soon the browser opens.
            Callback = Task.Run(RunAsync);
        }

        public Uri RedirectUri { get; }

        public Task<IReadOnlyDictionary<string, string>> Callback { get; }

        private async Task<IReadOnlyDictionary<string, string>> RunAsync()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                {
                    throw new OperationCanceledException(_ct);
                }

                if (!string.Equals(context.Request.Url?.AbsolutePath, RedirectUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    Reject(context, 404);
                    continue;
                }

                var parameters = ParseQuery(context.Request.Url!.Query);
                if (!parameters.TryGetValue("state", out var state) || !StateMatches(state, _expectedState))
                {
                    // Not the redirect this login is waiting for. Keep waiting rather than let a stray
                    // request (or a page that guessed the port) abort the commander's sign-in.
                    Reject(context, 400);
                    continue;
                }

                await RespondAsync(context, parameters.ContainsKey("error")).ConfigureAwait(false);
                return parameters;
            }
        }

        private static bool StateMatches(string actual, string expected) =>
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

        private static void Reject(HttpListenerContext context, int statusCode)
        {
            try
            {
                context.Response.StatusCode = statusCode;
                context.Response.Close();
            }
            catch { /* the other end went away */ }
        }

        private void Stop()
        {
            try { _listener.Stop(); } catch { /* already stopped/disposed */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _registration.Dispose();
            Stop();
            try { _listener.Close(); } catch { /* best-effort teardown */ }
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, bool isError)
    {
        var title = isError ? "Twitch login cancelled" : "Twitch login complete";
        var body = isError
            ? "Something went wrong or the request was cancelled. You can close this tab and return to EDNexus."
            : "You're all set — you can close this tab and return to EDNexus.";
        var html = $"""
            <!DOCTYPE html>
            <html><head><title>{title}</title></head>
            <body style="font-family: sans-serif; background:#141414; color:#e0a030; text-align:center; padding-top: 10vh;">
            <h2>{title}</h2><p>{body}</p>
            </body></html>
            """;

        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        try
        {
            await context.Response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Close();
        }
    }

    /// <summary>Minimal query-string parser — avoids a dependency on System.Web for a handful of known keys.</summary>
    internal static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx >= 0 ? pair[..idx] : pair;
            var value = idx >= 0 ? pair[(idx + 1)..] : "";
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result;
    }
}
