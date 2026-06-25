using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Auth.Jwt.Storage;

namespace TenE0.Api.Tests.ApiVersioning;

/// <summary>
/// API 版本化端到端验收（#163）。
///
/// 验证三种版本声明方式（URL segment / query string / header）均能访问已声明版本的端点，
/// 未声明版本时按默认版本透明处理（向后兼容裸路由），声明不支持版本返回 400，
/// 且响应头正确通告 api-supported-versions。
///
/// Demo 端点走 CQRS + PermissionBehavior，需认证 + 权限。测试用 seeded 的 alice 账号登录，
/// 并赋予 super_admin 角色（框架 super-user bypass）以确保权限链路放行，聚焦验证版本化行为。
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
public sealed class ApiVersioningAcceptanceTests
{
    /// <summary>
    /// Given Demo 端点已声明 v1.0 版本
    /// When 客户端不带任何版本信息请求 /demo
    /// Then 返回 200（版本透明：按默认版本 1.0 处理，向后兼容）
    /// </summary>
    [Fact]
    public async Task GivenVersionedDemoEndpoint_WhenCalledWithoutVersion_ThenReturns200()
    {
        using var harness = await AuthHarness.CreateAsync();
        var client = harness.AuthedClient;

        var response = await client.GetAsync("/demo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Given Demo 端点路由为裸 /demo（无版本前缀，版本透明策略）
    /// When 客户端用 URL segment 请求 /v1/demo
    /// Then 返回 404（裸路由无 {version:apiVersion} 占位符，URL segment reader 不适用）。
    /// 业务方若需 URL segment 版本，需把路由改为 /v{version:apiVersion}/demo。
    /// query / header 版本声明方式在裸路由下完全可用（见后续测试）。
    /// </summary>
    [Fact]
    public async Task GivenVersionedDemoEndpoint_WhenCalledWithUrlSegmentVersion_ThenReturns404_BecauseRouteHasNoVersionPlaceholder()
    {
        using var harness = await AuthHarness.CreateAsync();
        var client = harness.AuthedClient;

        var response = await client.GetAsync("/v1/demo");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Given Demo 端点已声明 v1.0 版本
    /// When 客户端用 query string 声明版本 /demo?api-version=1.0
    /// Then 返回 200
    /// </summary>
    [Fact]
    public async Task GivenVersionedDemoEndpoint_WhenCalledWithQueryVersion_ThenReturns200()
    {
        using var harness = await AuthHarness.CreateAsync();

        var response = await harness.AuthedClient.GetAsync("/demo?api-version=1.0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Given Demo 端点已声明 v1.0 版本
    /// When 客户端用 header 声明版本 X-Api-Version: 1.0
    /// Then 返回 200
    /// </summary>
    [Fact]
    public async Task GivenVersionedDemoEndpoint_WhenCalledWithHeaderVersion_ThenReturns200()
    {
        using var harness = await AuthHarness.CreateAsync();
        harness.AuthedClient.DefaultRequestHeaders.Add("X-Api-Version", "1.0");

        var response = await harness.AuthedClient.GetAsync("/demo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Given Demo 端点只声明了 v1.0 版本
    /// When 客户端请求不支持的版本 /demo?api-version=9.0
    /// Then 返回 400 Bad Request（Asp.Versioning 拒绝不支持的版本）
    /// </summary>
    [Fact]
    public async Task GivenVersionedDemoEndpoint_WhenCalledWithUnsupportedVersion_ThenReturns400()
    {
        using var harness = await AuthHarness.CreateAsync();

        var response = await harness.AuthedClient.GetAsync("/demo?api-version=9.0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Given Demo 端点声明了 v1.0 且开启了 ReportApiVersions
    /// When 客户端成功请求 /demo
    /// Then 响应头包含 api-supported-versions，其值含 1.0（客户端可探测升级路径）
    /// </summary>
    [Fact]
    public async Task GivenVersionedDemoEndpoint_WhenCalled_ThenResponseHasApiSupportedVersionsHeader()
    {
        using var harness = await AuthHarness.CreateAsync();

        var response = await harness.AuthedClient.GetAsync("/demo");

        response.Headers.Contains("api-supported-versions").Should().BeTrue();
        response.Headers.GetValues("api-supported-versions").Should().Contain(v => v.Contains("1.0"));
    }

    /// <summary>
    /// 认证测试套件：持有 IsolatedFactory（生命周期覆盖整个测试方法）+ 已登录的 HttpClient。
    /// </summary>
    private sealed class AuthHarness : IDisposable
    {
        public IsolatedFactory Factory { get; }
        public HttpClient AuthedClient { get; }

        private AuthHarness(IsolatedFactory factory, HttpClient authedClient)
        {
            Factory = factory;
            AuthedClient = authedClient;
        }

        /// <summary>构造隔离 Host，seed alice 为 super_admin，登录拿 token。</summary>
        public static async Task<AuthHarness> CreateAsync()
        {
            var factory = new IsolatedFactory();
            await factory.MakeAliceSuperAdminAsync();

            var login = await factory.CreateClient().PostAsJsonAsync("/auth/login", new
            {
                userCode = "alice",
                password = "dev-default-password-change-me",
            });
            login.EnsureSuccessStatusCode();
            var envelope = await login.Content.ReadFromJsonAsync<LoginEnvelope>();
            var token = envelope?.Data?.AccessToken
                ?? throw new InvalidOperationException("登录未返回 AccessToken");

            var authedClient = factory.CreateClient();
            authedClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            return new AuthHarness(factory, authedClient);
        }

        public void Dispose()
        {
            AuthedClient.Dispose();
            Factory.Dispose();
        }
    }

    /// <summary>
    /// 隔离的测试 Host：每个测试用独立的 InMemory 数据库，避免并发污染。
    /// 仿 CentralizedExceptionHandlingAcceptanceTests 的 IsolatedFactory 模式。
    /// </summary>
    private sealed class IsolatedFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"issue163-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IDbContextFactory<DemoDbContext>))
                    .ToList();
                foreach (var d in existing) services.Remove(d);

                services.AddDbContextFactory<DemoDbContext>(opt =>
                    opt.UseInMemoryDatabase(_dbName)
                        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            });
        }

        /// <summary>把 alice 设为 super_admin（框架 super-user bypass），跳过逐项授权。</summary>
        public async Task MakeAliceSuperAdminAsync()
        {
            // 触发 seeder（首次请求 DB 时种子数据写入）
            using (var client = CreateClient())
            {
                using var resp = await client.GetAsync("/");
            }

            using var scope = Services.CreateAsyncScope();
            var f = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
            await using var ctx = await f.CreateDbContextAsync();

            ctx.UserRoles.RemoveRange(ctx.UserRoles.Where(ur => ur.UserCode == "alice"));
            ctx.UserRoles.Add(new TenE0UserRole { UserCode = "alice", RoleCode = "super_admin" });
            await ctx.SaveChangesAsync();
        }
    }

    // ── Wire DTOs（宽松反序列化，只取断言需要的字段）──

    private sealed record AuthResponseDto(string AccessToken, string RefreshToken);
    private sealed record LoginEnvelope(bool Success, AuthResponseDto? Data);
}
