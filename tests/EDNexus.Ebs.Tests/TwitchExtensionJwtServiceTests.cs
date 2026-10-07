using System.Text;
using System.Text.Json;
using EDNexus.Ebs.Security;
using Microsoft.Extensions.Options;
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

    private static string EncodeToken(object header, object payload, byte[] key)
    {
        string Segment(object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        var headerSegment = Segment(header);
        var payloadSegment = Segment(payload);
        var signingInput = Encoding.ASCII.GetBytes($"{headerSegment}.{payloadSegment}");
        var signature = System.Security.Cryptography.HMACSHA256.HashData(key, signingInput);
        var signatureSegment = Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{headerSegment}.{payloadSegment}.{signatureSegment}";
    }

    [Fact]
    public void Validate_AcceptsWellFormedBroadcasterToken()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var service = CreateService(timeProvider);
        var key = Convert.FromBase64String(SecretBase64);

        var token = EncodeToken(
            new { alg = "HS256", typ = "JWT" },
            new
            {
                exp = now.AddMinutes(5).ToUnixTimeSeconds(),
                channel_id = "12345",
                user_id = "12345",
                opaque_user_id = "U12345",
                role = "broadcaster",
                pubsub_perms = new { listen = new[] { "broadcast" } },
            },
            key);

        var result = service.Validate(token);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Claims);
        Assert.Equal("12345", result.Claims!.ChannelId);
        Assert.True(result.Claims.IsBroadcaster);
    }

    [Fact]
    public void Validate_RejectsTokenSignedWithWrongSecret()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var service = CreateService(timeProvider);
        var wrongKey = Convert.FromBase64String(Convert.ToBase64String(Encoding.UTF8.GetBytes("totally-different-secret")));

        var token = EncodeToken(
            new { alg = "HS256", typ = "JWT" },
            new { exp = now.AddMinutes(5).ToUnixTimeSeconds(), channel_id = "12345", role = "broadcaster" },
            wrongKey);

        var result = service.Validate(token);

        Assert.False(result.IsValid);
        Assert.Equal("Signature verification failed.", result.Error);
    }

    [Fact]
    public void Validate_RejectsExpiredToken()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var service = CreateService(timeProvider);
        var key = Convert.FromBase64String(SecretBase64);

        var token = EncodeToken(
            new { alg = "HS256", typ = "JWT" },
            new { exp = now.AddMinutes(-10).ToUnixTimeSeconds(), channel_id = "12345", role = "broadcaster" },
            key);

        var result = service.Validate(token);

        Assert.False(result.IsValid);
        Assert.Equal("Token has expired.", result.Error);
    }

    [Fact]
    public void Validate_HonoursClockSkewGraceWindow()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var options = new EbsOptions { ExtensionSecret = SecretBase64, ClockSkewSeconds = 30 };
        var service = CreateService(timeProvider, options);
        var key = Convert.FromBase64String(SecretBase64);

        // Expired 10 seconds ago, but within the 30 second clock skew grace window.
        var token = EncodeToken(
            new { alg = "HS256", typ = "JWT" },
            new { exp = now.AddSeconds(-10).ToUnixTimeSeconds(), channel_id = "12345", role = "broadcaster" },
            key);

        var result = service.Validate(token);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsUnsupportedAlgorithm()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var service = CreateService(timeProvider);
        var key = Convert.FromBase64String(SecretBase64);

        var token = EncodeToken(
            new { alg = "none", typ = "JWT" },
            new { exp = now.AddMinutes(5).ToUnixTimeSeconds(), channel_id = "12345", role = "broadcaster" },
            key);

        var result = service.Validate(token);

        Assert.False(result.IsValid);
        Assert.Contains("HS256", result.Error);
    }

    [Fact]
    public void Validate_RejectsMalformedToken()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = CreateService(timeProvider);

        var result = service.Validate("not-a-jwt");

        Assert.False(result.IsValid);
        Assert.Contains("header.payload.signature", result.Error);
    }

    [Fact]
    public void Validate_RejectsTamperedPayload()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var service = CreateService(timeProvider);
        var key = Convert.FromBase64String(SecretBase64);

        var token = EncodeToken(
            new { alg = "HS256", typ = "JWT" },
            new { exp = now.AddMinutes(5).ToUnixTimeSeconds(), channel_id = "12345", role = "broadcaster" },
            key);

        var parts = token.Split('.');
        var tamperedPayload = EncodeToken(
                new { alg = "HS256", typ = "JWT" },
                new { exp = now.AddMinutes(5).ToUnixTimeSeconds(), channel_id = "99999", role = "broadcaster" },
                key)
            .Split('.')[1];
        var tamperedToken = $"{parts[0]}.{tamperedPayload}.{parts[2]}";

        var result = service.Validate(tamperedToken);

        Assert.False(result.IsValid);
        Assert.Equal("Signature verification failed.", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(12345)] // present, but not a string
    public void Validate_RejectsTokenWithoutChannelId(object? channelId)
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var service = CreateService(timeProvider);
        var key = Convert.FromBase64String(SecretBase64);

        var payload = new Dictionary<string, object>
        {
            ["exp"] = now.AddMinutes(5).ToUnixTimeSeconds(),
            ["role"] = "broadcaster",
        };
        if (channelId is not null)
        {
            payload["channel_id"] = channelId;
        }

        var result = service.Validate(EncodeToken(new { alg = "HS256", typ = "JWT" }, payload, key));

        Assert.False(result.IsValid);
        Assert.Equal("Token is missing the 'channel_id' claim.", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("Broadcaster")] // roles are matched exactly
    [InlineData(1)]
    public void Validate_RejectsTokenWithMissingOrUnknownRole(object? role)
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var service = CreateService(timeProvider);
        var key = Convert.FromBase64String(SecretBase64);

        var payload = new Dictionary<string, object>
        {
            ["exp"] = now.AddMinutes(5).ToUnixTimeSeconds(),
            ["channel_id"] = "12345",
        };
        if (role is not null)
        {
            payload["role"] = role;
        }

        var result = service.Validate(EncodeToken(new { alg = "HS256", typ = "JWT" }, payload, key));

        Assert.False(result.IsValid);
        Assert.Equal(
            "Unsupported or missing 'role' claim; only broadcaster, moderator, viewer, and external are accepted.",
            result.Error);
    }

    [Fact]
    public void Validate_RejectsTokenThatIsNotYetValid()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var options = new EbsOptions { ExtensionSecret = SecretBase64, ClockSkewSeconds = 30 };
        var service = CreateService(timeProvider, options);
        var key = Convert.FromBase64String(SecretBase64);

        // Not valid for another 31 seconds: one second beyond the 30 second clock skew.
        var token = EncodeToken(
            new { alg = "HS256", typ = "JWT" },
            new
            {
                exp = now.AddMinutes(5).ToUnixTimeSeconds(),
                nbf = now.AddSeconds(31).ToUnixTimeSeconds(),
                channel_id = "12345",
                role = "broadcaster",
            },
            key);

        var result = service.Validate(token);

        Assert.False(result.IsValid);
        Assert.Equal("Token is not yet valid.", result.Error);
    }

    [Fact]
    public void Validate_HonoursClockSkewGraceWindowForNotBefore()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var options = new EbsOptions { ExtensionSecret = SecretBase64, ClockSkewSeconds = 30 };
        var service = CreateService(timeProvider, options);
        var key = Convert.FromBase64String(SecretBase64);

        // Not valid for another 30 seconds, which is exactly at the edge of the clock skew grace window.
        var token = EncodeToken(
            new { alg = "HS256", typ = "JWT" },
            new
            {
                exp = now.AddMinutes(5).ToUnixTimeSeconds(),
                nbf = now.AddSeconds(30).ToUnixTimeSeconds(),
                channel_id = "12345",
                role = "broadcaster",
            },
            key);

        var result = service.Validate(token);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsTokenWithNonNumericNotBefore()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var service = CreateService(timeProvider);
        var key = Convert.FromBase64String(SecretBase64);

        var token = EncodeToken(
            new { alg = "HS256", typ = "JWT" },
            new { exp = now.AddMinutes(5).ToUnixTimeSeconds(), nbf = "soon", channel_id = "12345", role = "broadcaster" },
            key);

        var result = service.Validate(token);

        Assert.False(result.IsValid);
        Assert.Equal("Token has a malformed 'nbf' claim.", result.Error);
    }

    [Fact]
    public void Validate_EnforcesAllowedRolesWhenSupplied()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var service = CreateService(timeProvider);
        var key = Convert.FromBase64String(SecretBase64);

        var token = EncodeToken(
            new { alg = "HS256", typ = "JWT" },
            new { exp = now.AddMinutes(5).ToUnixTimeSeconds(), channel_id = "12345", role = "viewer" },
            key);

        Assert.True(service.Validate(token).IsValid);
        Assert.True(service.Validate(token, allowedRoles: ["broadcaster", "viewer"]).IsValid);

        var rejected = service.Validate(token, allowedRoles: ["broadcaster", "moderator"]);
        Assert.False(rejected.IsValid);
        Assert.Equal("Token role 'viewer' is not one of the allowed roles.", rejected.Error);

        // An empty allow-list admits no one rather than everyone.
        Assert.False(service.Validate(token, allowedRoles: []).IsValid);
    }

    [Fact]
    public void CreateExternalServiceToken_ProducesTokenValidatableByTwitchShapedVerifier()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var options = new EbsOptions { ExtensionSecret = SecretBase64, OutboundTokenLifetimeSeconds = 180 };
        var service = CreateService(timeProvider, options);

        var token = service.CreateExternalServiceToken("54321");

        var key = Convert.FromBase64String(SecretBase64);
        var result = TwitchExtensionJwtService.Validate(token, key, clockSkewSeconds: 0, now);

        Assert.True(result.IsValid);
        Assert.Equal("54321", result.Claims!.ChannelId);
        Assert.Equal("external", result.Claims.Role);
    }

    [Fact]
    public void CreateExternalServiceToken_RoundTripsThroughValidate()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var service = CreateService(timeProvider);

        var token = service.CreateExternalServiceToken("54321");
        var result = service.Validate(token, allowedRoles: ["external"]);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("54321", result.Claims!.ChannelId);
        Assert.Equal("external", result.Claims.Role);
        Assert.Equal("ednexus_ebs", result.Claims.UserId);
        Assert.Null(result.Claims.OpaqueUserId);
    }

    [Fact]
    public void CreateExternalServiceToken_RequiresChannelId()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = CreateService(timeProvider);

        Assert.Throws<ArgumentException>(() => service.CreateExternalServiceToken(""));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
