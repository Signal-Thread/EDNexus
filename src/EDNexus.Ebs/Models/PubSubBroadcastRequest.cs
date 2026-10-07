using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EDNexus.Ebs.Models;

/// <summary>
/// The request body Twitch's Helix Extensions PubSub endpoint
/// (<c>POST https://api.twitch.tv/helix/extensions/pubsub</c>) expects. See
/// https://dev.twitch.tv/docs/api/reference/#send-extension-pubsub-message.
/// </summary>
public sealed record PubSubBroadcastRequest
{
    /// <summary>Twitch requires this to be exactly <c>["broadcast"]</c> for the video-overlay use case.</summary>
    [JsonPropertyName("target")]
    public IReadOnlyList<string> Target { get; init; } = new[] { "broadcast" };

    [JsonPropertyName("broadcaster_id")]
    public required string BroadcasterId { get; init; }

    /// <summary>The message payload, already serialized to a JSON string (Twitch requires a string, not an object).</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Builds a validated PubSub broadcast request for the given broadcaster and state payload.
    /// </summary>
    /// <param name="broadcasterId">The Twitch channel/user id to broadcast to.</param>
    /// <param name="state">The arbitrary state payload to serialize and forward as-is.</param>
    /// <param name="maxMessageBytes">
    /// The maximum allowed size, in UTF-8 bytes, of the serialized message. Twitch enforces a hard
    /// 5 KiB (5120 byte) ceiling; callers should pass a value at or below that.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="broadcasterId"/> is empty.</exception>
    /// <exception cref="InvalidPubSubStateException">
    /// Thrown when <paramref name="state"/> is missing (<see cref="JsonValueKind.Undefined"/>), <c>null</c>
    /// or not a JSON object — the snapshot contract is an object, and an undefined element cannot be
    /// serialized at all.
    /// </exception>
    /// <exception cref="PubSubPayloadTooLargeException">
    /// Thrown when the serialized message exceeds <paramref name="maxMessageBytes"/>.
    /// </exception>
    public static PubSubBroadcastRequest Create(string broadcasterId, JsonElement state, int maxMessageBytes = 5000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);

        if (state.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidPubSubStateException(state.ValueKind);
        }

        var message = JsonSerializer.Serialize(state);
        var byteCount = Encoding.UTF8.GetByteCount(message);
        if (byteCount > maxMessageBytes)
        {
            throw new PubSubPayloadTooLargeException(byteCount, maxMessageBytes);
        }

        return new PubSubBroadcastRequest
        {
            BroadcasterId = broadcasterId,
            Message = message,
        };
    }
}

/// <summary>Thrown when a state payload is missing, <c>null</c> or not a JSON object.</summary>
public sealed class InvalidPubSubStateException(JsonValueKind kind)
    : Exception(kind is JsonValueKind.Undefined or JsonValueKind.Null
        ? "The request must contain a \"state\" JSON object."
        : $"\"state\" must be a JSON object, not {kind.ToString().ToLowerInvariant()}.")
{
}

/// <summary>Thrown when a state payload would exceed Twitch's PubSub message size limit.</summary>
public sealed class PubSubPayloadTooLargeException(int actualBytes, int maxBytes)
    : Exception($"Serialized state payload is {actualBytes} bytes, which exceeds the {maxBytes} byte limit.")
{
    /// <summary>The size, in UTF-8 bytes, of the payload that failed validation.</summary>
    public int ActualBytes { get; } = actualBytes;

    /// <summary>The configured maximum size, in bytes.</summary>
    public int MaxBytes { get; } = maxBytes;
}
