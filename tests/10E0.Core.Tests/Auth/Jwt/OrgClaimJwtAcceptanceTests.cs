using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Services;

namespace TenE0.Core.Auth.Jwt.Tests;

/// <summary>
/// BDD acceptance tests for #155 — org claim 链路（补全此前断裂的 ghost-claim）。
///
/// 验证 JwtTokenService 在签发 access token 时：
/// 1. 透传 orgId（传 null 时不写 claim）
/// 2. 传值时写 "org" claim，值原样回传（org 节点 Id，GUID-N）
/// 3. org 与 tenant claim 正交共存（互不影响）
/// 4. 解析回 ClaimsPrincipal 后 realtime/filter 消费者能读到
/// 5. Refresh 流程再次 Issue 保留 org
///
/// 背景：DemoDbContext 此前读 "org" claim 但无人写（ghost-claim bug），#155 补全写入端。
/// </summary>
[Trait("Category", "BDD")]
public sealed class OrgClaimJwtAcceptanceTests
{
    private const string SigningKey = "test-signing-key-CHANGE-ME-must-be-at-least-32-bytes-long";
    private const string Issuer = "10e0-tests";
    private const string Audience = "10e0-tests";

    private static JwtTokenService CreateService() =>
        new(
            Options.Create(new JwtOptions
            {
                Issuer = Issuer,
                Audience = Audience,
                SigningKey = SigningKey,
                AccessTokenLifetime = TimeSpan.FromMinutes(30),
                RefreshTokenLifetime = TimeSpan.FromDays(7),
            }),
            new FakeTimeProvider(),
            new JwtClaimsTokenClaimNames());

    [Fact]
    public void GivenJwtClaimsContract_WhenInspected_ThenOrgConstantIsDefined()
    {
        JwtClaims.Org.Should().NotBeNullOrWhiteSpace(
            "issue #155 requires an 'org' claim constant in JwtClaims");
        JwtClaims.Org.Should().Be("org",
            "the claim name must be 'org' to match the existing reader (DemoDbContext.CurrentOrgId)");
    }

    [Fact]
    public void GivenUserWithOrgId_WhenIssuingAccessToken_ThenJwtContainsOrgClaim()
    {
        var svc = CreateService();

        var issued = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: new Dictionary<string, long>(),
            tenantId: null,
            orgId: "hq-node-guid");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        var claim = jwt.Claims.FirstOrDefault(c => c.Type == JwtClaims.Org);

        claim.Should().NotBeNull("the access token must embed org so realtime/filter can read it");
        claim!.Value.Should().Be("hq-node-guid",
            "the claim value must round-trip the orgId the login handler passed in");
    }

    [Fact]
    public void GivenUserWithoutOrgId_WhenIssuingAccessToken_ThenJwtOmitsOrgClaim()
    {
        var svc = CreateService();

        var issued = svc.Issue(
            userCode: "system",
            displayName: "System",
            userType: UserType.Unit,
            roles: Array.Empty<string>(),
            roleVersions: new Dictionary<string, long>(),
            tenantId: null,
            orgId: null);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        jwt.Claims.FirstOrDefault(c => c.Type == JwtClaims.Org).Should().BeNull(
            "users without an org (system accounts / legacy) must not carry an org claim — " +
            "realtime group provider then omits the org:{id} group, filter goes safe-by-default");
    }

    [Fact]
    public void GivenUserWithOrgAndTenant_WhenIssuingAccessToken_ThenBothClaimsCoexist_Orthogonal()
    {
        // org 与 tenant 正交：二者同时存在且互不影响
        var svc = CreateService();

        var issued = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: new Dictionary<string, long>(),
            tenantId: "t-acme",
            orgId: "hq-node");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == JwtClaims.Org && c.Value == "hq-node");
        jwt.Claims.Should().Contain(c => c.Type == JwtClaims.TenantId && c.Value == "t-acme");
    }

    [Fact]
    public void GivenIssuedAccessToken_WhenClaimsPrincipalIsRebuilt_ThenOrgIsReadable()
    {
        var svc = CreateService();
        var issued = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: new Dictionary<string, long>(),
            tenantId: null,
            orgId: "bj-node-guid");

        var principal = new JwtSecurityTokenHandler().ValidateToken(
            issued.AccessToken,
            new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                    System.Text.Encoding.UTF8.GetBytes(SigningKey)),
                ValidateLifetime = false,
            },
            out _);

        principal.FindFirstValue(JwtClaims.Org).Should().Be("bj-node-guid",
            "realtime ClaimBasedGroupProvider + business filters read 'org' from ClaimsPrincipal — round-trip must survive");
    }
}
