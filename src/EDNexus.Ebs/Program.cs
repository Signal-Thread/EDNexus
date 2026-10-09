using System.Net;
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
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<TwitchEbsOptions>()
    .Bind(builder.Configuration.GetSection(TwitchEbsOptions.SectionName))
    // Fail fast at startup (ids/secrets present, a decodable Extension Secret, an https redirect URI)
    // rather than throwing a raw FormatException from inside a request handler the first time a state
    // update comes in, or sending viewers to a Twitch error page. See TwitchEbsOptionsValidator.
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<TwitchEbsOptions>, TwitchEbsOptionsValidator>();
builder.Services
    .AddOptions<EbsOptions>()
    .Bind(builder.Configuration.GetSection(EbsOptions.SectionName))
    // Includes the snapshot-age floor: the desktop app refreshes an unchanged live card on a fixed
    // schedule, so a shorter limit would take live cards down between refreshes. See EbsOptionsValidator.
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<EbsOptions>, EbsOptionsValidator>();

// Behind the documented reverse proxy the TCP peer is the proxy, so without this every viewer would
// share one rate-limit bucket (and the client address in any log would be the proxy's). The proxy's
// X-Forwarded-For is trusted ONLY when the peer is a configured proxy, and for one hop. Resolved
// through IOptions so test/host overrides are honoured.
builder.Services
    .AddOptions<ForwardedHeadersOptions>()
    .Configure<IOptions<EbsOptions>>((forwarded, ebsOptions) =>
    {
        var ebs = ebsOptions.Value;
        forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        forwarded.ForwardLimit = 1;

        // ASP.NET trusts loopback by default; replace that with the explicit list (or the defaults).
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var proxy in ebs.TrustedProxies)
            if (IPAddress.TryParse(proxy, out var address)) forwarded.KnownProxies.Add(address);

        var networks = ebs.TrustedProxyNetworks;
        if (networks.Length == 0 && ebs.TrustedProxies.Length == 0)
            networks = EbsOptions.DefaultTrustedProxyNetworks;
        foreach (var network in networks)
            if (System.Net.IPNetwork.TryParse(network, out var parsed)) forwarded.KnownIPNetworks.Add(parsed);
    });

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChannelLocks>();
// Signs the EBS's own OUTBOUND JWT for the Helix PubSub call. /api/update-state authenticates via the
// EBS-issued long-lived broadcaster token (IBroadcasterTokenStore); no inbound Twitch JWT is accepted.
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
builder.Services.AddHttpClient<ITwitchOAuthClient, TwitchOAuthClient>(client =>
{
    // Same reasoning as above, and a login/refresh waits on this: fail in seconds, not the 100s default.
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHostedService<TwitchTokenRefreshBackgroundService>();
builder.Services.AddHostedService<ChannelStatePruneBackgroundService>();

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
        .AllowAnyHeader()
        // A browser hides every response header outside the CORS-safelisted set from the extension's
        // script unless it is exposed; the frontend honours Retry-After on a 429 from initial-state.
        .WithExposedHeaders("Retry-After"));
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
        // A throttled answer must never be replayed from a shared cache to someone who is not throttled.
        context.HttpContext.Response.Headers.CacheControl = "no-store";
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

    // Per client address — the forwarded one, since UseForwardedHeaders runs first — so each viewer
    // gets their own budget instead of every viewer sharing the proxy's.
    options.AddPolicy("initial-state", httpContext =>
    {
        var ebsOptions = httpContext.RequestServices.GetRequiredService<IOptions<EbsOptions>>().Value;
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, ebsOptions.InitialStateRateLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, ebsOptions.InitialStateRateLimitWindowSeconds)),
            QueueLimit = 0,
        });
    });

    // The four unauthenticated /oauth/* routes share one per-IP budget.
    options.AddPolicy(OAuthEndpoints.RateLimitPolicy, httpContext =>
    {
        var ebsOptions = httpContext.RequestServices.GetRequiredService<IOptions<EbsOptions>>().Value;
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, ebsOptions.OAuthRateLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, ebsOptions.OAuthRateLimitWindowSeconds)),
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

// First, so every later middleware (and the rate limiter's partition key) sees the real client address.
app.UseForwardedHeaders();

// Cap the request body before anything reads it. Kestrel enforces the limit as the body streams (so a
// chunked upload is cut off too); the Content-Length check refuses an honest oversized request without
// reading a byte, and is what a TestServer host (no Kestrel feature) relies on.
app.Use(async (context, next) =>
{
    if ((HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
        && (context.Request.Path.StartsWithSegments("/api/update-state") || context.Request.Path.StartsWithSegments("/oauth")))
    {
        var limit = context.RequestServices.GetRequiredService<IOptions<EbsOptions>>().Value.MaxRequestBodyBytes;
        if (context.Request.ContentLength > limit)
        {
            await Results.Problem(
                    $"The request body exceeds the {limit} byte limit.",
                    statusCode: StatusCodes.Status413PayloadTooLarge)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
            bodyLimit.MaxRequestBodySize = limit;
    }

    await next().ConfigureAwait(false);
});

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
app.MapGet("/", () => Results.Redirect("https://signal-thread.github.io/EDNexus/"));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapOAuthEndpoints();

app.MapPost("/api/update-state", async (
        HttpRequest httpRequest,
        UpdateStateRequest body,
        IChannelStateStore stateStore,
        ITwitchPubSubClient pubSubClient,
        IBroadcasterTokenStore tokenStore,
        ChannelLocks channelLocks,
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
        catch (InvalidPubSubStateException ex)
        {
            // A missing/null/non-object "state" — a client error, not the 500 an undefined JsonElement used to cause.
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (PubSubPayloadTooLargeException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        // What was size-checked is the compact serialisation, so that is what is stored and broadcast —
        // not the request's original whitespace, which the limit never saw.
        using var compactState = JsonDocument.Parse(pubSubRequest.Message);

        bool published;
        using (await channelLocks.AcquireAsync(channelId, cancellationToken).ConfigureAwait(false))
        {
            // Authenticated again now that this channel is ours alone: a sign-out (POST /oauth/revoke) that
            // finished while this request waited has revoked the token, and storing the snapshot after that
            // clear would leave a public card with no credential able to take it down.
            if (!TryAuthenticateBroadcaster(httpRequest, tokenStore, requireValidTwitchGrant: true, out var stillAuthorizedChannel, out var failure)
                || stillAuthorizedChannel != channelId)
            {
                return failure ?? Results.Unauthorized();
            }

            stateStore.Set(channelId, compactState.RootElement);
            published = await pubSubClient.BroadcastAsync(channelId, compactState.RootElement, cancellationToken).ConfigureAwait(false);
        }

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
        IBroadcasterTokenStore tokenStore,
        ChannelLocks channelLocks,
        CancellationToken cancellationToken) =>
    {
        if (httpRequest.HttpContext.Items["ChannelId"] is not string channelId)
        {
            return (IResult?)httpRequest.HttpContext.Items["BroadcasterAuthFailure"] ?? Results.Unauthorized();
        }

        bool viewersTold;
        using (await channelLocks.AcquireAsync(channelId, cancellationToken).ConfigureAwait(false))
        {
            // Still the channel's live credential? A token revoked while this waited must not clear
            // a card the broadcaster has since published again under a new sign-in.
            if (!TryAuthenticateBroadcaster(httpRequest, tokenStore, requireValidTwitchGrant: false, out var stillAuthorizedChannel, out var failure)
                || stillAuthorizedChannel != channelId)
            {
                return failure ?? Results.Unauthorized();
            }

            viewersTold = await ChannelStateClearing.ClearAsync(channelId, stateStore, pubSubClient, cancellationToken).ConfigureAwait(false);
        }

        // A 502, like the POST's, when viewers already watching were not told: the snapshot is gone
        // either way, and a retry is harmless, so the client tries again rather than leave them the card.
        if (!viewersTold)
        {
            httpRequest.HttpContext.Response.Headers[ChannelStateClearing.SnapshotRemovedHeader] = "true";
            return Results.Problem(
                "The card was removed, but Twitch PubSub did not deliver the offline message to viewers already watching.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.NoContent();
    })
    .RequireRateLimiting("clear-state");

app.MapGet("/api/initial-state/{channelId}", (HttpContext httpContext, string channelId, IChannelStateStore stateStore, IOptions<EbsOptions> ebsOptions) =>
    {
        if (!stateStore.TryGet(channelId, out var state))
        {
            return Results.NotFound(new { message = "No state has been published for this channel yet." });
        }

        // Lets a CDN/proxy/browser absorb a burst of viewers; live changes arrive by PubSub, so a few
        // seconds of staleness is invisible. The CORS middleware adds Vary: Origin, so a shared cache
        // keeps one copy per allowed origin instead of replaying one origin's Access-Control-Allow-Origin.
        var cacheSeconds = ebsOptions.Value.InitialStateCacheSeconds;
        if (cacheSeconds > 0)
        {
            httpContext.Response.Headers.CacheControl = $"public, max-age={cacheSeconds}";
        }

        return Results.Ok(state);
    })
    .RequireRateLimiting("initial-state")
    .RequireCors("extension-frontend");

try
{
    app.Run();
}
catch (OptionsValidationException ex)
{
    // Missing or invalid settings are an operator mistake, not a crash. Say exactly what to fix and
    // exit with a configuration error code, instead of an unhandled-exception stack trace that also
    // sets off crash reporters (e.g. Datadog crash tracking) for something that is not a bug.
    Console.Error.WriteLine("EDNexus EBS cannot start: the configuration is invalid.");
    foreach (var failure in ex.Failures) Console.Error.WriteLine("  - " + failure);
    Console.Error.WriteLine(
        "Set the missing values as environment variables (for example Twitch__ClientId and Twitch__ExtensionId) " +
        "or in the .env file; see .env.example and the README.");
    return 78; // EX_CONFIG
}

return 0;

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
