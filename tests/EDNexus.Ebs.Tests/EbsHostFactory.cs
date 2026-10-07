using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using EDNexus.Ebs.Services;

namespace EDNexus.Ebs.Tests;

/// <summary>
/// A throwaway in-memory EBS host with configuration overrides, an optional pinned peer address (the
/// TCP peer a reverse proxy would be) and optional service replacements. One factory per test, so a
/// tight rate limit in one test cannot leak into another.
/// </summary>
internal sealed class EbsHostFactory : WebApplicationFactory<Program>
{
    public const string ExtensionSecret = "c3VwZXItc2VjcmV0LWV4dGVuc2lvbi1rZXktMTIzNA==";

    private readonly Dictionary<string, string?> _config = new()
    {
        ["Twitch:ExtensionSecret"] = ExtensionSecret,
        ["Twitch:ClientId"] = "test-client-id",
        ["Twitch:ClientSecret"] = "test-client-secret",
        ["Twitch:ExtensionId"] = "test-extension-id",
        ["Ebs:StorageProvider"] = "InMemory",
        ["Ebs:UpdateStateRateLimit"] = "1000",
        ["Ebs:UpdateStateRateLimitWindowSeconds"] = "1",
    };

    private readonly IPAddress? _peer;
    private readonly Action<IServiceCollection>? _services;

    public EbsHostFactory(Dictionary<string, string?>? overrides = null, IPAddress? peer = null, Action<IServiceCollection>? services = null)
    {
        foreach (var pair in overrides ?? [])
            _config[pair.Key] = pair.Value;
        _peer = peer;
        _services = services;
    }

    public FakeTwitchPubSubClient PubSub { get; } = new();

    public IBroadcasterTokenStore Tokens => Services.GetRequiredService<IBroadcasterTokenStore>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_config));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITwitchPubSubClient>();
            services.AddSingleton<ITwitchPubSubClient>(PubSub);
            if (_peer is not null)
                services.AddSingleton<IStartupFilter>(new PeerAddressFilter(_peer));
            _services?.Invoke(services);
        });
    }

    /// <summary>Signs in a broadcaster directly in the token store and returns a client carrying their bearer token.</summary>
    public (HttpClient Client, string Token) CreateBroadcasterClient(string channelId)
    {
        var record = Tokens.IssueToken(channelId, "CMDR", "access", "refresh", DateTimeOffset.UtcNow.AddHours(4));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", record.Token);
        return (client, record.Token);
    }

    private sealed class PeerAddressFilter(IPAddress peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = peer;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
