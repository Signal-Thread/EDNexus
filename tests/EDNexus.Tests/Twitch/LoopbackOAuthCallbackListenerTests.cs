using System.Net;
using System.Net.Sockets;
using EDNexus.Core.Twitch;
using Xunit;

namespace EDNexus.Tests.Twitch;

/// <summary>The real <see cref="LoopbackOAuthCallbackListener"/>, over real loopback sockets.</summary>
public class LoopbackOAuthCallbackListenerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private static Uri Redirect(int port) => new($"http://localhost:{port}/callback");

    private static async Task<HttpStatusCode> GetAsync(Uri redirect, string pathAndQuery)
    {
        using var http = new HttpClient { Timeout = Patience };
        using var response = await http.GetAsync(new Uri(redirect, pathAndQuery));
        return response.StatusCode;
    }

    [Fact]
    public async Task Returns_the_parameters_of_the_request_that_carries_the_expected_state()
    {
        var port = FreePort();
        using var session = new LoopbackOAuthCallbackListener().Listen(Redirect(port), "the-state", CancellationToken.None);

        var status = await GetAsync(session.RedirectUri, "/callback?code=abc%20123&state=the-state");
        var result = await session.Callback.WaitAsync(Patience);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("abc 123", result["code"]);
        Assert.Equal("the-state", result["state"]);
    }

    [Fact]
    public async Task A_denial_that_carries_the_expected_state_is_returned()
    {
        var port = FreePort();
        using var session = new LoopbackOAuthCallbackListener().Listen(Redirect(port), "the-state", CancellationToken.None);

        await GetAsync(session.RedirectUri, "/callback?error=access_denied&state=the-state");
        var result = await session.Callback.WaitAsync(Patience);

        Assert.Equal("access_denied", result["error"]);
    }

    [Fact]
    public async Task A_stray_request_does_not_end_the_login()
    {
        var port = FreePort();
        using var session = new LoopbackOAuthCallbackListener().Listen(Redirect(port), "the-state", CancellationToken.None);

        // Another program, or a web page that guessed the port: wrong state, no state, a forged denial.
        Assert.Equal(HttpStatusCode.BadRequest, await GetAsync(session.RedirectUri, "/callback?code=evil&state=wrong"));
        Assert.Equal(HttpStatusCode.BadRequest, await GetAsync(session.RedirectUri, "/callback?code=evil"));
        Assert.Equal(HttpStatusCode.BadRequest, await GetAsync(session.RedirectUri, "/callback?error=access_denied"));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(session.RedirectUri, "/favicon.ico"));
        Assert.False(session.Callback.IsCompleted);

        // The genuine redirect still gets through afterwards.
        await GetAsync(session.RedirectUri, "/callback?code=good&state=the-state");
        var result = await session.Callback.WaitAsync(Patience);

        Assert.Equal("good", result["code"]);
    }

    [Fact]
    public async Task Cancelling_stops_the_listener_and_releases_the_port()
    {
        var port = FreePort();
        using var cts = new CancellationTokenSource();
        var listener = new LoopbackOAuthCallbackListener();
        var session = listener.Listen(Redirect(port), "the-state", cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Callback.WaitAsync(Patience));
        session.Dispose();

        // The same port can be bound again straight away.
        using var again = listener.Listen(Redirect(port), "other", CancellationToken.None);
        Assert.Equal(port, again.RedirectUri.Port);
    }

    [Fact]
    public void Disposing_releases_the_port()
    {
        var port = FreePort();
        var listener = new LoopbackOAuthCallbackListener();

        listener.Listen(Redirect(port), "the-state", CancellationToken.None).Dispose();

        using var again = listener.Listen(Redirect(port), "other", CancellationToken.None);
        Assert.Equal(port, again.RedirectUri.Port);
    }

    [Fact]
    public async Task A_taken_port_falls_back_to_another_and_reports_it()
    {
        var port = FreePort();
        var listener = new LoopbackOAuthCallbackListener();
        using var first = listener.Listen(Redirect(port), "first", CancellationToken.None);

        using var second = listener.Listen(Redirect(port), "second", CancellationToken.None);

        Assert.NotEqual(port, second.RedirectUri.Port);
        Assert.Equal("/callback", second.RedirectUri.AbsolutePath);

        // The fallback really is listening, on the URI it reports.
        await GetAsync(second.RedirectUri, "/callback?code=c&state=second");
        var result = await second.Callback.WaitAsync(Patience);
        Assert.Equal("c", result["code"]);
    }
}
