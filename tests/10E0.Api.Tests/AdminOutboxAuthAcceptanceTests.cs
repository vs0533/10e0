using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Auth.Jwt.Storage;

namespace TenE0.Api.Tests;

/// <summary>
/// BDD acceptance tests for issue #119 — <c>GET /admin/outbox</c> lacks an
/// authorization check, so any authenticated user (e.g. alice with only
/// <c>viewer</c> + <c>editor</c> roles) can pull the last 20 outbox messages
/// including the raw <c>Payload</c> JSON. <c>Payload</c> may contain PII
/// (e.g. order details, customer fields) — this is a P2 information-disclosure
/// regression.
///
/// Issue #119's hard rule: any <c>/admin/*</c> endpoint that touches
/// sensitive tables MUST be gated by <c>[RequireAdmin]</c> (or an equivalent
/// admin-only policy) just like the sibling <c>/admin/permissions</c> routes.
/// Once the fix lands, a plain-user token must be rejected with HTTP 403.
///
/// Each scenario encodes a Given/When/Then business behavior. Today the
/// endpoint is unguarded, so a non-admin token gets HTTP 200 + the
/// outbox payload — these tests fail RED until the <c>[RequireAdmin]</c>
/// (or equivalent) attribute is added to the endpoint mapping.
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
public sealed class AdminOutboxAuthAcceptanceTests
{
    // ── 403 for an ordinary user ───────────────────────────────

    [Fact]
    public async Task GivenOrdinaryUserToken_WhenGettingAdminOutbox_ThenResponseIs403()
    {
        // Arrange — alice 默认只有 viewer + editor（见 AuthSeeder），不持有 perm.admin。
        // 修复前 /admin/outbox 无任何授权注解 → 200 + Outbox 列表。
        // 修复后（[RequireAdmin] / Authorize(Policy="perm.admin")）必须 403。
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "111111");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        // Act — 任何登录用户都能拉到 Outbox Payload 是 issue #119 的核心漏洞
        var response = await client.GetAsync("/admin/outbox");

        // Assert — 普通用户必须被拦截
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "alice holds only viewer/editor roles (no perm.admin) and must be " +
            "denied by the same authorization gate that protects /admin/permissions");
    }

    [Fact]
    public async Task GivenOrdinaryUserTokenAndExistingOutboxRow_WhenGettingAdminOutbox_ThenResponseBodyDoesNotLeakOutboxPayload()
    {
        // Arrange — 重点断言：响应 body 不能包含任何 outbox 行（含 LastError 之类的
        // 间接泄露也算）。修复后由 Authorize middleware 在 401/403 路径提前拦截，
        // 业务代码根本不会跑 → 列表为空。
        // 这里手工往 Outbox 表塞一行带敏感 marker 的数据：未修复时 GET 会原样返回
        // 包含 "PII-SECRET" 的 LastError，修复后被 401/403 提前拦截，body 不含 marker。
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "111111");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        // 注入一行带 PII marker 的 outbox 记录，模拟生产环境的真实数据
        await factory.SeedOutboxRowAsync(
            eventType: "TenE0.Api.Events.DemoCreatedEvent,10E0.Api",
            payload: "{\"pii-marker\":\"PII-SECRET-customer-ssn-12345\"}",
            lastError: "PII-SECRET-last-error-stack");

        // Act
        var response = await client.GetAsync("/admin/outbox");
        var raw = await response.Content.ReadAsStringAsync();

        // Assert — outbox 行特征字段（Id / OccurredOn / LastError）不能出现在 body 里
        raw.Should().NotContain("PII-SECRET-customer-ssn-12345",
            "an unauthorized caller must never see the outbox Payload field — " +
            "Payload may carry PII and is exactly the leak issue #119 reported");
        raw.Should().NotContain("PII-SECRET-last-error-stack",
            "an unauthorized caller must never see the outbox LastError column");
        raw.Should().NotContain("OccurredOn",
            "an unauthorized caller must never see the outbox OccurredOn column");
        raw.Should().NotContain("AttemptCount",
            "an unauthorized caller must never see the outbox AttemptCount column");
    }

    [Fact]
    public async Task GivenOrdinaryUserToken_WhenGettingAdminOutbox_ThenResponseBodyFollowsApiResultShape()
    {
        // Arrange — 与 #39 统一：所有 4xx 响应都走 ApiResult<T> 信封（success=false,
        // errorCode 稳定），客户端能用同一个 DTO 反序列化。
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "111111");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        // Act
        var response = await client.GetAsync("/admin/outbox");

        // Assert — 状态码 + 媒体类型
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an unauthorized caller must be denied with HTTP 403, not 200 or 401");
        response.Content.Headers.ContentType.Should().NotBeNull(
            "every error response must declare a content type");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json",
            "the 403 body must be JSON, not plain text or empty");

        // Assert — ApiResult<T> 信封存在
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotBeNullOrWhiteSpace("the 403 body must carry a JSON payload");
        var json = JsonDocument.Parse(raw).RootElement;
        json.TryGetProperty("success", out var success).Should().BeTrue(
            "the 403 body must expose the ApiResult `success` field for clients to branch on");
        success.GetBoolean().Should().BeFalse(
            "a 403 response is a failure, so success must be false");
    }

    // ── 401 for an unauthenticated caller ──────────────────────

    [Fact]
    public async Task GivenUnauthenticatedCaller_WhenGettingAdminOutbox_ThenResponseIs401Or403()
    {
        // Arrange — 没带 token。修复后端点带授权注解，未认证访问必须被拒（401 或 403
        // 取决于 Authorize middleware 是否在认证失败时短路；两种行为都接受）。
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/admin/outbox");

        // Assert
        (response.StatusCode == HttpStatusCode.Unauthorized
         || response.StatusCode == HttpStatusCode.Forbidden)
            .Should().BeTrue(
                "an unauthenticated caller must be rejected with 401 or 403, " +
                $"never 200 (actual: {(int)response.StatusCode})");
    }

    // ── Source-level regression: endpoint must declare the admin gate ──

    [Fact]
    public void GivenIssue119HardRule_WhenScanningAdminEndpoints_ThenOutboxRouteIsGuarded()
    {
        // Arrange — 文本扫描 `MapGet("/admin/outbox"`) 周围是否出现 [RequireAdmin] /
        // Authorize(Policy="perm.admin") / 任何 Authorize 派生属性。
        // 这是源码层兜底：万一有人回退了 endpoint 的 [RequireAdmin] 注解，
        // 这条断言会立刻 fail，即便运行时测试在某种诡异 mock 环境下被绕过。
        var path = Path.Combine("src", "10E0.Api", "Endpoints", "AdminEndpoints.cs");
        var absolutePath = Path.Combine(FindRepoRoot(), path);
        File.Exists(absolutePath).Should().BeTrue(
            $"endpoint source file `{path}` must exist for the scan");

        var content = File.ReadAllText(absolutePath);
        // 抽出 /admin/outbox 路由声明所在行起的 200 字符窗口
        var marker = "MapGet(\"/admin/outbox\"";
        var idx = content.IndexOf(marker, StringComparison.Ordinal);
        idx.Should().BeGreaterThanOrEqualTo(0,
            "the /admin/outbox route declaration must exist in AdminEndpoints.cs");

        // 取前 1 行（注解一般写在 MapGet 同一行的左侧 / 上方）的窗口
        var lineStart = content.LastIndexOf('\n', idx) + 1;
        // 同行往后取到行尾 + 上一行（覆盖 [RequireAdmin] 写在 MapGet 上方一行的常见写法）
        var lineEnd = content.IndexOf('\n', idx);
        if (lineEnd < 0) lineEnd = content.Length;
        // 看 MapGet 同一行
        var sameLine = content[lineStart..lineEnd];
        // 看 MapGet 上一行（注解贴在 lambda 上方）
        var prevLineStart = lineStart > 0
            ? content.LastIndexOf('\n', lineStart - 2) + 1
            : 0;
        var prevLineEnd = lineStart - 1; // 跳过 \n
        var prevLine = content[prevLineStart..prevLineEnd];

        var window = sameLine + " || " + prevLine;
        var guarded = window.Contains("RequireAdmin", StringComparison.Ordinal)
                      || window.Contains("Authorize", StringComparison.Ordinal);

        guarded.Should().BeTrue(
            "/admin/outbox must be guarded by [RequireAdmin] or [Authorize(Policy=\"perm.admin\")] " +
            "— the same authorization gate that protects sibling routes like /admin/permissions. " +
            "Today the endpoint is unguarded, which is exactly the leak issue #119 reported.");
    }

    // ── Helpers ────────────────────────────────────────────────

    /// <summary>Logs in as a seeded user and returns the auth payload.</summary>
    private static async Task<AuthResponseDto> LoginAsAsync(
        HttpClient client, string userCode, string password)
    {
        var resp = await client.PostAsJsonAsync(
            "/auth/login", new { userCode, password });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"{userCode} is seeded in AuthSeeder with password {password}");
        var env = await resp.Content.ReadFromJsonAsync<LoginEnvelope>();
        env.Should().NotBeNull();
        env!.Success.Should().BeTrue("the success body must carry success = true");
        env.Data.Should().NotBeNull("the success body must carry the data envelope");
        env.Data!.AccessToken.Should().NotBeNullOrWhiteSpace();
        return env.Data;
    }

    private static string FindRepoRoot()
    {
        // tests/10E0.Api.Tests/bin/Debug/net10.0/<this.dll> → repo root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "10e0.slnx")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("must be able to locate the repository root via 10e0.slnx");
        return dir!.FullName;
    }

    /// <summary>
    /// Per-test isolated host — mirrors RoleRevocationEndToEndAcceptanceTests.
    /// </summary>
    public sealed class IsolatedFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"issue119-{Guid.NewGuid():N}";
        private readonly string[] _aliceRoles;

        public IsolatedFactory() : this(new[] { "viewer", "editor" }) { }

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
                ctx.UserRoles.Add(new TenE0UserRole
                {
                    UserCode = "alice",
                    RoleCode = r
                });
            await ctx.SaveChangesAsync();
        }

        /// <summary>
        /// 注入一行带敏感 marker 的 OutboxMessage，模拟生产环境的真实数据。
        /// 用于 issue #119 的 Payload 泄露回归测试 —— 未修复时 GET /admin/outbox
        /// 会原样回显该行的 Payload / LastError，修复后由 401/403 拦截，body 干净。
        /// </summary>
        public async Task SeedOutboxRowAsync(
            string eventType, string payload, string? lastError)
        {
            using var scope = Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync();
            ctx.Set<TenE0.Core.Events.Outbox.OutboxMessage>().Add(new()
            {
                EventType = eventType,
                Payload = payload,
                OccurredOn = DateTimeOffset.UtcNow,
                AttemptCount = 0,
                LastError = lastError,
            });
            await ctx.SaveChangesAsync();
        }
    }

    // ── Wire DTOs (lenient deserialization — only the fields we assert) ──

    private sealed record AuthResponseDto(string AccessToken, string RefreshToken);

    /// <summary>#50: /auth/login success body 是 ApiResult&lt;T&gt; 信封。</summary>
    private sealed record LoginEnvelope(bool Success, AuthResponseDto? Data);
}
