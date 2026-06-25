using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace TenE0.Api.Tests;

/// <summary>
/// 实时推送 Hub 集成测试（#155）。
///
/// 端到端验证：
/// - 无 token 连接被拒（401）
/// - 带 JWT token 经 query string 连接成功
/// - Clients.User(code) 定向推送可达
/// - org claim 链路端到端通：Clients.Group("org:{nodeId}") 推送可达（验证 org claim 写入+派生组）
///
/// 连接方式：WebApplicationFactory 的内存测试服务器与 HubConnection 共享 BaseAddress，
/// token 经 ?access_token= 传（浏览器无法在 WS 握手设 Authorization 头）。
/// </summary>
public sealed class RealtimeHubAcceptanceTests
{
    private const string DefaultPassword = "dev-default-password-change-me";

    [Fact]
    public async Task HubConnection_WithoutToken_IsRejectedUnauthorized()
    {
        using var factory = new IsolatedFactory();

        // 不带 access_token 连接 —— 应被 JwtBearer 拒绝
        var conn = BuildHubConnection(factory, accessToken: null);

        var act = async () => await conn.StartAsync();

        await act.Should().ThrowAsync<HttpRequestException>(
            "无 token 的 WebSocket 握手应被 JwtBearer 拒（401）");
    }

    [Fact]
    public async Task HubConnection_WithToken_ConnectsAndReceivesUserTargetedPush()
    {
        using var factory = new IsolatedFactory();
        using var client = factory.CreateClient();
        var auth = await LoginAsAsync(client, "alice", DefaultPassword);

        var conn = BuildHubConnection(factory, auth.AccessToken);
        string? received = null;
        conn.On<object>("order.approved", _ => received = "ok");
        await conn.StartAsync();

        // 用服务端 IRealtimeNotifier 推给 alice
        await PushFromServerAsync(factory, notifier =>
            notifier.NotifyUserAsync("alice", "order.approved", new { OrderId = 7 }));

        await Task.Delay(500); // 推送是异步，等一拍
        received.Should().Be("ok", "alice 应收到定向推送");
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task HubConnection_OrgGroup_PushReachesUser_BoundToThatOrg()
    {
        // 验证 org claim 链路端到端：alice 登录 → token 带 org claim(BJ 节点 Id)
        // → 连接加入 org:{bjId} 组 → 推 Clients.Group("org:{bjId}") 可达。
        using var factory = new IsolatedFactory();
        using var client = factory.CreateClient();
        var auth = await LoginAsAsync(client, "alice", DefaultPassword);

        // 回查 alice 的 org 节点 Id（AuthSeeder 把 alice 绑到 BJ）
        var bjOrgId = await GetOrgNodeIdAsync(factory, "BJ");

        var conn = BuildHubConnection(factory, auth.AccessToken);
        string? received = null;
        conn.On<object>("org.broadcast", _ => received = "ok");
        await conn.StartAsync();

        // 推给 org:{bjOrgId} 组 —— alice 应在组内
        await PushFromServerAsync(factory, notifier =>
            notifier.NotifyGroupAsync($"org:{bjOrgId}", "org.broadcast", new { Msg = "hi" }));

        await Task.Delay(500);
        received.Should().Be("ok",
            "alice 归属 BJ 组织 → 连接加入 org:{bjId} 组 → 组广播应送达（org claim 链路端到端通）");
        await conn.DisposeAsync();
    }

    // ── Helpers ────────────────────────────────────────────────

    private static HubConnection BuildHubConnection(IsolatedFactory factory, string? accessToken)
    {
        var hubUrl = $"{factory.Server.BaseAddress}hub/notification";
        if (accessToken is not null)
            hubUrl += $"?access_token={Uri.EscapeDataString(accessToken)}";

        // TestServer 不能做真实 WebSocket 升级 —— 显式用 LongPolling 传输（TestServer 内存测试标准做法）。
        // 用 TestServer 的内存 handler 让 SignalR 客户端连到 WebApplicationFactory 的内存服务器。
        var serverHandler = factory.Server.CreateHandler();

        return new HubConnectionBuilder()
            .WithUrl(hubUrl, opts =>
            {
                opts.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                opts.HttpMessageHandlerFactory = _ => serverHandler;
            })
            .Build();
    }

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

    private static async Task PushFromServerAsync(
        IsolatedFactory factory, Func<TenE0.Core.Realtime.IRealtimeNotifier, Task> push)
    {
        using var scope = factory.Services.CreateAsyncScope();
        var notifier = scope.ServiceProvider
            .GetRequiredService<TenE0.Core.Realtime.IRealtimeNotifier>();
        await push(notifier);
    }

    private static async Task<string> GetOrgNodeIdAsync(IsolatedFactory factory, string code)
    {
        using var scope = factory.Services.CreateAsyncScope();
        var dcFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<DemoDbContext>>();
        await using var dc = await dcFactory.CreateDbContextAsync();
        var id = await dc.Orgs.Where(o => o.Code == code).Select(o => o.Id).FirstOrDefaultAsync();
        id.Should().NotBeNullOrEmpty($"组织 {code} 应由 AuthSeeder 播种");
        return id!;
    }

    // ── Wire DTOs ──

    private sealed record AuthResponseDto(string AccessToken, string RefreshToken);
    private sealed record LoginEnvelope(bool Success, AuthResponseDto? Data);

    /// <summary>Per-test isolated host —— 镜像 AdminOutboxAuthAcceptanceTests。</summary>
    public sealed class IsolatedFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"realtime-{Guid.NewGuid():N}";

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
    }

    [Fact]
    public async Task Negotiate_WithTokenFromQuery_PassesAuth()
    {
        // 验证 query-string token 提取生效：浏览器无法在 WS 握手设 Authorization 头，
        // 必须从 ?access_token= 取。直接 POST negotiate 断言非 401。
        using var factory = new IsolatedFactory();
        using var client = factory.CreateClient();
        var auth = await LoginAsAsync(client, "alice", DefaultPassword);

        var resp = await client.PostAsync(
            $"hub/notification/negotiate?access_token={Uri.EscapeDataString(auth.AccessToken)}", null);

        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            $"带 ?access_token= 的 negotiate 应通过认证（实际 {resp.StatusCode}）—— query-string 提取必须生效");
    }

    [Fact]
    public async Task Negotiate_WithAuthorizationHeader_PassesAuth()
    {
        // 对照组：用 Authorization 头（标准路径）验证 token 本身有效，隔离 query-string 提取问题
        using var factory = new IsolatedFactory();
        using var client = factory.CreateClient();
        var auth = await LoginAsAsync(client, "alice", DefaultPassword);

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var resp = await client.PostAsync("hub/notification/negotiate", null);

        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            $"带 Authorization 头的 negotiate 应通过认证（实际 {resp.StatusCode}）—— 对照组确认 token 本身有效");
    }
}
