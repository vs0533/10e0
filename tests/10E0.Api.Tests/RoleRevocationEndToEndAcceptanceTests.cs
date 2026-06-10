using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.DynamicFilters;
using TenE0.Core.Permissions.DataFilter;

namespace TenE0.Api.Tests;

/// <summary>
/// BDD acceptance tests for #7 — covers the end-to-end "instant revocation"
/// guarantee from the issue's first acceptance criterion:
///
///   "管理员撤销某用户角色权限后，该用户下一个 HTTP 请求立即返回 403
///    （无需等 token 过期）"
///
/// Each test boots a fresh WAF with a per-test InMemory database (named
/// uniquely so the seeders run on a clean slate) and walks the full HTTP
/// flow: login → call protected endpoint → admin revoke → reuse the
/// original (un-refreshed) access token → assert 403.
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
public sealed class RoleRevocationEndToEndAcceptanceTests
{
    private static IsolatedFactory CreateFactory() => new();

    // ── End-to-end: revoke on a live token ────────────────────

    [Fact]
    public async Task GivenAuthenticatedUserWithViewerRole_WhenAdminRevokesDemoView_ThenNextRequestReturns403WithoutTokenRefresh()
    {
        // Arrange — fresh isolated host with its own InMemory DB
        // alice 在该实例里只持有 viewer（测试名 GivenUserWithViewerRole 暗示）
        using var factory = new IsolatedFactory(new[] { "viewer" });
        await factory.ResetAliceRolesAsync();
        var aliceClient = factory.CreateClient();
        var loginResp = await aliceClient.PostAsJsonAsync(
            "/auth/login", new { userCode = "alice", password = "111111" });
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "alice is seeded in AuthSeeder with password 111111");
        var aliceAuth = await loginResp.Content.ReadFromJsonAsync<AuthResponseDto>();
        aliceAuth.Should().NotBeNull();
        aliceAuth!.AccessToken.Should().NotBeNullOrWhiteSpace();

        // Alice can read /demo with the original token
        aliceClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);
        var firstRead = await aliceClient.GetAsync("/demo");
        firstRead.StatusCode.Should().Be(HttpStatusCode.OK,
            "viewer role grants demo.view, and alice's token claims it");

        // Act — admin revokes demo.view from the viewer role
        // (Issue: revocation must take effect on alice's NEXT request, even with
        //  the same un-refreshed access token.)
        var adminClient = factory.CreateClient();
        var adminLoginResp = await adminClient.PostAsJsonAsync(
            "/auth/login", new { userCode = "admin", password = "111111" });
        adminLoginResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminAuth = await adminLoginResp.Content.ReadFromJsonAsync<AuthResponseDto>();
        adminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminAuth!.AccessToken);

        var revokeResp = await adminClient.DeleteAsync(
            "/admin/roles/viewer/permissions/demo.view");
        revokeResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "admin has perm.admin and should be able to revoke");

        // Assert — alice re-uses her OLD access token, no /auth/refresh in between
        var secondRead = await aliceClient.GetAsync("/demo");

        secondRead.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the version mismatch between alice's token and the DB must trigger an immediate 403");
    }

    [Fact]
    public async Task GivenAuthenticatedUserWithRevokedPermission_WhenUserRefreshesToken_ThenRefreshedTokenStillCannotAccessEndpoint()
    {
        // Arrange — fresh isolated host; alice 持有 viewer
        using var factory = new IsolatedFactory(new[] { "viewer" });
        await factory.ResetAliceRolesAsync();
        var aliceClient = factory.CreateClient();
        var loginResp = await aliceClient.PostAsJsonAsync(
            "/auth/login", new { userCode = "alice", password = "111111" });
        var aliceAuth = await loginResp.Content.ReadFromJsonAsync<AuthResponseDto>();

        // Admin revokes demo.view from the viewer role BEFORE alice refreshes
        var adminClient = factory.CreateClient();
        var adminLoginResp = await adminClient.PostAsJsonAsync(
            "/auth/login", new { userCode = "admin", password = "111111" });
        var adminAuth = await adminLoginResp.Content.ReadFromJsonAsync<AuthResponseDto>();
        adminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminAuth!.AccessToken);
        await adminClient.DeleteAsync("/admin/roles/viewer/permissions/demo.view");

        // Act — alice calls /auth/refresh to get a brand-new access token
        var refreshResp = await aliceClient.PostAsJsonAsync(
            "/auth/refresh", new { refreshToken = aliceAuth!.RefreshToken });
        refreshResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "rotation flow should still succeed; the issue is permission, not token validity");
        var refreshed = await refreshResp.Content.ReadFromJsonAsync<AuthResponseDto>();

        // Assert — even the freshly-issued token cannot access /demo
        aliceClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", refreshed!.AccessToken);
        var resp = await aliceClient.GetAsync("/demo");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the refreshed token must embed the latest role versions, so the revocation stays in effect");
    }

    [Fact]
    public async Task GivenAuthenticatedUserWithStaleToken_WhenAdminGrantsNewPermission_ThenNewPermissionIsImmediatelyVisible()
    {
        // Arrange — fresh isolated host
        using var factory = CreateFactory();
        var aliceClient = factory.CreateClient();
        var loginResp = await aliceClient.PostAsJsonAsync(
            "/auth/login", new { userCode = "alice", password = "111111" });
        var aliceAuth = await loginResp.Content.ReadFromJsonAsync<AuthResponseDto>();
        aliceClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth!.AccessToken);

        // Sanity: alice can already create demos (editor grants demo.create)
        var preGrantCreate = await aliceClient.PostAsJsonAsync(
            "/demo", new { name = "before-grant", orgId = (string?)null, salary = (decimal?)null });
        preGrantCreate.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act — admin revokes demo.create then re-grants it; this proves that
        // a new grant produces a version bump and the next call sees the new state.
        var adminClient = factory.CreateClient();
        var adminLoginResp = await adminClient.PostAsJsonAsync(
            "/auth/login", new { userCode = "admin", password = "111111" });
        var adminAuth = await adminLoginResp.Content.ReadFromJsonAsync<AuthResponseDto>();
        adminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminAuth!.AccessToken);

        await adminClient.DeleteAsync("/admin/roles/editor/permissions/demo.create");
        var revokeResp = await aliceClient.PostAsJsonAsync(
            "/demo", new { name = "should-deny", orgId = (string?)null, salary = (decimal?)null });
        revokeResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await adminClient.PostAsync(
            "/admin/roles/editor/permissions/demo.create", content: null);

        // Assert — alice re-uses her old token, the re-grant takes effect immediately
        var postGrantCreate = await aliceClient.PostAsJsonAsync(
            "/demo", new { name = "after-grant", orgId = (string?)null, salary = (decimal?)null });
        postGrantCreate.StatusCode.Should().Be(HttpStatusCode.OK,
            "the version bump from the new grant must let the next request through, even with the stale token");
    }

    // ── Per-test isolated host ─────────────────────────────────

    /// <summary>
    /// WAF that swaps the registered <c>IDbContextFactory&lt;DemoDbContext&gt;</c>
    /// for a per-instance in-memory store. The factory hands out DemoDbContext
    /// instances built against a unique database name, so the seeders and
    /// permission cache don't leak between tests.
    /// </summary>
    public sealed class IsolatedFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"role-revoke-{Guid.NewGuid():N}";
        private readonly string[] _aliceRoles;

        /// <summary>
        /// 构造时指定 alice 在该隔离实例里应持有的角色集合。
        /// #7 三个 E2E 测不同场景：
        /// - 测试 1+2：alice 仅 viewer（revoke viewer/permissions/demo.view 才能影响 alice）
        /// - 测试 3：alice 仅 editor（验证 editor role 的 grant/revoke 路径）
        /// </summary>
        public IsolatedFactory() : this(new[] { "viewer", "editor" }) { }

        public IsolatedFactory(string[] aliceRoles)
        {
            _aliceRoles = aliceRoles;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                // Remove the existing IDbContextFactory<DemoDbContext> registration
                var existing = services
                    .Where(d => d.ServiceType == typeof(IDbContextFactory<DemoDbContext>))
                    .ToList();
                foreach (var d in existing) services.Remove(d);

                // Re-register against a unique in-memory store, ignoring the
                // InMemory transaction warning (transactions don't apply to
                // in-memory providers but EF logs a warning anyway).
                var dbName = _dbName;
                services.AddDbContextFactory<DemoDbContext>(opt =>
                    opt.UseInMemoryDatabase(dbName)
                       .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            });
        }

        /// <summary>
        /// 触发 host 启动（让 seeders 跑）然后按本 factory 指定的角色重置 alice 的 TenE0UserRole。
        /// </summary>
        public async Task ResetAliceRolesAsync()
        {
            // 触发 server 启动
            using var client = CreateClient();
            using var resp = await client.GetAsync("/");
            // 把 alice 角色重置为 _aliceRoles
            using var scope = Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync();
            var existing = ctx.UserRoles.Where(ur => ur.UserCode == "alice");
            ctx.UserRoles.RemoveRange(existing);
            foreach (var r in _aliceRoles)
                ctx.UserRoles.Add(new TenE0UserRole { UserCode = "alice", RoleCode = r });
            await ctx.SaveChangesAsync();
        }
    }

    // ── Minimal DTOs for HttpClient JSON parsing ──────────────

    private sealed record AuthResponseDto(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt,
        string UserCode,
        string DisplayName,
        IReadOnlyList<string> Roles);
}
