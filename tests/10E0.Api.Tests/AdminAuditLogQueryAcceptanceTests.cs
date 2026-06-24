using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Auditing;

namespace TenE0.Api.Tests;

/// <summary>
/// BDD acceptance tests for issue #152 — the new <c>/admin/audit-logs</c> and
/// <c>/admin/login-logs</c> query endpoints must be gated by <c>[RequireAdmin]</c>,
/// the same authorization gate that protects sibling routes like
/// <c>/admin/outbox</c> (issue #119). A plain-user token (alice with only
/// viewer/editor roles) must be rejected with HTTP 403.
///
/// Also covers a happy path: a seeded audit log row is returned to an admin,
/// and a source-level scan guards against regressions if someone strips the
/// <c>[RequireAdmin]</c> attribute.
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
public sealed class AdminAuditLogQueryAcceptanceTests
{
    // ── 403 for an ordinary user (audit-logs) ──────────────────

    [Fact]
    public async Task GivenOrdinaryUserToken_WhenGettingAdminAuditLogs_ThenResponseIs403()
    {
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        var response = await client.GetAsync("/admin/audit-logs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "alice holds only viewer/editor roles (no perm.admin) and must be denied " +
            "by the same authorization gate that protects /admin/outbox");
    }

    [Fact]
    public async Task GivenOrdinaryUserToken_WhenGettingAdminLoginLogs_ThenResponseIs403()
    {
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        var response = await client.GetAsync("/admin/login-logs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "alice holds only viewer/editor roles (no perm.admin) and must be denied");
    }

    // ── 401 for an unauthenticated caller ──────────────────────

    [Fact]
    public async Task GivenUnauthenticatedCaller_WhenGettingAdminAuditLogs_ThenResponseIs401Or403()
    {
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/audit-logs");

        (response.StatusCode == HttpStatusCode.Unauthorized
         || response.StatusCode == HttpStatusCode.Forbidden)
            .Should().BeTrue(
                "an unauthenticated caller must be rejected with 401 or 403, " +
                $"never 200 (actual: {(int)response.StatusCode})");
    }

    // ── happy path: admin sees seeded audit row ────────────────

    [Fact]
    public async Task GivenAdminTokenAndSeededAuditRow_WhenGettingAdminAuditLogs_ThenRowReturned()
    {
        using var factory = new IsolatedFactory();
        await factory.SeedAuditRowAsync("alice", "Order", "1", "Create");
        var client = factory.CreateClient();
        // admin 是 AuthSeeder 预置的 super_admin 账号，登录即持 perm.admin bypass
        var adminAuth = await LoginAsAsync(client, "admin", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminAuth.AccessToken);

        var response = await client.GetAsync("/admin/audit-logs");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "super_admin bypasses authorization");

        var result = await response.Content.ReadFromJsonAsync<PagedEnvelope>();
        result.Should().NotBeNull();
        result!.Total.Should().BeGreaterThanOrEqualTo(1);
        result.Items.Should().Contain(i => i.EntityId == "1" && i.Action == "Create");
    }

    // ── login flow writes a login log (integration of the埋点) ──

    [Fact]
    public async Task GivenSuccessfulLogin_WhenGettingAdminLoginLogs_ThenLoginEventRecorded()
    {
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        // 触发一次成功登录 → LoginCommandHandler 应写一条 LoginLog
        await LoginAsAsync(client, "alice", "dev-default-password-change-me");

        // 等待后台 AuditLogRelayWorker 落库（异步 Channel，需要短暂等待）
        await Task.Delay(500);

        var adminAuth = await LoginAsAsync(client, "admin", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminAuth.AccessToken);
        var response = await client.GetAsync("/admin/login-logs");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<LoginPagedEnvelope>();
        result.Should().NotBeNull();
        result!.Items.Should().Contain(i => i.UserCode == "alice" && i.EventType == "Login" && i.Success);
    }

    // ── Source-level regression: endpoints must declare the admin gate ──

    [Fact]
    public void GivenIssue152HardRule_WhenScanningAdminEndpoints_ThenAuditRoutesAreGuarded()
    {
        var path = Path.Combine("src", "10E0.Api", "Endpoints", "AdminEndpoints.cs");
        var absolutePath = Path.Combine(FindRepoRoot(), path);
        File.Exists(absolutePath).Should().BeTrue(
            $"endpoint source file `{path}` must exist for the scan");

        var content = File.ReadAllText(absolutePath);

        AssertRouteGuarded(content, "MapGet(\"/admin/audit-logs\"");
        AssertRouteGuarded(content, "MapGet(\"/admin/login-logs\"");
    }

    private static void AssertRouteGuarded(string content, string marker)
    {
        var idx = content.IndexOf(marker, StringComparison.Ordinal);
        idx.Should().BeGreaterThanOrEqualTo(0, $"the route `{marker}` must exist");

        // 取 MapGet 同一行 + 上一行（覆盖 [RequireAdmin] 贴在 lambda 上方的写法）
        var lineStart = content.LastIndexOf('\n', idx) + 1;
        var lineEnd = content.IndexOf('\n', idx);
        if (lineEnd < 0) lineEnd = content.Length;
        var sameLine = content[lineStart..lineEnd];
        var prevLineStart = lineStart > 0
            ? content.LastIndexOf('\n', lineStart - 2) + 1
            : 0;
        var prevLine = content[prevLineStart..(lineStart - 1)];

        var window = sameLine + " || " + prevLine;
        var guarded = window.Contains("RequireAdmin", StringComparison.Ordinal)
                      || window.Contains("Authorize", StringComparison.Ordinal);
        guarded.Should().BeTrue(
            $"{marker} must be guarded by [RequireAdmin] or [Authorize(...)] " +
            "— the same authorization gate that protects sibling /admin/* routes.");
    }

    // ── Helpers ────────────────────────────────────────────────

    private static async Task<AuthResponseDto> LoginAsAsync(
        HttpClient client, string userCode, string password)
    {
        var resp = await client.PostAsJsonAsync("/auth/login", new { userCode, password });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"{userCode} is seeded in AuthSeeder with password {password}");
        var env = await resp.Content.ReadFromJsonAsync<LoginEnvelope>();
        env.Should().NotBeNull();
        env!.Success.Should().BeTrue();
        env.Data.Should().NotBeNull();
        env.Data!.AccessToken.Should().NotBeNullOrWhiteSpace();
        return env.Data;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "10e0.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("must be able to locate the repository root via 10e0.slnx");
        return dir!.FullName;
    }

    /// <summary>Per-test isolated host — mirrors AdminOutboxAuthAcceptanceTests.</summary>
    public sealed class IsolatedFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"issue152-{Guid.NewGuid():N}";
        private readonly string[] _aliceRoles;

        public IsolatedFactory() : this(["viewer", "editor"]) { }
        public IsolatedFactory(string[] aliceRoles) => _aliceRoles = aliceRoles;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IDbContextFactory<DemoDbContext>))
                    .ToList();
                foreach (var d in existing) services.Remove(d);

                var dbName = _dbName;
                services.AddDbContextFactory<DemoDbContext>(opt =>
                    opt.UseInMemoryDatabase(dbName)
                       .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            });
        }

        public async Task ResetAliceRolesAsync()
        {
            using var client = CreateClient();
            using var resp = await client.GetAsync("/");
            using var scope = Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync();
            var existing = ctx.UserRoles.Where(ur => ur.UserCode == "alice");
            ctx.UserRoles.RemoveRange(existing);
            foreach (var r in _aliceRoles)
                ctx.UserRoles.Add(new TenE0.Core.Auth.Jwt.Storage.TenE0UserRole
                {
                    UserCode = "alice",
                    RoleCode = r
                });
            await ctx.SaveChangesAsync();
        }

        public async Task SeedAuditRowAsync(
            string actor, string entityType, string entityId, string action)
        {
            using var scope = Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync();
            ctx.Set<TenE0AuditLog>().Add(new TenE0AuditLog
            {
                ActorCode = actor,
                EntityType = entityType,
                EntityId = entityId,
                Action = action,
                ChangedFieldsJson = "[]",
                CreateTime = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
    }

    // ── Wire DTOs (lenient deserialization) ──

    private sealed record AuthResponseDto(string AccessToken, string RefreshToken);
    private sealed record LoginEnvelope(bool Success, AuthResponseDto? Data);

    private sealed record AuditLogItemDto(
        string Id, string? ActorCode, string EntityType, string EntityId, string Action);
    private sealed record PagedEnvelope(long Total, int Page, int Size, List<AuditLogItemDto> Items);

    private sealed record LoginLogItemDto(
        string UserCode, string EventType, bool Success);
    private sealed record LoginPagedEnvelope(long Total, int Page, int Size, List<LoginLogItemDto> Items);
}
