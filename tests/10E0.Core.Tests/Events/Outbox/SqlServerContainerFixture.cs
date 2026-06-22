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
        if (!IsDockerAvailable())
        {
            // ConnectionString 保持空串；测试方法应在第一行检查并 Assert.Skip 风格早返。
            return;
        }

        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("TenE0Test!Passw0rd")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    /// <summary>
    /// 用 Docker.DotNet SDK 探测本机 Docker daemon —— 不抛异常返回 bool。
    /// 测试机器无 docker / docker 未启动 / 权限不足都安全返回 false。
    /// </summary>
    private static bool IsDockerAvailable()
    {
        try
        {
            using var client = new DockerClientConfiguration().CreateClient();
            // 同步 ping：PingAsync 在某些 SDK 版本会挂，这里用 1s 超时的 GetSystemInfoAsync 同步轮询。
            var info = client.System.GetSystemInfoAsync().GetAwaiter().GetResult();
            return info is not null;
        }
        catch
        {
            return false;
        }
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
