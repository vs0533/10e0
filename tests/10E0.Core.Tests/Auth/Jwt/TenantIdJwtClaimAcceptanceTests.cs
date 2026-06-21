using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Services;

namespace TenE0.Core.Auth.Jwt.Tests;

/// <summary>
/// BDD acceptance tests for #11 — Part 4 (JWT claim 集成).
///
/// 验证 JwtTokenService 在签发 access token 时：
/// 1. 透传 tenantId（业务方传 null 时不写 claim）
/// 2. 透传 tenantId（业务方传值时写 "tenant_id" claim，值原样回传）
/// 3. 与现有 role / role_versions claim 共存
/// 4. 解析回 ClaimsPrincipal 后 ITenantContext 风格的消费者能拿到
/// 5. Refresh 流程（重新 Issue）必须再次把同一 tenantId 写进新 token
///
/// 失败模式：未实现前，token 不带 tenant_id claim → 解析后 TenantId 为 null，
/// 在 EF Tenant filter 启用后所有业务实体读不到 → 跨租户隔离测试全 RED。
/// </summary>
[Trait("Category", "BDD")]
public sealed class TenantIdJwtClaimAcceptanceTests
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
            // #37: 测试走默认 ITokenClaimNames（JwtClaims 常量），保持向后兼容
            new JwtClaimsTokenClaimNames());

    // ── 静态 contract: claim 常量与解析逻辑 ─────────────────────

    [Fact]
    public void GivenJwtClaimsContract_WhenInspected_ThenTenantIdConstantIsDefined()
    {
        // Arrange + Act
        var constant = JwtClaims.TenantId;

        // Assert
        constant.Should().NotBeNullOrWhiteSpace(
            "issue #11 requires a 'tenant_id' claim constant in JwtClaims");
        constant.Should().Be("tenant_id",
            "the claim name must be the conventional snake_case 'tenant_id' so business code can spot it");
    }

    // ── 签发: tenantId 写进 claim ─────────────────────────────────

    [Fact]
    public void GivenUserWithTenantId_WhenIssuingAccessToken_ThenJwtContainsTenantIdClaim()
    {
        // Arrange
        var svc = CreateService();

        // Act
        var issued = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: new Dictionary<string, long>(),
            tenantId: "t-acme");

        // Assert
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        var claim = jwt.Claims.FirstOrDefault(c => c.Type == JwtClaims.TenantId);

        claim.Should().NotBeNull(
            "the access token must embed tenant_id so downstream middleware (HttpTenantContext) can read it");
        claim!.Value.Should().Be("t-acme",
            "the claim value must round-trip the tenantId the login handler passed in");
    }

    [Fact]
    public void GivenUserWithoutTenantId_WhenIssuingAccessToken_ThenJwtOmitsTenantIdClaim()
    {
        // Arrange
        var svc = CreateService();

        // Act
        var issued = svc.Issue(
            userCode: "system",
            displayName: "System",
            userType: UserType.Unit,
            roles: Array.Empty<string>(),
            roleVersions: new Dictionary<string, long>(),
            tenantId: null);

        // Assert — 不应写空串 claim（避免下游把它当真值）
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        var claim = jwt.Claims.FirstOrDefault(c => c.Type == JwtClaims.TenantId);

        claim.Should().BeNull(
            "users without a tenant (system accounts, legacy users) must not have a tenant_id claim " +
            "— otherwise the EF filter would compare TenantId == '' and break for them");
    }

    [Fact]
    public void GivenUserWithTenantIdAndRoles_WhenIssuingAccessToken_ThenAllClaimsCoexist()
    {
        // Arrange
        var svc = CreateService();

        // Act
        var issued = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor", "viewer" },
            roleVersions: new Dictionary<string, long> { ["editor"] = 3L, ["viewer"] = 5L },
            tenantId: "t-acme");

        // Assert
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == JwtClaims.Subject && c.Value == "alice");
        jwt.Claims.Should().Contain(c => c.Type == JwtClaims.TenantId && c.Value == "t-acme");
        jwt.Claims.Where(c => c.Type == JwtClaims.Role).Select(c => c.Value)
            .Should().BeEquivalentTo("editor", "viewer");
        jwt.Claims.Should().Contain(c => c.Type == JwtClaims.RoleVersion);
    }

    // ── 端到端: 解析回的 ClaimsPrincipal 仍能取到 tenant ─────────

    [Fact]
    public void GivenIssuedAccessToken_WhenClaimsPrincipalIsRebuilt_ThenTenantIdIsReadableFromPrincipal()
    {
        // Arrange
        var svc = CreateService();
        var issued = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: new Dictionary<string, long>(),
            tenantId: "t-globex");

        // Act — simulate HttpTenantContext reading the principal back
        var handler = new JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(
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

        var tenantId = principal.FindFirstValue(JwtClaims.TenantId);

        // Assert
        tenantId.Should().Be("t-globex",
            "HttpTenantContext reads tenant_id from ClaimsPrincipal — the round-trip must survive validation");
    }

    // ── Refresh 流程: 再次 Issue 必须保留 tenantId ───────────────

    [Fact]
    public void GivenUserWithTenantId_WhenRefreshIssuesNewToken_ThenNewTokenAlsoCarriesTenantId()
    {
        // Arrange — Login 阶段 token 带 tenant
        var svc = CreateService();
        var login = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: new Dictionary<string, long>(),
            tenantId: "t-acme");

        // Act — Refresh 阶段：业务方从 DB 重新读 user，再用同一 tenantId 调 Issue
        var refreshed = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: new Dictionary<string, long> { ["editor"] = 4L },
            tenantId: "t-acme");

        // Assert — 新 token 也带 tenant_id
        var loginJwt = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken);
        var refreshJwt = new JwtSecurityTokenHandler().ReadJwtToken(refreshed.AccessToken);

        refreshJwt.Claims.First(c => c.Type == JwtClaims.TenantId).Value.Should().Be("t-acme",
            "the refresh path must re-emit tenant_id; otherwise the user 'loses' their tenant after refresh " +
            "and the next request would be denied by the Tenant filter");
        loginJwt.Claims.First(c => c.Type == JwtClaims.TenantId).Value.Should().Be("t-acme");
    }

    [Fact]
    public void GivenUserSwitchesTenantOnRefresh_WhenRefreshIssuesNewToken_ThenNewTokenCarriesNewTenantId()
    {
        // Arrange — 模拟：用户原本在 t-acme，admin 把人迁到 t-globex。Refresh 必须反映最新值。
        var svc = CreateService();

        // Act
        var refreshed = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: new Dictionary<string, long>(),
            tenantId: "t-globex");

        // Assert
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(refreshed.AccessToken);
        jwt.Claims.First(c => c.Type == JwtClaims.TenantId).Value.Should().Be("t-globex",
            "refresh must read tenantId from the latest DB value, not from the old token's claim");
    }
}
