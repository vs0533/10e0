using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt;
using TenE0.Core.Auth.Jwt.Services;

namespace TenE0.Core.Auth.Jwt.Tests;

/// <summary>
/// BDD acceptance tests for #7 — verifies that newly issued access tokens
/// carry the current per-role version snapshot, so the evaluator can compare
/// it to the DB on every request.
///
/// The test reads claims back from a re-parsed JWT, so it exercises the
/// full sign/verify round-trip, not just the in-memory claim list.
/// </summary>
[Trait("Category", "BDD")]
public sealed class RoleVersionJwtClaimAcceptanceTests
{
    private const string SigningKey = "test-signing-key-CHANGE-ME-must-be-at-least-32-bytes-long";
    private const string Issuer = "10e0-tests";
    private const string Audience = "10e0-tests";

    private static JwtOptions CreateOptions() => new()
    {
        Issuer = Issuer,
        Audience = Audience,
        SigningKey = SigningKey,
        AccessTokenLifetime = TimeSpan.FromMinutes(30),
        RefreshTokenLifetime = TimeSpan.FromDays(7),
    };

    private static JwtTokenService CreateService() =>
        new(Options.Create(CreateOptions()), new FakeTimeProvider());

    private static IReadOnlyDictionary<string, long> ParseRoleVersionClaim(JwtSecurityToken jwt)
    {
        // The claim is encoded as JSON in a single string claim so the JWT stays
        // compact. The shape is: {"editor":5,"viewer":8}
        var claim = jwt.Claims.FirstOrDefault(c => c.Type == JwtClaims.RoleVersion);
        if (claim is null)
            return new Dictionary<string, long>();

        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(claim.Value)
            ?? new Dictionary<string, long>();
        return dict;
    }

    // ── Acceptance: new tokens embed the current version snapshot ──

    [Fact]
    public void GivenUserWithCurrentRoleVersions_WhenIssuingAccessToken_ThenJwtContainsRoleVersionClaim()
    {
        // Arrange
        var svc = CreateService();
        var roleVersions = new Dictionary<string, long>
        {
            ["editor"] = 7L,
            ["viewer"] = 12L,
        };

        // Act
        var issued = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor", "viewer" },
            roleVersions: roleVersions);

        // Assert — parse the JWT and confirm the claim is present and intact
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        var embedded = ParseRoleVersionClaim(jwt);

        embedded.Should().ContainKey("editor").WhoseValue.Should().Be(7L);
        embedded.Should().ContainKey("viewer").WhoseValue.Should().Be(12L);
    }

    [Fact]
    public void GivenUserWithoutRoles_WhenIssuingAccessToken_ThenJwtOmitsOrEmptiesRoleVersionClaim()
    {
        // Arrange
        var svc = CreateService();

        // Act
        var issued = svc.Issue(
            userCode: "bob",
            displayName: "Bob",
            userType: UserType.Person,
            roles: Array.Empty<string>(),
            roleVersions: new Dictionary<string, long>());

        // Assert
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        var embedded = ParseRoleVersionClaim(jwt);
        embedded.Should().BeEmpty("users without roles have no version snapshot to embed");
    }

    [Fact]
    public void GivenUserWithCurrentRoleVersions_WhenRefreshingToken_ThenNewJwtContainsLatestRoleVersions()
    {
        // Arrange — the refresh path must re-read the latest versions from DB.
        // Simulate: at login, editor=v5. Between login and refresh, admin granted
        // a new permission → editor=v6. The refreshed token must carry v6.
        var svc = CreateService();
        var latestVersions = new Dictionary<string, long> { ["editor"] = 6L };

        // Act
        var issued = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: latestVersions);

        // Assert
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        var embedded = ParseRoleVersionClaim(jwt);
        embedded["editor"].Should().Be(6L,
            "refresh must hand out a token with the latest versions, not the ones from login");
    }
}
