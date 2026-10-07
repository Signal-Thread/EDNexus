using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EDNexus.Ebs.Security;
using EbsOptions = EDNexus.Ebs.Options.TwitchEbsOptions;

namespace EDNexus.Ebs.Tests;

public class TwitchExtensionJwtServiceTests
{
    private const string SecretBase64 = "c3VwZXItc2VjcmV0LWV4dGVuc2lvbi1rZXktMTIzNA=="; // "super-secret-extension-key-1234"

    private static TwitchExtensionJwtService CreateService(FakeTimeProvider timeProvider, EbsOptions? options = null)
    {
        options ??= new EbsOptions { ExtensionSecret = SecretBase64 };
        return new TwitchExtensionJwtService(Microsoft.Extensions.Options.Options.Create(options), timeProvider);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }

    /// <summary>What Twitch does on receipt: verify the HS256 signature with the extension secret, then read the claims.</summary>
    private static JsonElement VerifyAndReadClaims(string token, byte[] key)
    {
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        Assert.True(CryptographicOperations.FixedTimeEquals(expected, Base64UrlDecode(parts[2])), "signature does not verify");
        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        Assert.Equal("HS256", header.RootElement.GetProperty("alg").GetString());
        return JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement.Clone();
    }

    [Fact]
    public void CreateExternalServiceToken_produces_a_token_Twitch_can_verify_with_the_extension_secret()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var service = CreateService(new FakeTimeProvider(now), new EbsOptions { ExtensionSecret = SecretBase64, OutboundTokenLifetimeSeconds = 180 });

        var claims = VerifyAndReadClaims(service.CreateExternalServiceToken("54321"), Convert.FromBase64String(SecretBase64));

        Assert.Equal("54321", claims.GetProperty("channel_id").GetString());
        Assert.Equal("external", claims.GetProperty("role").GetString());
        Assert.Equal(now.AddSeconds(180).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());
        Assert.Equal(now.ToUnixTimeSeconds(), claims.GetProperty("iat").GetInt64());
        Assert.Equal("broadcast", claims.GetProperty("pubsub_perms").GetProperty("send")[0].GetString());
    }

    [Fact]
    public void A_token_signed_with_a_different_secret_does_not_verify()
    {
        var service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var token = service.CreateExternalServiceToken("54321");

        Assert.ThrowsAny<Exception>(() => VerifyAndReadClaims(token, Encoding.UTF8.GetBytes("some-other-secret-entirely-0000")));
    }

    [Fact]
    public void The_user_id_claim_is_the_configured_extension_owner()
    {
        var options = new EbsOptions { ExtensionSecret = SecretBase64, OwnerUserId = "1234567" };
        var service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow), options);

        var claims = VerifyAndReadClaims(service.CreateExternalServiceToken("54321"), Convert.FromBase64String(SecretBase64));

        Assert.Equal("1234567", claims.GetProperty("user_id").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_an_owner_the_user_id_keeps_its_historical_value(string? owner)
    {
        var options = new EbsOptions { ExtensionSecret = SecretBase64, OwnerUserId = owner };
        var service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow), options);

        var claims = VerifyAndReadClaims(service.CreateExternalServiceToken("54321"), Convert.FromBase64String(SecretBase64));

        Assert.Equal(TwitchExtensionJwtService.FallbackUserId, claims.GetProperty("user_id").GetString());
    }

    [Fact]
    public void CreateExternalServiceToken_RequiresChannelId()
    {
        var service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentException>(() => service.CreateExternalServiceToken(""));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
