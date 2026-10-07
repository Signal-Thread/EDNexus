using System.Net;
using Microsoft.Extensions.Options;

namespace EDNexus.Ebs.Options;

/// <summary>
/// Startup validation for <see cref="TwitchEbsOptions"/>: refuse to boot on configuration that would
/// otherwise surface as a 500 (or a confusing Twitch error) on the first request.
/// </summary>
public sealed class TwitchEbsOptionsValidator : IValidateOptions<TwitchEbsOptions>
{
    /// <summary>
    /// Shortest decoded Extension Secret accepted. Twitch issues 32-byte secrets; this only rejects
    /// an obvious placeholder, since an HMAC key this short signs nothing worth trusting.
    /// </summary>
    public const int MinExtensionSecretBytes = 16;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TwitchEbsOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ClientId))
            failures.Add("Twitch:ClientId must be set.");
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
            failures.Add("Twitch:ClientSecret must be set (the OAuth login cannot work without it).");
        if (string.IsNullOrWhiteSpace(options.ExtensionId))
            failures.Add("Twitch:ExtensionId must be set (it is the Client-Id of the PubSub call; it is often the same value as Twitch:ClientId).");

        if (string.IsNullOrWhiteSpace(options.ExtensionSecret))
        {
            failures.Add("Twitch:ExtensionSecret must be set to a non-empty, valid base64 string.");
        }
        else
        {
            byte[]? secret = null;
            try { secret = Convert.FromBase64String(options.ExtensionSecret); }
            catch (FormatException) { failures.Add("Twitch:ExtensionSecret must be set to a non-empty, valid base64 string."); }

            if (secret is { Length: < MinExtensionSecretBytes })
                failures.Add($"Twitch:ExtensionSecret decodes to only {secret.Length} bytes; expected at least {MinExtensionSecretBytes} (Twitch issues 32).");
        }

        if (!Uri.TryCreate(options.OAuthRedirectUri, UriKind.Absolute, out var redirect)
            || !(redirect.Scheme == Uri.UriSchemeHttps || (redirect.Scheme == Uri.UriSchemeHttp && redirect.IsLoopback)))
            failures.Add("Twitch:OAuthRedirectUri must be an absolute https URL (http is allowed only for localhost), exactly as registered with Twitch.");

        if (!string.IsNullOrWhiteSpace(options.OwnerUserId) && !options.OwnerUserId.Trim().All(char.IsAsciiDigit))
            failures.Add("Twitch:OwnerUserId, when set, must be the numeric Twitch user id of the extension owner.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Startup validation for <see cref="EbsOptions"/>.</summary>
public sealed class EbsOptionsValidator : IValidateOptions<EbsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, EbsOptions options)
    {
        var failures = new List<string>();

        // The desktop app refreshes an unchanged live card on a fixed schedule; a shorter limit would
        // take live cards down between refreshes. Refuse to start rather than fail silently for viewers.
        if (options.ChannelStateMaxAgeHours > 0 && options.ChannelStateMaxAgeHours < EbsOptions.MinChannelStateMaxAgeHours)
            failures.Add($"Ebs:ChannelStateMaxAgeHours must be 0 (no limit) or at least {EbsOptions.MinChannelStateMaxAgeHours}: "
                + "a shorter limit could take down the live card of a desktop build that refreshes an unchanged card only every 6 hours.");

        if (options.MaxStatePayloadBytes is < 1 or > EbsOptions.TwitchPubSubMaxMessageBytes)
            failures.Add($"Ebs:MaxStatePayloadBytes must be between 1 and {EbsOptions.TwitchPubSubMaxMessageBytes} (Twitch drops a PubSub message over 5 KiB).");

        if (options.MaxRequestBodyBytes < 1024)
            failures.Add("Ebs:MaxRequestBodyBytes must be at least 1024.");
        else if (options.MaxRequestBodyBytes < options.MaxStatePayloadBytes + 64)
            failures.Add("Ebs:MaxRequestBodyBytes must leave room for a full Ebs:MaxStatePayloadBytes state plus the request envelope.");

        foreach (var proxy in options.TrustedProxies)
            if (!IPAddress.TryParse(proxy, out _))
                failures.Add($"Ebs:TrustedProxies entry '{proxy}' is not an IP address.");
        foreach (var network in options.TrustedProxyNetworks)
            if (!IPNetwork.TryParse(network, out _))
                failures.Add($"Ebs:TrustedProxyNetworks entry '{network}' is not a CIDR range such as 172.18.0.0/16.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
