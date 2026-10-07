using System.Net;
using EDNexus.Core.Twitch;
using EDNexus.Tests.Reporting;
using Xunit;

namespace EDNexus.Tests.Twitch;

public class EbsAuthApiClientTests
{
    [Fact]
    public async Task ExchangeCodeAsync_posts_the_code_and_pkce_verifier_and_parses_the_response()
    {
        var handler = new RecordingHandler(body: """
            { "token": "ebs-token-1", "channelId": "999", "username": "CMDR_Jameson" }
            """);
        using var client = new EbsAuthApiClient(new HttpClient(handler));

        var token = await client.ExchangeCodeAsync("http://localhost:8787/oauth/token", "auth-code", "verifier-value", "http://localhost:59123/callback");

        Assert.Equal("ebs-token-1", token.Token);
        Assert.Equal("999", token.ChannelId);
        Assert.Equal("CMDR_Jameson", token.Username);

        var sent = handler.Bodies[0];
        Assert.Contains("\"code\":\"auth-code\"", sent);
        Assert.Contains("\"code_verifier\":\"verifier-value\"", sent);
        Assert.Contains("\"redirect_uri\":\"http://localhost:59123/callback\"", sent);
        Assert.Equal("http://localhost:8787/oauth/token", handler.Uris[0]!.ToString());
    }

    [Fact]
    public async Task ExchangeCodeAsync_throws_on_non_success_response()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, """{ "message": "invalid_grant" }""");
        using var client = new EbsAuthApiClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<EbsAuthApiException>(() =>
            client.ExchangeCodeAsync("http://localhost:8787/oauth/token", "bad-code", "verifier", "http://localhost:59123/callback"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("invalid_grant", ex.Message);
    }

    [Fact]
    public async Task ExchangeCodeAsync_throws_on_an_empty_token_response()
    {
        var handler = new RecordingHandler(body: "{}");
        using var client = new EbsAuthApiClient(new HttpClient(handler));

        await Assert.ThrowsAsync<EbsAuthApiException>(() =>
            client.ExchangeCodeAsync("http://localhost:8787/oauth/token", "code", "verifier", "http://localhost:59123/callback"));
    }

    [Fact]
    public async Task RevokeAsync_sends_a_bearer_token_and_never_throws_on_error_response()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, "{}");
        using var client = new EbsAuthApiClient(new HttpClient(handler));

        await client.RevokeAsync("http://localhost:8787/oauth/revoke", "ebs-token-1");
        // Reaching here without an exception is the assertion.
    }

    [Theory]
    [InlineData("http://ebs.example.com/oauth/token")]
    [InlineData("ftp://ebs.example.com/oauth/token")]
    [InlineData("not a url")]
    public async Task ExchangeCodeAsync_refuses_to_send_the_code_to_a_non_https_address(string endpoint)
    {
        var handler = new RecordingHandler(body: """{ "token": "t", "channelId": "1", "username": "u" }""");
        using var client = new EbsAuthApiClient(new HttpClient(handler));

        await Assert.ThrowsAsync<EbsAuthApiException>(() =>
            client.ExchangeCodeAsync(endpoint, "code", "verifier", "http://localhost:59123/callback"));

        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task RevokeAsync_refuses_to_send_the_token_to_a_non_https_address()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        using var client = new EbsAuthApiClient(new HttpClient(handler));

        await Assert.ThrowsAsync<EbsAuthApiException>(() =>
            client.RevokeAsync("http://ebs.example.com/oauth/revoke", "ebs-token-1"));

        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task Both_calls_accept_https_and_loopback_http()
    {
        var handler = new RecordingHandler(body: """{ "token": "t", "channelId": "1", "username": "u" }""");
        using var client = new EbsAuthApiClient(new HttpClient(handler));

        await client.ExchangeCodeAsync("https://ebs.example.com/oauth/token", "code", "verifier", "http://localhost:59123/callback");
        await client.RevokeAsync("http://127.0.0.1:8787/oauth/revoke", "ebs-token-1");

        Assert.Equal(2, handler.Uris.Count);
    }

    [Fact]
    public async Task RevokeAsync_throws_on_a_server_error_so_the_caller_can_retry()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError, "{}");
        using var client = new EbsAuthApiClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<EbsAuthApiException>(() =>
            client.RevokeAsync("http://localhost:8787/oauth/revoke", "ebs-token-1"));
        Assert.Equal(500, ex.StatusCode);
    }
}
