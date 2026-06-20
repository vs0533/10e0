using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt;
using TenE0.Core.Auth.Jwt.Services;

namespace TenE0.Core.Tests.Auth.Jwt;

public sealed class JwtTokenServiceTests
{
    private JwtTokenService CreateService(TimeProvider? timeProvider = null)
    {
        var options = new Mock<IOptions<JwtOptions>>();
        options.Setup(o => o.Value).Returns(new JwtOptions
        {
            Issuer = "test-issuer",
            Audience = "test-audience",
            SigningKey = "test-secret-key-at-least-32-bytes-long!!",
            AccessTokenLifetime = TimeSpan.FromMinutes(30),
            RefreshTokenLifetime = TimeSpan.FromDays(7),
        });

        var tp = timeProvider ?? TimeProvider.System;
        return new JwtTokenService(options.Object, tp);
    }

    [Fact]
    public void Issue_ShouldReturnNonEmptyAccessToken()
    {
        var service = CreateService();
        var tokens = service.Issue("user01", "John Doe", UserType.Person, ["admin"], new Dictionary<string, long>());

        tokens.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Issue_AccessTokenShouldBeValidJwt()
    {
        var service = CreateService();
        var tokens = service.Issue("user01", "John Doe", UserType.Person, ["admin"], new Dictionary<string, long>());

        var parts = tokens.AccessToken.Split('.');
        parts.Should().HaveCount(3, "JWT tokens have header.payload.signature parts");
    }

    [Fact]
    public void Issue_ShouldContainCorrectClaims()
    {
        var service = CreateService();
        var tokens = service.Issue("user01", "John Doe", UserType.Person, ["admin", "editor"], new Dictionary<string, long>());

        var handler = new JwtSecurityTokenHandler();
        var jwtToken = handler.ReadJwtToken(tokens.AccessToken);

        jwtToken.Claims.Should().Contain(c => c.Type == JwtClaims.Subject && c.Value == "user01");
        jwtToken.Claims.Should().Contain(c => c.Type == JwtClaims.Name && c.Value == "John Doe");
        jwtToken.Claims.Should().Contain(c => c.Type == JwtClaims.UserType && c.Value == "Person");
        jwtToken.Claims.Should().Contain(c => c.Type == JwtClaims.Role && c.Value == "admin");
        jwtToken.Claims.Should().Contain(c => c.Type == JwtClaims.Role && c.Value == "editor");
    }

    [Fact]
    public void Issue_ShouldIncludeJti()
    {
        var service = CreateService();
        var tokens = service.Issue("user01", "John Doe", UserType.Person, [], new Dictionary<string, long>());

        var handler = new JwtSecurityTokenHandler();
        var jwtToken = handler.ReadJwtToken(tokens.AccessToken);

        var jti = jwtToken.Claims.FirstOrDefault(c => c.Type == "jti");
        jti.Should().NotBeNull();
        jti!.Value.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Issue_ShouldHaveCorrectExpiration()
    {
        var fixedTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new Mock<TimeProvider>();
        timeProvider.Setup(t => t.GetUtcNow()).Returns(fixedTime);

        var service = CreateService(timeProvider.Object);
        var tokens = service.Issue("user01", "John Doe", UserType.Person, [], new Dictionary<string, long>());

        tokens.AccessTokenExpiresAt.Should().Be(fixedTime.AddMinutes(30));
        tokens.RefreshTokenExpiresAt.Should().Be(fixedTime.AddDays(7));
    }

    [Fact]
    public void Issue_ShouldReturnRefreshToken()
    {
        var service = CreateService();
        var tokens = service.Issue("user01", "John Doe", UserType.Person, [], new Dictionary<string, long>());

        tokens.RefreshToken.Should().NotBeNullOrEmpty();
        tokens.RefreshTokenHash.Should().NotBeNullOrEmpty();
        tokens.RefreshToken.Should().NotBe(tokens.RefreshTokenHash);
    }

    [Fact]
    public void GenerateRefreshToken_ShouldReturnCorrectTuple()
    {
        var fixedTime = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new Mock<TimeProvider>();
        timeProvider.Setup(t => t.GetUtcNow()).Returns(fixedTime);

        var service = CreateService(timeProvider.Object);
        var (token, hash, expiresAt) = service.GenerateRefreshToken();

        token.Should().NotBeNullOrEmpty();
        hash.Should().NotBeNullOrEmpty();
        expiresAt.Should().Be(fixedTime.AddDays(7));
    }

    [Fact]
    public void GenerateRefreshToken_New_Call_ShouldReturnDifferentToken()
    {
        var service = CreateService();
        var first = service.GenerateRefreshToken();
        var second = service.GenerateRefreshToken();

        first.Token.Should().NotBe(second.Token);
        first.Hash.Should().NotBe(second.Hash);
    }

    [Fact]
    public void HashRefreshToken_SameInput_ShouldReturnSameHash()
    {
        var service = CreateService();
        var input = "some-refresh-token-string";

        var hash1 = service.HashRefreshToken(input);
        var hash2 = service.HashRefreshToken(input);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void HashRefreshToken_DifferentInput_ShouldReturnDifferentHash()
    {
        var service = CreateService();

        var hash1 = service.HashRefreshToken("token-a");
        var hash2 = service.HashRefreshToken("token-b");

        hash1.Should().NotBe(hash2);
    }
}
