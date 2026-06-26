using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Auth.Jwt.Storage;

namespace TenE0.Api.Tests;

/// <summary>
/// #161 可观测性端到端验收：
/// - <c>/health/live</c> 匿名恒 200；<c>/health/ready</c> 健康时 200。
/// - <c>/health</c> 完整报告 + <c>/metrics</c> Prometheus 抓取端点需 perm.admin（无 token 401 / 非 admin 403 / admin 200）。
/// - 触发一次 CQRS 命令（登录）后 <c>/metrics</c> 含 <c>tene0_command_total</c>。
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
public sealed class ObservabilityAcceptanceTests
{
    [Fact]
    public async Task HealthLive_Anonymous_AlwaysReturns200()
    {
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health/live");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "/health/live 是 K8s liveness 探针，不跑任何依赖检查，只要进程在就 200（且匿名）");
    }

    [Fact]
    public async Task HealthReady_Anonymous_Returns200WhenHealthy()
    {
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health/ready");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "/health/ready 跑 ready 标签的检查（DB/Outbox/Files）；InMemory 健康时应 200（且匿名，K8s readiness 探针不认证）");
    }

    [Fact]
    public async Task HealthFull_WithoutToken_Returns401()
    {
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "/health 完整报告含每项 check 详情/积压数，需 perm.admin；无 token 必须 401");
    }

    [Fact]
    public async Task HealthFull_NonAdminToken_Returns403()
    {
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var alice = await LoginAsAsync(client, "alice", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "alice 仅持 viewer/editor，无 perm.admin，必须 403");
    }

    [Fact]
    public async Task Metrics_WithoutToken_Returns401()
    {
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/metrics");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "/metrics 含内部指标，需 perm.admin；无 token 必须 401");
    }

    [Fact]
    public async Task Metrics_AdminToken_ReturnsPrometheusTextWithCommandTotal()
    {
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        // 触发一次 CQRS 命令：admin 登录（LoginCommandHandler 经 CommandDispatcher 分发 → 埋点）。
        await LoginAsAsync(client, "admin", "dev-default-password-change-me");

        // 用 admin token 抓取 /metrics。
        var adminAuth = await LoginAsAsync(client, "admin", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization = new("Bearer", adminAuth.AccessToken);
        var resp = await client.GetAsync("/metrics");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await resp.Content.ReadAsStringAsync();
        text.Should().Contain("tene0_command_total",
            "CQRS 命令埋点经 CommandDispatcher 写入 tene0.command.total，Prometheus 命名为 tene0_command_total");
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
        env.Data!.AccessToken.Should().NotBeNullOrWhiteSpace();
        return env.Data;
    }

    private sealed record AuthResponseDto(string AccessToken, string RefreshToken);
    private sealed record LoginEnvelope(bool Success, AuthResponseDto? Data);

    /// <summary>Per-test 隔离主机 —— 用唯一 InMemory DB，环境 Test（载入 appsettings.json 的 seed 密钥）。</summary>
    public sealed class IsolatedFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"obs161-{Guid.NewGuid():N}";

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
            using var _ = await client.GetAsync("/");
            using var scope = Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync();
            var existing = ctx.UserRoles.Where(ur => ur.UserCode == "alice");
            ctx.UserRoles.RemoveRange(existing);
            foreach (var r in new[] { "viewer", "editor" })
                ctx.UserRoles.Add(new TenE0UserRole { UserCode = "alice", RoleCode = r });
            await ctx.SaveChangesAsync();
        }
    }
}
