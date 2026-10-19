using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EDNexus.Core.Twitch;

/// <summary>Why a <see cref="StreamCardSnapshot"/> publish did not reach viewers.</summary>
public enum StreamStatePublishStatus
{
    /// <summary>The EBS accepted the snapshot and relayed it to Twitch PubSub.</summary>
    Published,

    /// <summary>
    /// The EBS rejected the token (<c>401</c>) — either it was never valid or the underlying Twitch
    /// grant has been revoked. The commander has to log in again; retrying will not help.
    /// </summary>
    Unauthorized,

    /// <summary>The snapshot exceeded Twitch's 5 KiB message cap (<c>413</c>).</summary>
    TooLarge,

    /// <summary>The per-channel rate limit was hit (<c>429</c>). Back off and send the next snapshot instead.</summary>
    RateLimited,

    /// <summary>Anything else: the EBS was unreachable, timed out, or Twitch PubSub itself failed.</summary>
    Failed,

    /// <summary>The EBS forgot the published card and told viewers it is offline (the card was switched off).</summary>
    Cleared,

    /// <summary>
    /// The EBS forgot the published card, so new viewers no longer get it, but could not tell viewers
    /// already watching. Worth retrying for their sake; nothing is left public to anyone new.
    /// </summary>
    ClearedNotDelivered,

    /// <summary>
    /// Twitch is refusing the <i>backend's</i> own PubSub credentials (<c>503</c> with the
    /// <see cref="StreamStateApiClient.PubSubRejectedHeader"/> header). The card is stored, so new
    /// viewers still get it, but nothing reaches viewers already watching. It is a server
    /// misconfiguration: not the commander's sign-in (so no re-login) and not worth retrying hard.
    /// </summary>
    BackendRejectedByTwitch,
}

/// <param name="Status">Whether the snapshot reached viewers, and if not, why not.</param>
/// <param name="Error">Detail for logging/diagnostics. Null on success.</param>
/// <param name="RetryAfter">How long the EBS asked the client to wait, from a <c>429</c>'s <c>Retry-After</c>.</param>
public sealed record StreamStatePublishResult(StreamStatePublishStatus Status, string? Error = null, TimeSpan? RetryAfter = null)
{
    public bool IsSuccess => Status is StreamStatePublishStatus.Published or StreamStatePublishStatus.Cleared;

    /// <summary>
    /// True when retrying the same credential is pointless — the commander must log in again. The
    /// publisher stops sending on this rather than hammering the EBS with a dead token.
    /// </summary>
    public bool RequiresReauth => Status == StreamStatePublishStatus.Unauthorized;

    public static readonly StreamStatePublishResult Ok = new(StreamStatePublishStatus.Published);

    public static readonly StreamStatePublishResult ClearedOk = new(StreamStatePublishStatus.Cleared);
}

/// <summary>
/// Publishes commander snapshots to the EBS's <c>POST /api/update-state</c>, which relays them to
/// viewers over Twitch Extensions PubSub. Pure transport: no throttling, no state, no policy — those
/// belong to <see cref="TwitchStreamCardService"/>.
/// </summary>
public interface IStreamStateApiClient
{
    /// <summary>
    /// Sends one snapshot. The broadcaster channel is never part of the request: the EBS resolves it
    /// server-side from <paramref name="token"/>, so this client cannot publish to a channel the
    /// commander does not own.
    /// </summary>
    /// <param name="updateStateEndpoint">Absolute URL of the EBS's <c>/api/update-state</c>.</param>
    /// <param name="token">The long-lived, EBS-issued broadcaster token from the login flow.</param>
    /// <param name="snapshot">What viewers should see.</param>
    Task<StreamStatePublishResult> PublishAsync(
        string updateStateEndpoint, string token, StreamCardSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// Takes the card off the air (<c>DELETE</c> on the same endpoint): the EBS forgets the stored
    /// snapshot, so it stops being served to new viewers, and tells current viewers to hide it.
    /// </summary>
    Task<StreamStatePublishResult> ClearAsync(string updateStateEndpoint, string token, CancellationToken ct = default);
}

/// <summary>Default <see cref="IStreamStateApiClient"/> over a real (or injected, for tests) <see cref="HttpClient"/>.</summary>
public sealed class StreamStateApiClient : IStreamStateApiClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public StreamStateApiClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Most characters of an EBS error body kept. A proxy's HTML error page runs to kilobytes, and
    /// this text reaches the log and the Settings status line.
    /// </summary>
    public const int MaxErrorDetailLength = 200;

    /// <summary>
    /// Header the EBS sets on a clear whose snapshot was removed but whose offline broadcast failed.
    /// Mirrors <c>EDNexus.Ebs.Services.ChannelStateClearing.SnapshotRemovedHeader</c>.
    /// </summary>
    public const string SnapshotRemovedHeader = "X-EDNexus-Snapshot-Removed";

    /// <summary>
    /// Header the EBS sets when Twitch is refusing the EBS's own PubSub credentials. Mirrors
    /// <c>EDNexus.Ebs.Services.TwitchCredentialStatus.RejectedHeader</c>.
    /// </summary>
    public const string PubSubRejectedHeader = "X-EDNexus-Pubsub-Rejected";

    public Task<StreamStatePublishResult> PublishAsync(
        string updateStateEndpoint, string token, StreamCardSnapshot snapshot, CancellationToken ct = default)
    {
        // The EBS's body shape is { "state": <payload> } — see EDNexus.Ebs UpdateStateRequest.
        var body = JsonSerializer.Serialize(
            new StateEnvelope(snapshot), StreamCardSnapshot.SerializerOptions);

        return SendAsync(
            HttpMethod.Post, updateStateEndpoint, token,
            new StringContent(body, Encoding.UTF8, "application/json"),
            StreamStatePublishResult.Ok, ct);
    }

    public Task<StreamStatePublishResult> ClearAsync(string updateStateEndpoint, string token, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, updateStateEndpoint, token, content: null, StreamStatePublishResult.ClearedOk, ct);

    private async Task<StreamStatePublishResult> SendAsync(
        HttpMethod method, string endpoint, string token, HttpContent? content, StreamStatePublishResult success, CancellationToken ct)
    {
        // The token is long-lived: never put it on the wire in cleartext, whatever the settings say.
        if (!TwitchOAuthOptions.IsSecureEbsUrl(endpoint))
        {
            content?.Dispose();
            return new StreamStatePublishResult(
                StreamStatePublishStatus.Failed, "Refusing to send the Twitch token to a non-https address.");
        }

        using var request = new HttpRequestMessage(method, endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A stream card is best-effort telemetry: an unreachable EBS must never surface as an
            // unhandled exception on the commander's machine mid-session.
            return new StreamStatePublishResult(StreamStatePublishStatus.Failed, Truncate(ex.Message));
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return success;

            var status = response.StatusCode switch
            {
                _ when response.Headers.Contains(SnapshotRemovedHeader) => StreamStatePublishStatus.ClearedNotDelivered,
                _ when response.Headers.Contains(PubSubRejectedHeader) => StreamStatePublishStatus.BackendRejectedByTwitch,
                HttpStatusCode.Unauthorized => StreamStatePublishStatus.Unauthorized,
                HttpStatusCode.RequestEntityTooLarge => StreamStatePublishStatus.TooLarge,
                HttpStatusCode.TooManyRequests => StreamStatePublishStatus.RateLimited,
                _ => StreamStatePublishStatus.Failed,
            };

            var detail = await SafeReadAsync(response, ct).ConfigureAwait(false);
            return new StreamStatePublishResult(
                status, $"HTTP {(int)response.StatusCode} — {Truncate(detail)}", RetryAfter(response));
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static string Truncate(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= MaxErrorDetailLength ? flat : string.Concat(flat.AsSpan(0, MaxErrorDetailLength), "…");
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return response.ReasonPhrase ?? ""; }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>Mirrors the EBS's <c>UpdateStateRequest</c> body: the snapshot under a <c>state</c> property.</summary>
    private sealed record StateEnvelope(
        [property: System.Text.Json.Serialization.JsonPropertyName("state")] StreamCardSnapshot State);
}
