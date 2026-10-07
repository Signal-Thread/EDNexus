using System.Text.Json;
using System.Threading.RateLimiting;
using EDNexus.Ebs.Endpoints;
using EDNexus.Ebs.Models;
using EDNexus.Ebs.Options;
using EDNexus.Ebs.Security;
using EDNexus.Ebs.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<TwitchEbsOptions>()
    .Bind(builder.Configuration.GetSection(TwitchEbsOptions.SectionName))
    // Fail fast at startup rather than throwing a raw FormatException from inside a request handler
    // (TwitchExtensionJwtService.CreateExternalServiceToken) the first time a state update comes in.
    .Validate(
        o => !string.IsNullOrWhiteSpace(o.ExtensionSecret) && IsValidBase64(o.ExtensionSecret),
        "Twitch:ExtensionSecret must be set to a non-empty, valid base64 string.")
    .ValidateOnStart();
builder.Services
    .AddOptions<EbsOptions>()
    .Bind(builder.Configuration.GetSection(EbsOptions.SectionName))
    // The desktop app refreshes an unchanged live card on a fixed schedule; a shorter limit would
    // take live cards down between refreshes. Refuse to start rather than fail silently for viewers.
    .Validate(
        o => o.ChannelStateMaxAgeHours <= 0 || o.ChannelStateMaxAgeHours >= EbsOptions.MinChannelStateMaxAgeHours,
        $"Ebs:ChannelStateMaxAgeHours must be 0 (no limit) or at least {EbsOptions.MinChannelStateMaxAgeHours}: "
        + "the desktop app refreshes an unchanged card every 6 hours.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
// Still needed by TwitchPubSubClient to sign the EBS's own OUTBOUND JWT for the Helix PubSub call —
// Validate() (inbound JWT verification) is no longer used now that /api/update-state authenticates
// via the EBS-issued long-lived broadcaster token (IBroadcasterTokenStore) instead of a Twitch
// Extension JWT.
builder.Services.AddSingleton<ITwitchExtensionJwtService, TwitchExtensionJwtService>();

// Durable state (see EbsOptions.StorageProvider): broadcaster tokens + Twitch grants and each
// channel's last published state live in one SQLite file so a crash/restart/redeploy doesn't log
// every broadcaster out. Twitch tokens are encrypted with Data Protection, whose key ring must
// itself be persisted — otherwise every restart would mint a fresh key and orphan the ciphertext.
// Everything resolves through IOptions (not builder.Configuration) so test/host overrides applied
// after this point are honoured.
builder.Services.AddDataProtection().SetApplicationName("EDNexus.Ebs");
builder.Services
    .AddOptions<KeyManagementOptions>()
    .Configure<IOptions<EbsOptions>, IHostEnvironment, ILoggerFactory>((keys, ebs, env, loggerFactory) =>
    {
        if (ebs.Value.StorageProvider != EbsStorageProvider.Sqlite)
            return; // in-memory state dies with the process anyway; the default key ring is fine.

        var configured = ebs.Value.ResolveDataProtectionKeysDirectory(env.ContentRootPath);
        DataProtectionKeyRingMove.MoveIfNeeded(
            ebs.Value.ResolveDefaultDataProtectionKeysDirectory(env.ContentRootPath), configured,
            loggerFactory.CreateLogger(nameof(DataProtectionKeyRingMove)));

        var keysDirectory = Directory.CreateDirectory(configured);
        keys.XmlRepository = new FileSystemXmlRepository(keysDirectory, loggerFactory);
    });
builder.Services.AddSingleton(sp =>
{
    var ebs = sp.GetRequiredService<IOptions<EbsOptions>>().Value;
    var dataDirectory = Directory.CreateDirectory(ebs.ResolveDataDirectory(sp.GetRequiredService<IHostEnvironment>().ContentRootPath));
    return new EbsDatabase(Path.Combine(dataDirectory.FullName, EbsDatabase.FileName));
});
builder.Services.AddSingleton<IChannelStateStore>(sp =>
{
    var ebs = sp.GetRequiredService<IOptions<EbsOptions>>().Value;
    var time = sp.GetRequiredService<TimeProvider>();
    return ebs.StorageProvider == EbsStorageProvider.Sqlite
        ? new SqliteChannelStateStore(sp.GetRequiredService<EbsDatabase>(), time, ebs.ChannelStateMaxAge)
        : new InMemoryChannelStateStore(time, ebs.ChannelStateMaxAge);
});
builder.Services.AddSingleton<IBroadcasterTokenStore>(sp =>
    sp.GetRequiredService<IOptions<EbsOptions>>().Value.StorageProvider == EbsStorageProvider.Sqlite
        ? new SqliteBroadcasterTokenStore(
            sp.GetRequiredService<EbsDatabase>(),
            sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SqliteBroadcasterTokenStore>>())
        : new InMemoryBroadcasterTokenStore(sp.GetRequiredService<TimeProvider>()));

builder.Services.AddHttpClient<ITwitchPubSubClient, TwitchPubSubClient>(client =>
{
    // A hanging Helix call shouldn't be able to tie up a request indefinitely (the default
    // HttpClient timeout is 100s); combined with the per-channel rate limit below, this bounds how
    // long a single misbehaving/slow call can hold resources.
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<ITwitchOAuthClient, TwitchOAuthClient>();
builder.Services.AddHostedService<TwitchTokenRefreshBackgroundService>();

// CORS: only the extension's own Twitch-hosted iframe (https://*.ext-twitch.tv) — plus, for local
// development, the Twitch Developer Rig or any origin explicitly listed in Ebs:AdditionalAllowedFrontendOrigins
// — may read GET /api/initial-state/{channelId} from a browser context.
var additionalFrontendOrigins = new HashSet<string>(
    builder.Configuration.GetSection(EbsOptions.SectionName).Get<EbsOptions>()?.AdditionalAllowedFrontendOrigins ?? [],
    StringComparer.OrdinalIgnoreCase);
builder.Services.AddCors(options =>
{
    options.AddPolicy("extension-frontend", policy => policy
        .SetIsOriginAllowed(origin => IsAllowedFrontendOrigin(origin, additionalFrontendOrigins))
        .WithMethods("GET")
        .AllowAnyHeader());
});

// Rate limiting: throttle state updates and initial-state reads per broadcaster channel, so a
// misbehaving desktop client (or a burst of viewers) cannot exceed Twitch's own PubSub quota or
// exhaust EBS resources. The channel id is authenticated by AuthenticateBroadcasterMiddleware, which
// runs BEFORE UseRateLimiter — the partition-key callback below runs at routing time, before the
// endpoint delegate's body, so the channel id cannot come from anything set inside the handler
// itself (that would silently degrade every request to per-IP partitioning instead of per-channel).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Tell the client how long to wait, rather than leaving it to guess and retry into the same window.
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };

    options.AddPolicy("update-state", httpContext =>
    {
        var ebsOptions = httpContext.RequestServices.GetRequiredService<IOptions<EbsOptions>>().Value;
        var partitionKey = httpContext.Items.TryGetValue("ChannelId", out var channelId) && channelId is string id
            ? id
            : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, ebsOptions.UpdateStateRateLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, ebsOptions.UpdateStateRateLimitWindowSeconds)),
            QueueLimit = 0,
        });
    });

    // Separate from update-state so switching the card off straight after a publish is never
    // rejected by that publish's window — a clear is the privacy-critical request of the two.
    options.AddPolicy("clear-state", httpContext =>
    {
        var partitionKey = httpContext.Items.TryGetValue("ChannelId", out var channelId) && channelId is string id
            ? id
            : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });

    options.AddPolicy("initial-state", httpContext =>
    {
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromSeconds(10),
            QueueLimit = 0,
        });
    });
});

// Only fall back to the configured Ebs:Port when nothing else (ASPNETCORE_URLS, --urls, launch
// profile, etc.) has already told Kestrel what to bind to.
var urlsAlreadyConfigured = builder.Configuration["urls"] is not null
    || Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is not null;
if (!urlsAlreadyConfigured && !builder.Environment.IsEnvironment("Testing"))
{
    var configuredPort = builder.Configuration.GetSection(EbsOptions.SectionName).Get<EbsOptions>()?.Port ?? 8787;
    builder.WebHost.UseUrls($"http://0.0.0.0:{configuredPort}");
}

var app = builder.Build();

// Open the database (creating the data directory and migrating the schema) now, so a bad
// Ebs:DataDirectory or an unreadable/too-new database fails the deploy instead of the first request.
app.Services.GetRequiredService<IBroadcasterTokenStore>();
app.Services.GetRequiredService<IChannelStateStore>();

app.UseCors("extension-frontend");

// Authenticates /api/update-state (via the EBS-issued long-lived broadcaster token — see
// IBroadcasterTokenStore) and stores the verified channel id in HttpContext.Items BEFORE
// UseRateLimiter runs its partition-key callback (rate-limiting middleware evaluates the policy at
// routing time — before the endpoint delegate's body executes — so setting Items from inside the
// handler, as this used to do, was always too late to affect partitioning for that same request).
app.Use(async (context, next) =>
{
    if ((HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
        && context.Request.Path.Equals("/api/update-state", StringComparison.OrdinalIgnoreCase))
    {
        var tokenStore = context.RequestServices.GetRequiredService<IBroadcasterTokenStore>();
        // Taking a card down needs no Twitch grant (PubSub is signed with the extension secret), and
        // a broadcaster whose grant lapsed must still be able to stop being shown.
        var requireGrant = !HttpMethods.IsDelete(context.Request.Method);
        if (TryAuthenticateBroadcaster(context.Request, tokenStore, requireGrant, out var channelId, out var failure))
        {
            context.Items["ChannelId"] = channelId;
        }
        else
        {
            context.Items["BroadcasterAuthFailure"] = failure;
        }
    }

    await next().ConfigureAwait(false);
});

app.UseRateLimiter();

// Anyone who lands on the bare EBS host (e.g. following the OAuth redirect URI's origin) gets the
// project site rather than a 404.
app.MapGet("/", () => Results.Redirect("https://signal-thread-llc.github.io/EDNexus/"));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapOAuthEndpoints();

app.MapPost("/api/update-state", async (
        HttpRequest httpRequest,
        UpdateStateRequest body,
        IChannelStateStore stateStore,
        ITwitchPubSubClient pubSubClient,
        IOptions<EbsOptions> ebsOptions,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        // Authenticated by the middleware above (long-lived, EBS-issued token minted at
        // POST /oauth/token — not a Twitch/Extension JWT). The channel id is resolved server-side
        // from the token, never taken from the request body, so a compromised client cannot spoof
        // another broadcaster's channel.
        if (httpRequest.HttpContext.Items["ChannelId"] is not string channelId)
        {
            return (IResult?)httpRequest.HttpContext.Items["BroadcasterAuthFailure"] ?? Results.Unauthorized();
        }

        PubSubBroadcastRequest pubSubRequest;
        try
        {
            pubSubRequest = PubSubBroadcastRequest.Create(channelId, body.State, ebsOptions.Value.MaxStatePayloadBytes);
        }
        catch (PubSubPayloadTooLargeException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        stateStore.Set(channelId, body.State);

        var published = await pubSubClient.BroadcastAsync(channelId, body.State, cancellationToken).ConfigureAwait(false);
        if (!published)
        {
            logger.LogWarning("Failed to publish state update for channel {ChannelId} to Twitch PubSub.", channelId);
            return Results.Problem("Failed to publish the update to Twitch PubSub.", statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new
        {
            published = true,
            channelId,
            bytes = System.Text.Encoding.UTF8.GetByteCount(pubSubRequest.Message),
        });
    })
    .RequireRateLimiting("update-state");

// The broadcaster switched the card off or signed out: forget the stored snapshot so
// /api/initial-state stops serving it to anyone who asks, and tell viewers already watching to hide it.
app.MapDelete("/api/update-state", async (
        HttpRequest httpRequest,
        IChannelStateStore stateStore,
        ITwitchPubSubClient pubSubClient,
        CancellationToken cancellationToken) =>
    {
        if (httpRequest.HttpContext.Items["ChannelId"] is not string channelId)
        {
            return (IResult?)httpRequest.HttpContext.Items["BroadcasterAuthFailure"] ?? Results.Unauthorized();
        }

        // A 502, like the POST's, when viewers already watching were not told: the snapshot is gone
        // either way, and a retry is harmless, so the client tries again rather than leave them the card.
        if (!await ChannelStateClearing.ClearAsync(channelId, stateStore, pubSubClient, cancellationToken).ConfigureAwait(false))
        {
            httpRequest.HttpContext.Response.Headers[ChannelStateClearing.SnapshotRemovedHeader] = "true";
            return Results.Problem(
                "The card was removed, but Twitch PubSub did not deliver the offline message to viewers already watching.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.NoContent();
    })
    .RequireRateLimiting("clear-state");

app.MapGet("/api/initial-state/{channelId}", (string channelId, IChannelStateStore stateStore) =>
    {
        if (!stateStore.TryGet(channelId, out var state))
        {
            return Results.NotFound(new { message = "No state has been published for this channel yet." });
        }

        return Results.Ok(state);
    })
    .RequireRateLimiting("initial-state")
    .RequireCors("extension-frontend");

app.Run();

static bool IsValidBase64(string value)
{
    try
    {
        Convert.FromBase64String(value);
        return true;
    }
    catch (FormatException)
    {
        return false;
    }
}

static bool IsAllowedFrontendOrigin(string origin, HashSet<string> additionalOrigins)
{
    if (additionalOrigins.Contains(origin))
    {
        return true;
    }

    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
    {
        return false;
    }

    return uri.Host.Equals("ext-twitch.tv", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".ext-twitch.tv", StringComparison.OrdinalIgnoreCase);
}

static bool TryAuthenticateBroadcaster(
    HttpRequest request, IBroadcasterTokenStore tokenStore, bool requireValidTwitchGrant, out string channelId, out IResult? failure)
{
    channelId = "";
    var header = request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        failure = Results.Unauthorized();
        return false;
    }

    var token = header["Bearer ".Length..].Trim();
    if (!tokenStore.TryGetByToken(token, out var record))
    {
        // Say which kind of 401 this is: an empty 401 sends people hunting through their Twitch
        // console configuration. Tokens survive restarts, so an unknown one was revoked, replaced by
        // a newer login, or issued by a different EBS.
        failure = Results.Problem(
            "This token is not known to the service. It was revoked, replaced by a newer sign-in, or "
            + "issued by a different service — log in again.",
            statusCode: StatusCodes.Status401Unauthorized);
        return false;
    }

    if (requireValidTwitchGrant && !record.IsTwitchGrantValid)
    {
        failure = Results.Problem(
            "The underlying Twitch grant is no longer valid — please log in again.",
            statusCode: StatusCodes.Status401Unauthorized);
        return false;
    }

    channelId = record.ChannelId;
    failure = null;
    return true;
}

/// <summary>Entry point marker used by <c>WebApplicationFactory</c> in integration tests.</summary>
public partial class Program;
