using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EDNexus.Ebs.Models;
using EDNexus.Ebs.Options;
using EDNexus.Ebs.Security;
using Microsoft.Extensions.Options;

namespace EDNexus.Ebs.Services;

/// <summary>
/// Calls the Twitch Helix "Send Extension PubSub Message" endpoint to relay a broadcaster's state
/// update to every viewer subscribed to the extension's overlay.
/// </summary>
public sealed class TwitchPubSubClient : ITwitchPubSubClient
{
    private const string PubSubUrl = "https://api.twitch.tv/helix/extensions/pubsub";

    private readonly HttpClient _httpClient;
    private readonly ITwitchExtensionJwtService _jwtService;
    private readonly TwitchEbsOptions _twitchOptions;
    private readonly EbsOptions _ebsOptions;
    private readonly TwitchCredentialStatus _credentials;
    private readonly ILogger<TwitchPubSubClient> _logger;

    public TwitchPubSubClient(
        HttpClient httpClient,
        ITwitchExtensionJwtService jwtService,
        IOptions<TwitchEbsOptions> twitchOptions,
        IOptions<EbsOptions> ebsOptions,
        TwitchCredentialStatus credentials,
        ILogger<TwitchPubSubClient> logger)
    {
        _httpClient = httpClient;
        _jwtService = jwtService;
        _twitchOptions = twitchOptions.Value;
        _ebsOptions = ebsOptions.Value;
        _credentials = credentials;
        _logger = logger;
    }

    /// <summary>The <c>Client-Id</c> sent to Helix PubSub: the extension's id, else the application's.</summary>
    internal static string ClientIdHeader(TwitchEbsOptions options) =>
        string.IsNullOrWhiteSpace(options.ExtensionId) ? options.ClientId : options.ExtensionId;

    /// <inheritdoc />
    public async Task<bool> BroadcastAsync(string broadcasterId, JsonElement state, CancellationToken cancellationToken)
    {
        var request = PubSubBroadcastRequest.Create(broadcasterId, state, _ebsOptions.MaxStatePayloadBytes);
        var token = _jwtService.CreateExternalServiceToken(broadcasterId);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, PubSubUrl)
        {
            Content = JsonContent.Create(request),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Twitch ties this call to the EXTENSION (the JWT is signed with its secret), so the header is
        // the extension's Client ID. The OAuth application's ClientId is the fallback for a
        // deployment that never set ExtensionId, which in practice is the same value.
        httpRequest.Headers.Add("Client-Id", ClientIdHeader(_twitchOptions));

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Twitch PubSub broadcast for channel {BroadcasterId} failed with {StatusCode}: {Body}",
                broadcasterId,
                (int)response.StatusCode,
                body);
            // Only a refusal of the signed token says anything about the credentials; a 400 is the
            // payload and a 5xx is Twitch, and neither should flip the state.
            if (TwitchCredentialStatus.IsCredentialRefusal((int)response.StatusCode))
                _credentials.MarkRejected((int)response.StatusCode);
            return false;
        }

        // A real broadcast is the authority: it clears a rejection the startup check recorded.
        _credentials.MarkAccepted();
        return true;
    }
}
