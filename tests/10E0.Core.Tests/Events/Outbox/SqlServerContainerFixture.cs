using Docker.DotNet;
using TenE0.Core.Events.Outbox;
using Testcontainers.MsSql;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// xUnit <see cref="IAsyncLifetime"/> 共享 fixture —— 启动一个 SQL Server 容器并暴露其
/// <see cref="DbConnection.ConnectionString"/>，供 Outbox 真实并发验收测试复用。
///
/// <para>
/// <b>为何用 fixture 而不是每测试启容器？</b>
/// <list type="bullet">
/// <item>SQL Server 镜像拉取 + 启动 ≈ 30s+：每测试启一次会让 50 条消息的真实并发测试套件膨胀到分钟级。</item>
/// <item>xUnit <c>[Collection]</c> + <see cref="IAsyncLifetime"/> 让多个测试类共享同一容器，CI 总成本可控。</item>
/// <item>Testcontainers 自动按 <c>AsyncLifetime</c> 释放容器，测试套件退出时统一停止 — 无资源泄漏。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>关于 IDistributedCache 在多 ServiceProvider 下的共享问题</b>：fixture 仅暴露 SQL Server 连接串。
/// 各测试方法自行 Build <c>IServiceCollection</c>，可以独立注册 <c>IDistributedCache</c>。
/// 本 fixture 不缓存 DbContextFactory：每次测试 fresh 建库 + EnsureCreatedAsync，避免 schema 跨测试污染。
/// </para>
/// </summary>
[Trait("Category", "Acceptance")]
[Trait("Requires", "Docker")]
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    /// <summary>
    /// 容器暴露的 SQL Server 连接串（<c>TrustServerCertificate=True</c> 已加）。
    /// 测试方法用它 <c>UseSqlServer(connectionString)</c> 构造 DbContextOptions。
    /// </summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        // 本地无 Docker（CI runner / 开发机没装）→ ConnectionString 留空 + IsDockerAvailable=false，
        // 各测试方法在 Arrange 阶段用 EnsureDockerAvailable() 抛 Skip 整体跳过。
        // xunit v2 没有 SkipException，这里用自定义测试异常 + 测试方法 try/catch 转 Assert.True(false, "skip")。
        // 但更简单：xunit v2 的 Skip 模式是抛任意异常后用 [Fact(Skip=...)] 或在测试方法里
        // 早返（基于 IsDockerAvailable() == false）。我们选"让 ConnectionString 留空"路径，
        // 测试方法自己 Assert.Skip-like 早返（见 OutboxRelayConcurrencyTests）。
        if (!TryResolveDockerEndpoint(out var endpoint))
        {
            // ConnectionString 保持空串；测试方法应在第一行检查并 Assert.Skip 风格早返。
            return;
        }

        // 关键：把探测到的 endpoint 注入 DOCKER_HOST，Testcontainers 内部 DockerClient 会读这个 env
        // （Testcontainers.MsSqlBuilder.Build() 用的 Docker.DotNet 客户端默认只看
        //  /var/run/docker.sock，OrbStack 走 /private/var/run/docker.sock → 内部客户端连不上）
        Environment.SetEnvironmentVariable("DOCKER_HOST", endpoint.ToString());

        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("TenE0Test!Passw0rd")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    /// <summary>
    /// 用 Docker.DotNet SDK 探测本机 Docker daemon —— 不抛异常返回 (bool, endpoint)。
    /// 测试机器无 docker / docker 未启动 / 权限不足都安全返回 false。
    ///
    /// <para>
    /// <b>Socket 探测顺序</b>（2026-06-22 本地 OrbStack 教训）：
    /// <list type="number">
    /// <item><c>DOCKER_HOST</c> 环境变量（CI 标准做法）</item>
    /// <item>OrbStack macOS 真 socket：<c>/private/var/run/docker.sock</c>（注意是
    ///   <c>/private/var/run</c> 不是 <c>/var/run</c> —— 后者是个 dangling symlink
    ///   指 <c>~/.orbstack/run/docker.sock</c>，文件并不存在）</item>
    /// <item><c>~/.orbstack/run/docker.sock</c>（旧版 OrbStack，部分 colima）</item>
    /// <item><c>/var/run/docker.sock</c>（Docker Desktop / linux 标准）</item>
    /// </list>
    /// </para>
    /// </summary>
    private static bool TryResolveDockerEndpoint(out Uri endpoint)
    {
        endpoint = null!;
        // 候选 socket 路径（按优先级）
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        var candidates = new List<Uri>();
        if (!string.IsNullOrWhiteSpace(dockerHost))
        {
            if (Uri.TryCreate(dockerHost, UriKind.Absolute, out var envUri))
                candidates.Add(envUri);
        }
        // OrbStack macOS 真 socket —— 必须用 /private/var/run 路径，/var/run 是 dangling symlink
        if (File.Exists("/private/var/run/docker.sock"))
            candidates.Add(new Uri("unix:///private/var/run/docker.sock"));
        // 旧版 OrbStack / 部分 colima：socket 直接在 ~/.orbstack/run/docker.sock
        var orbStackSocket = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".orbstack", "run", "docker.sock");
        if (File.Exists(orbStackSocket))
            candidates.Add(new Uri($"unix://{orbStackSocket}"));
        // Docker Desktop / Linux 标准路径
        if (File.Exists("/var/run/docker.sock"))
            candidates.Add(new Uri("unix:///var/run/docker.sock"));

        foreach (var ep in candidates)
        {
            try
            {
                var config = new DockerClientConfiguration(ep);
                using var client = config.CreateClient();
                // 同步 ping：PingAsync 在某些 SDK 版本会挂，这里用 GetSystemInfoAsync 同步轮询。
                var info = client.System.GetSystemInfoAsync().GetAwaiter().GetResult();
                if (info is not null)
                {
                    endpoint = ep;
                    return true;
                }
            }
            catch
            {
                // 试下一个候选
                continue;
            }
        }
        return false;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// 在当前 <see cref="ConnectionString"/> 上用内部 <see cref="TestOutboxDbContext"/> 调 <c>EnsureCreatedAsync</c>，
    /// 把 <see cref="OutboxMessage"/> 表创建出来。
    /// </summary>
    /// <remarks>
    /// 沿用既有 schema 创建模式（与 <c>Hosting/DatabaseInitializerService.cs:54</c> 同口径）：
    /// 不依赖 Migration 文件，CI 跑测试零额外步骤。
    /// </remarks>
    public async Task EnsureSchemaAsync()
    {
        var options = new DbContextOptionsBuilder<TestOutboxDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        await using var ctx = new TestOutboxDbContext(options);
        await ctx.Database.EnsureCreatedAsync();
    }

    /// <summary>
    /// 清空 OutboxMessages 表所有行（PR #88 docker-integration-tests CI 教训）：
    /// IClassFixture 跨 test method 共享同一 SQL 容器，前一个 method seed 的行会被后一个 method 看到。
    /// 每次 seed 前必须 TruncateAsync() 让 verify 阶段读到的是本 method 的状态。
    /// </summary>
    public async Task TruncateOutboxMessagesAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE OutboxMessages";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 测试专用 DbContext —— fixture 内部建表用。生产代码的 DbContext 仍由各测试方法自己建。
    /// </summary>
    private sealed class TestOutboxDbContext(DbContextOptions<TestOutboxDbContext> options)
        : DbContext(options)
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureTenE0OutboxTables();
        }
    }
}
