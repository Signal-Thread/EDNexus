using System.Net.Http.Headers;
using System.Net.Http.Json;
using EDNexus.Ebs.Options;
using EDNexus.Ebs.Security;
using Microsoft.Extensions.Options;

namespace EDNexus.Ebs.Services;

/// <summary>What a credential check found.</summary>
public enum TwitchCredentialProbeResult
{
    /// <summary>The check could not tell (a network error, or a response that says nothing about the credentials).</summary>
    Inconclusive = 0,

    /// <summary>Twitch did not refuse the credentials.</summary>
    Accepted,

    /// <summary>Twitch refused the credentials.</summary>
    Rejected,
}

/// <summary>Asks Twitch, without broadcasting anything, whether it accepts the credentials PubSub calls are signed with.</summary>
public interface ITwitchCredentialProbe
{
    /// <summary>Runs one check and records a definite answer in <see cref="TwitchCredentialStatus"/>.</summary>
    Task<TwitchCredentialProbeResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Checks the PubSub credentials by sending the real endpoint a request that is signed exactly like a
/// real broadcast but carries a deliberately invalid target, so it can never reach a viewer.
/// </summary>
/// <remarks>
/// Twitch verifies the signed token before it looks at the body, so a refused credential comes back
/// <c>401</c>/<c>403</c> and an accepted one comes back <c>400</c> (the bogus target is what it then
/// rejects). Going through the same endpoint, <c>Client-Id</c> and token claims as a real publish
/// is the point: a different endpoint would be checking a different set of rules.
/// <para>
/// Two limits, stated rather than hidden. The refusal side is the one seen in production
/// (<c>403 JWT could not be verified</c>); that Twitch really does check the token before the body is
/// reasoned from its documentation, not observed, so an <see cref="TwitchCredentialProbeResult.Accepted"/>
/// here means "not refused", and only a real broadcast proves the credentials work. And this never
/// guesses: anything it does not recognise is <see cref="TwitchCredentialProbeResult.Inconclusive"/>,
/// which changes nothing.
/// </para>
/// </remarks>
public sealed class TwitchCredentialProbe : ITwitchCredentialProbe
{
    private const string PubSubUrl = "https://api.twitch.tv/helix/extensions/pubsub";

    /// <summary>Not a target Twitch knows (<c>broadcast</c>, <c>global</c>, <c>whisper-…</c>), so it is refused whatever else is true.</summary>
    internal const string InvalidTarget = "__ednexus_credential_check__";

    private readonly HttpClient _httpClient;
    private readonly ITwitchExtensionJwtService _jwtService;
    private readonly TwitchEbsOptions _options;
    private readonly TwitchCredentialStatus _status;
    private readonly ILogger<TwitchCredentialProbe> _logger;

    public TwitchCredentialProbe(
        HttpClient httpClient,
        ITwitchExtensionJwtService jwtService,
        IOptions<TwitchEbsOptions> options,
        TwitchCredentialStatus status,
        ILogger<TwitchCredentialProbe> logger)
    {
        _httpClient = httpClient;
        _jwtService = jwtService;
        _options = options.Value;
        _status = status;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TwitchCredentialProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        // Any channel id will do for a request that is going to be refused on its target; the owner's
        // is the most natural, and "0" cannot be a real one.
        var channelId = !string.IsNullOrWhiteSpace(_options.OwnerUserId) ? _options.OwnerUserId.Trim() : "0";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, PubSubUrl)
            {
                Content = JsonContent.Create(new
                {
                    broadcaster_id = channelId,
                    target = new[] { InvalidTarget },
                    message = "{}",
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtService.CreateExternalServiceToken(channelId));
            request.Headers.Add("Client-Id", TwitchPubSubClient.ClientIdHeader(_options));

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await ClassifyAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A check that cannot reach Twitch (or cannot even sign: a malformed secret) says nothing
            // about whether Twitch accepts the credentials, and must never take the service down.
            _logger.LogWarning(ex, "The Twitch credential check could not be completed; it proves nothing either way.");
            return TwitchCredentialProbeResult.Inconclusive;
        }
    }

    private async Task<TwitchCredentialProbeResult> ClassifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;

        if (TwitchCredentialStatus.IsCredentialRefusal(status))
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _status.MarkRejected(status);
            _logger.LogError(
                "Twitch REJECTED the EBS's PubSub credentials ({StatusCode}: {Body}). Live updates will NOT reach viewers " +
                "until this is fixed. Check Twitch__ExtensionSecret (this extension's own base64 secret), Twitch__ExtensionId " +
                "(this extension's Client ID) and Twitch__OwnerUserId (the numeric Twitch user id of the extension's owner).",
                status, body);
            return TwitchCredentialProbeResult.Rejected;
        }

        // 400: the credentials got past verification and only the deliberately invalid target was
        // refused, which is the expected answer. 2xx would mean Twitch accepted a message it should not
        // have; the credentials are plainly not refused, but it is worth saying.
        if (status == 400 || response.IsSuccessStatusCode)
        {
            _status.MarkAccepted();
            if (response.IsSuccessStatusCode)
                _logger.LogWarning("The Twitch credential check was accepted outright ({StatusCode}); it should have been refused for its invalid target.", status);
            else
                _logger.LogInformation("Twitch did not refuse the EBS's PubSub credentials (the check's invalid target was refused, as intended).");
            return TwitchCredentialProbeResult.Accepted;
        }

        _logger.LogWarning("The Twitch credential check got {StatusCode}, which says nothing about the credentials; leaving the status unchanged.", status);
        return TwitchCredentialProbeResult.Inconclusive;
    }
}
