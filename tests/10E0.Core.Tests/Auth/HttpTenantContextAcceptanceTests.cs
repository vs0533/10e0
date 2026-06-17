using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Moq;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth;

namespace TenE0.Core.Tests.Auth;

/// <summary>
/// BDD acceptance tests for #11 — Part 2 (ITenantContext + HttpTenantContext).
///
/// 验证 HttpTenantContext：
/// 1. 从 HttpContext.User 的 "tenant_id" claim 读取租户 ID
/// 2. 没有 claim / 未认证时返回 null
/// 3. 空字符串 claim 当作 null（不返回空串）
/// 4. 与 HttpCurrentUserContext 走同一 IHttpContextAccessor，互不干扰
/// 5. 多次访问是幂等的（无 I/O 副作用）
/// </summary>
[Trait("Category", "BDD")]
public sealed class HttpTenantContextAcceptanceTests
{
    private static IHttpContextAccessor CreateAccessorWithClaims(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "test");
        var principal = new ClaimsPrincipal(identity);

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.SetupGet(c => c.User).Returns(principal);

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContextMock.Object);
        return accessor.Object;
    }

    private static IHttpContextAccessor CreateAccessorWithNoHttpContext()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns((HttpContext?)null);
        return accessor.Object;
    }

    [Fact]
    public void GivenAuthenticatedRequestWithTenantClaim_WhenResolvingContext_ThenTenantIdMatchesClaim()
    {
        // Arrange — 请求同时带 sub + tenant_id
        var accessor = CreateAccessorWithClaims(
            new Claim(JwtClaims.Subject, "alice"),
            new Claim(JwtClaims.TenantId, "t-42"));

        var ctx = new HttpTenantContext(accessor);

        // Act
        var tenantId = ctx.TenantId;

        // Assert
        tenantId.Should().Be("t-42",
            "the HTTP tenant context must surface the tenant_id claim so the Tenant filter can read it");
    }

    [Fact]
    public void GivenRequestWithoutTenantClaim_WhenResolvingContext_ThenTenantIdIsNull()
    {
        // Arrange — 仅 sub，无 tenant_id（多租户关闭 / 系统账号）
        var accessor = CreateAccessorWithClaims(
            new Claim(JwtClaims.Subject, "system"));

        var ctx = new HttpTenantContext(accessor);

        // Act
        var tenantId = ctx.TenantId;

        // Assert
        tenantId.Should().BeNull(
            "users without a tenant claim (system accounts, legacy tokens) must yield a null TenantId, " +
            "which the Tenant filter treats as 'no rows visible' — safe default");
    }

    [Fact]
    public void GivenUnauthenticatedRequest_WhenResolvingContext_ThenTenantIdIsNull()
    {
        // Arrange — anonymous request (no claims at all)
        var accessor = CreateAccessorWithNoHttpContext();

        var ctx = new HttpTenantContext(accessor);

        // Act
        var tenantId = ctx.TenantId;

        // Assert
        tenantId.Should().BeNull(
            "an unauthenticated/anonymous request has no tenant — must not throw and must be null");
    }

    [Fact]
    public void GivenBlankTenantClaim_WhenResolvingContext_ThenTenantIdIsNullNotEmptyString()
    {
        // Arrange — claim 存在但值是空串（防 JWT 注入被遗留下）
        var accessor = CreateAccessorWithClaims(
            new Claim(JwtClaims.TenantId, "   "));

        var ctx = new HttpTenantContext(accessor);

        // Act
        var tenantId = ctx.TenantId;

        // Assert
        tenantId.Should().BeNull(
            "whitespace-only tenant claim must be treated as absent — otherwise the filter would do " +
            "TenantId == '' which matches nothing but is semantically wrong");
    }

    [Fact]
    public void GivenAuthenticatedRequest_WhenResolvingContextRepeatedly_ThenReadsAreIdempotentAndSideEffectFree()
    {
        // Arrange
        var accessor = CreateAccessorWithClaims(
            new Claim(JwtClaims.Subject, "bob"),
            new Claim(JwtClaims.TenantId, "t-7"));

        var ctx = new HttpTenantContext(accessor);

        // Act
        var first = ctx.TenantId;
        var second = ctx.TenantId;
        var third = ctx.TenantId;

        // Assert
        first.Should().Be("t-7");
        second.Should().Be("t-7");
        third.Should().Be("t-7",
            "the tenant context is a thin reader over Claims — repeated calls must return the same value " +
            "without any caching or state mutation");
    }
}
