using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace TenE0.Api.Tests;

/// <summary>
/// Configuration 模块（#153）端点验收测试。
///
/// <para>覆盖：</para>
/// <list type="bullet">
///   <item>公开只读 <c>/dict/{code}</c>：任何已登录用户可读（alice 200）。</item>
///   <item>Admin 写入 <c>/admin/dict-types</c>：非 admin（alice）403、未认证 401/403、super_admin 200。</item>
///   <item>Seeder 落数：<c>/dict/gender</c> 返回 3 项。</item>
///   <item>系统参数改值：<c>PUT /admin/system-parameters/{key}</c> 只读参数被拒。</item>
/// </list>
/// 镜像 <c>AdminOutboxAuthAcceptanceTests</c> 的 <c>IsolatedFactory</c> 范式。
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
public sealed class ConfigurationEndpointsAcceptanceTests
{
    // ── 公开只读：任何已登录用户可读 ──────────────────────────

    [Fact]
    public async Task GivenOrdinaryUserToken_WhenGettingPublicDict_ThenResponseIs200()
    {
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        var response = await client.GetAsync("/dict/gender");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "alice 已登录，/dict/{code} 是公开只读端点，任何已登录用户可读");
    }

    [Fact]
    public async Task GivenSeededData_WhenGettingGenderDict_ThenReturnsThreeItems()
    {
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        var response = await client.GetAsync("/dict/gender");
        var items = await response.Content.ReadFromJsonAsync<List<DictItemWire>>();

        items.Should().NotBeNull();
        items!.Select(i => i.Value).Should().BeEquivalentTo(["M", "F", "U"],
            "ConfigurationSeeder 种了 gender 三项（男/女/未知）");
    }

    // ── Admin 写入：权限闸门 ─────────────────────────────────

    [Fact]
    public async Task GivenOrdinaryUserToken_WhenPostingAdminDictType_ThenResponseIs403()
    {
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        var response = await client.PostAsJsonAsync("/admin/dict-types", new
        {
            code = "forbidden",
            name = "测试",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "alice 仅持 viewer/editor（无 perm.admin），写入必须被 [RequireAdmin] 拦截");
    }

    [Fact]
    public async Task GivenUnauthenticatedCaller_WhenPostingAdminDictType_ThenResponseIs401Or403()
    {
        using var factory = new IsolatedFactory();
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/admin/dict-types", new
        {
            code = "forbidden",
            name = "测试",
        });

        (response.StatusCode == HttpStatusCode.Unauthorized
         || response.StatusCode == HttpStatusCode.Forbidden).Should().BeTrue(
            "未认证调用 admin 写入端点必须 401/403，绝不 200");
    }

    [Fact]
    public async Task GivenSuperAdminToken_WhenPostingAdminDictType_ThenResponseIs200()
    {
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();
        var adminAuth = await LoginAsAsync(client, "admin", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminAuth.AccessToken);

        var response = await client.PostAsJsonAsync("/admin/dict-types", new
        {
            code = "priority",
            name = "优先级",
            isEnabled = true,
            sortOrder = 99,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "admin 持 super_admin，perm.admin 放行写入");
    }

    // ── 系统参数：只读参数拒绝修改 ───────────────────────────

    [Fact]
    public async Task GivenSuperAdminToken_WhenUpdatingReadOnlyParameter_ThenResponseIs500OrRejected()
    {
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();
        var adminAuth = await LoginAsAsync(client, "admin", "dev-default-password-change-me");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminAuth.AccessToken);

        // system.installation_id 由 SystemParameterDefinitions 声明为 IsReadOnly=true
        var response = await client.PutAsJsonAsync(
            "/admin/system-parameters/system.installation_id",
            new { value = "tampered" });

        // IsReadOnly 抛 InvalidOperationException → 集中异常映射为 500（或框架映射的其它非 2xx）
        response.IsSuccessStatusCode.Should().BeFalse(
            "只读系统参数必须拒绝运行时修改");
    }

    // ── Helpers ───────────────────────────────────────────────

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
    private sealed record DictItemWire(string Label, string Value);

    /// <summary>Per-test isolated host — 镜像 AdminOutboxAuthAcceptanceTests.IsolatedFactory。</summary>
    public sealed class IsolatedFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"issue153-{Guid.NewGuid():N}";
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
            // 触发 DatabaseInitializerService 完成 Seeder 落数（含 ConfigurationSeeder）
            var existing = ctx.UserRoles.Where(ur => ur.UserCode == "alice");
            ctx.UserRoles.RemoveRange(existing);
            foreach (var r in _aliceRoles)
                ctx.UserRoles.Add(new TenE0.Core.Auth.Jwt.Storage.TenE0UserRole
                {
                    UserCode = "alice",
                    RoleCode = r,
                });
            await ctx.SaveChangesAsync();
        }
    }
}
