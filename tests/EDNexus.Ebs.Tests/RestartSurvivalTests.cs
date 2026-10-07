using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EDNexus.Ebs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EDNexus.Ebs.Tests;

/// <summary>
/// Boots the real EBS host on the default Sqlite provider, disposes it, and boots a fresh host over
/// the same data directory — the end-to-end proof that a crash/restart/redeploy doesn't log
/// broadcasters out or blank the extension overlay.
/// </summary>
public sealed class RestartSurvivalTests : IDisposable
{
    private readonly TempEbsDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task A_broadcaster_token_and_the_last_published_state_survive_a_host_restart()
    {
        string token;
        using (var first = new Host(_data.Path))
        {
            token = first.Services.GetRequiredService<IBroadcasterTokenStore>()
                .IssueToken("chan-restart", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4)).Token;
            var client = first.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            var published = await client.PostAsJsonAsync("/api/update-state", new { state = new { system = "Sol" } });
            published.EnsureSuccessStatusCode();
        }

        using var second = new Host(_data.Path);
        var viewer = second.CreateClient();

        var initial = await viewer.GetAsync("/api/initial-state/chan-restart");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.Equal("Sol", (await initial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("system").GetString());

        var desktop = second.CreateClient();
        desktop.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var update = await desktop.PostAsJsonAsync("/api/update-state", new { state = new { system = "Achenar" } });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.True(second.PubSubClient.BroadcastCalls.ContainsKey("chan-restart"));
    }

    [Fact]
    public async Task A_token_revoked_before_a_restart_stays_revoked()
    {
        string token;
        using (var first = new Host(_data.Path))
        {
            token = first.Services.GetRequiredService<IBroadcasterTokenStore>()
                .IssueToken("chan-revoked", "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4)).Token;
            var client = first.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            (await client.PostAsync("/oauth/revoke", null)).EnsureSuccessStatusCode();
        }

        using var second = new Host(_data.Path);
        var desktop = second.CreateClient();
        desktop.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var update = await desktop.PostAsJsonAsync("/api/update-state", new { state = new { system = "Sol" } });

        Assert.Equal(HttpStatusCode.Unauthorized, update.StatusCode);
    }

    [Fact]
    public async Task The_home_page_redirects_to_the_project_site()
    {
        using var host = new Host(_data.Path);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://signal-thread-llc.github.io/EDNexus/", response.Headers.Location?.ToString());
    }

    private sealed class Host(string dataDirectory) : WebApplicationFactory<Program>
    {
        public FakeTwitchPubSubClient PubSubClient { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Twitch:ExtensionSecret"] = "c3VwZXItc2VjcmV0LWV4dGVuc2lvbi1rZXktMTIzNA==",
                ["Twitch:ClientId"] = "test-client-id",
                ["Twitch:ClientSecret"] = "test-client-secret",
                ["Twitch:ExtensionId"] = "test-extension-id",
                ["Ebs:StorageProvider"] = "Sqlite",
                ["Ebs:DataDirectory"] = dataDirectory,
                ["Ebs:UpdateStateRateLimit"] = "1000",
                ["Ebs:UpdateStateRateLimitWindowSeconds"] = "1",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITwitchPubSubClient>();
                services.AddSingleton<ITwitchPubSubClient>(PubSubClient);
            });
        }
    }
}
