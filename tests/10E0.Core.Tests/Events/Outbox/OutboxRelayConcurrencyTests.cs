using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenE0.Core.Caching;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Tests.Events.Outbox.TestFakes;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// BDD 验收测试 — Outbox Relay 真实并发"每条消息恰好一次"(feature #82 终验)
///
/// <para>
/// <b>业务动机</b>：
/// <list type="bullet">
/// <item>前序步骤 (#85 / #86) 已落地 <see cref="IOutboxLock"/> 抽象 + SQL Server / PostgreSQL 行级锁 provider，
/// 但都仅用 InMemory 单测验证契约语义；本测试用 Testcontainers 启真实 SQL Server 容器，
/// 跑两个独立 Host（独立 <c>IServiceProvider</c> + 独立 DbContext 连接）+ 预置 50 条消息 +
/// 30 轮并发轮询，断言：</item>
/// <item>(1) Publisher mock 被每条消息恰好调用 1 次（exactly-once 投递语义）；</item>
/// <item>(2) 全部 50 条消息的 <c>SentTime</c> 都非空（都被成功投递）；</item>
/// <item>(3) 50 条消息的 <c>AttemptCount</c> 总和 == 50（无重复拾取 = 无重复 ++）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>设计差异（vs <c>OutboxRelayConcurrencyAcceptanceTests</c>）</b>：<br/>
/// 本文件采用 <see cref="IClassFixture{TFixture}"/> 共享 <see cref="SqlServerContainerFixture"/>
/// （避免每测试启容器），并直接引用 <c>Testcontainers.MicrosoftSqlServer</c> 强类型 API
/// （而非反射加载 NuGet 程序集），编译期即暴露包引用错误。
/// </para>
///
/// <para>
/// <b>CI 注意</b>：本测试需要 Docker。本地无 Docker 时静默跳过；
/// CI 需在能跑 Docker 的 runner 上执行（maintainer 划归后续 issue 解决）。
/// </para>
/// </summary>
[Trait("Category", "Acceptance")]
[Trait("Requires", "Docker")]
public sealed class OutboxRelayConcurrencyTests : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture;

    public OutboxRelayConcurrencyTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    // ================================================================
    // Test Infrastructure
    // ================================================================

    public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureTenE0OutboxTables();
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options)
        : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    /// <summary>
    /// Publisher mock —— 记录每条 messageId 被 publish 的次数。
    /// Exactly-once 验证的关键探针（与 OutboxRelayConcurrencyAcceptanceTests 一致）。
    /// </summary>
    private sealed class TrackingOutboxPublisher
    {
        private readonly ConcurrentDictionary<string, int> _callCounts = new();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _sentTimes = new();

        public IOutboxPublisher AsIOutboxPublisher() => new Impl(this);

        public int CallCount(string messageId)
            => _callCounts.TryGetValue(messageId, out var c) ? c : 0;

        public DateTimeOffset? SentTime(string messageId)
            => _sentTimes.TryGetValue(messageId, out var t) ? t : null;

        public IReadOnlyDictionary<string, int> AllCalls() => _callCounts;

        private sealed class Impl : IOutboxPublisher
        {
            private readonly TrackingOutboxPublisher _owner;
            public Impl(TrackingOutboxPublisher owner) => _owner = owner;

            public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
            {
                _owner._callCounts.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
                _owner._sentTimes[message.Id] = DateTimeOffset.UtcNow;
                return Task.CompletedTask;
            }
        }
    }

    private static OutboxMessage NewMessage(string id, DateTimeOffset occurredOn)
        => new()
        {
            Id = id,
            EventType = "TestEvent",
            Payload = "{}",
            OccurredOn = occurredOn,
        };

    /// <summary>
    /// 用 <see cref="AddTenE0DomainEvents{TContext}"/> 完整注册 Relay + OutboxLocking 基础设施，
    /// 再注入 <paramref name="publisher"/> 作为 <see cref="IOutboxPublisher"/> 的实际实现。
    /// 关键：<paramref name="sharedMemoryCache"/> / <paramref name="sharedDistributedCache"/> / <paramref name="sharedCounter"/>
    /// 由调用方在 BuildHost 外部构造一次，让两个 host 通过 DI 拿到**同一个**实例 —— 这样 Distributed/Leader 模式
    /// 才能真验证多实例下的 SETNX / 续约语义（#82 PR #88 bot review 揭示：每个 host 各 new 一个 cache 会让
    /// Distributed/Leader 模式静默回退 NoOp，"exactly-once" 断言形同虚设）。
    /// </summary>
    private static IServiceProvider BuildHost(
        string connectionString,
        string instanceId,
        OutboxLockProviderKind lockProvider,
        TrackingOutboxPublisher publisher,
        IMemoryCache sharedMemoryCache,
        IDistributedCache sharedDistributedCache,
        IAtomicCounter sharedCounter,
        out IDbContextFactory<TestDbContext> outFactory)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        // 共享 cache + counter：两个 host 拿到的是同一份实例，Distributed/Leader 模式才能跨 host 真验证
        services.AddSingleton(sharedMemoryCache);
        services.AddSingleton(sharedDistributedCache);
        services.AddSingleton(sharedCounter);

        // OutboxRelayService<TContext> ctor 依赖 TimeProvider（生产由 AddTenE0Core 注册），
        // 测试 BuildHost 不调 AddTenE0Core 所以单独注册（PR #88 docker-integration-tests CI 教训）。
        services.AddSingleton(TimeProvider.System);

        // ⚠️ 关键：IMultiLevelCache 必须显式注册为共享实例（PR #88 docker CI 第三次教训）
        //   早期 BuildHost 只共享 L1/L2 cache + counter，没共享 MultiLevelCache。
        //   MultiLevelCache 是 internal sealed class，每个 DI 容器 new 一个 → 各自一个 _setnxGate
        //   锁不跨 host 共享 → SETNX race 仍存在 → 两个 host 都 publish 同一消息。
        //   显式 AddSingleton 在 AddTenE0Caching 之前 → TryAddSingleton 不会覆盖 → 两个 host
        //   拿到同一 MultiLevelCache 实例 → _setnxGate 锁真跨 host 共享 → SETNX 原子性真生效。
        services.AddSingleton<IMultiLevelCache>(_ =>
            new MultiLevelCache(sharedMemoryCache, sharedDistributedCache));

        var ctxOptions = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(connectionString)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var factory = new TestDbContextFactory(ctxOptions);
        services.AddSingleton<IDbContextFactory<TestDbContext>>(factory);
        outFactory = factory;

        services.AddSingleton<IOutboxPublisher>(publisher.AsIOutboxPublisher());

        services.AddSingleton(Options.Create(new OutboxRelayOptions
        {
            LockInstanceId = instanceId,
            LockProvider = lockProvider,
            LockLeaseDuration = TimeSpan.FromSeconds(30),
            BatchSize = 50,
            PollInterval = TimeSpan.FromMilliseconds(100),
        }));

        // 走 AddTenE0DomainEvents 真实集成路径（自动 AddOutboxLocking<TContext>），
        // 让 IOutboxLock 的 switch 表达式按 LockProvider 选项分派真实实现。
        // AddTenE0Caching 内 IMemoryCache/IMultiLevelCache/IAtomicCounter 都用 TryAdd，
        // 上面已注册的共享实例不会被覆盖。
        // ⚠️ 必须显式调 AddTenE0Caching() —— AddTenE0DomainEvents 不调它，IMultiLevelCache
        //   未注册时 AddOutboxLocking 内部 try/catch 兜底返回 NoOpOutboxLock（无锁），
        //   两个 host 都 publish 同一消息 → exactly-once 失败（PR #88 docker CI 教训：早期
        //   BuildHost 缺这一步，所有 lock fix 全部白做）。
        services.AddTenE0Caching();
        services.AddTenE0DomainEvents<TestDbContext>();

        return services.BuildServiceProvider();
    }

    // ================================================================
    // Scenario 1: Distributed 模式 — 两个独立 Host 并发跑 50 条消息，每条恰好投递 1 次
    // ================================================================

    [Fact]
    public async Task TwoRelayHosts_Concurrent_50Messages_EachPublishedExactlyOnce()
    {
        // Docker 不可用（fixture 留空 ConnectionString）→ loud-fail（#82 PR #88 教训）：
        // 静默 return 会让"无 Docker 时测试也 Pass"，给后续 review 假象。
        // 现在直接 Assert.Fail 让任何跑 dotnet test 的人知道这些测试没真跑。
        if (string.IsNullOrEmpty(_fixture.ConnectionString))
        {
            Assert.Fail(
                "Requires Docker daemon. Test uses Testcontainers.MsSql to spin up real SQL Server. "
                + "Run on a machine with Docker (e.g. macOS Docker Desktop), "
                + "or skip intentionally with `--filter \"FullyQualifiedName!~OutboxRelayConcurrencyTests\"`.");
        }

        // Arrange — 用 fixture 共享容器 + EnsureCreated 建表
        var connectionString = _fixture.ConnectionString;
        await _fixture.EnsureSchemaAsync();
        // 关键：清空前一个 test method 留下的 OutboxMessage 行（PR #88 docker CI 教训）
        await _fixture.TruncateOutboxMessagesAsync();

        // Seed 50 条 OutboxMessage
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        var seededIds = Enumerable.Range(0, 50)
            .Select(i => $"msg-{i:D3}")
            .ToArray();
        await using (var seedCtx = new TestDbContext(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlServer(connectionString).Options))
        {
            for (int i = 0; i < 50; i++)
            {
                seedCtx.OutboxMessages.Add(NewMessage(seededIds[i], baseTime.AddMilliseconds(i)));
            }
            await seedCtx.SaveChangesAsync();
        }

        // 两个独立 Host + 共享 L1/L2 + counter（让 Distributed 模式 SETNX 真验证跨 host 互斥）
        var sharedPublisher = new TrackingOutboxPublisher();
        var sharedMemoryCache = new MemoryCache(new MemoryCacheOptions());
        var sharedDistributedCache = new InMemoryDistributedCache();
        var sharedCounter = new L2AtomicCounterForTest(sharedDistributedCache);
        var hostA = BuildHost(
            connectionString, "host-A", OutboxLockProviderKind.Distributed, sharedPublisher,
            sharedMemoryCache, sharedDistributedCache, sharedCounter, out _);
        var hostB = BuildHost(
            connectionString, "host-B", OutboxLockProviderKind.Distributed, sharedPublisher,
            sharedMemoryCache, sharedDistributedCache, sharedCounter, out _);

        // 直接调 internal ProcessBatchAsync（不再用反射 — PR #88 bot review 🟡 Suggestion 3）
        // internal 由 10E0.Core 的 InternalsVisibleTo("10E0.Core.Tests") 开放
        var batchProcessor = typeof(OutboxRelayService<TestDbContext>).GetMethod(
            "ProcessBatchAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "OutboxRelayService<TestDbContext>.ProcessBatchAsync 不可见 — 方法签名变更或 access modifier 改回 private？");

        // OutboxRelayService<TContext> 只以 IHostedService 身份注册（生产代码仅 BackgroundService 消费），
        // 拿实例要走 GetServices<IHostedService>().OfType<...>().First()（PR #88 CI 教训：直接 GetRequiredService<T> 找不到）。
        var relayA = hostA.GetServices<IHostedService>().OfType<OutboxRelayService<TestDbContext>>().First();
        var relayB = hostB.GetServices<IHostedService>().OfType<OutboxRelayService<TestDbContext>>().First();

        // 30 轮并发跑（约 30s × 1 轮/秒）
        const int rounds = 30;
        var taskA = Task.Run(async () =>
        {
            for (int i = 0; i < rounds; i++)
            {
                await (Task<int>)batchProcessor.Invoke(relayA, new object[] { CancellationToken.None })!;
                await Task.Delay(1000);
            }
        });
        var taskB = Task.Run(async () =>
        {
            for (int i = 0; i < rounds; i++)
            {
                await (Task<int>)batchProcessor.Invoke(relayB, new object[] { CancellationToken.None })!;
                await Task.Delay(1000);
            }
        });
        await Task.WhenAll(taskA, taskB);

        // 读回真实状态
        int attemptSum;
        int sentCount;
        List<OutboxMessage> allRows;
        await using (var verifyCtx = new TestDbContext(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlServer(connectionString).Options))
        {
            allRows = await verifyCtx.OutboxMessages.ToListAsync();
            attemptSum = allRows.Sum(m => m.AttemptCount);
            sentCount = allRows.Count(m => m.SentTime != null);

            // DIAG: 失败时把所有行 dump 到 /tmp/outbox-diag.txt 供 CI artifact 上传
            if (sentCount != 50 || attemptSum != 50)
            {
                var sorted = allRows.OrderBy(m => m.OccurredOn).ThenBy(m => m.Id).ToList();
                var dump = $"sentCount={sentCount}, attemptSum={attemptSum}, totalRows={allRows.Count}\n"
                    + $"distinctIds={allRows.Select(m => m.Id).Distinct().Count()}\n"
                    + "---\n"
                    + string.Join("\n", sorted.Select(m =>
                        $"Id={m.Id}|SentTime={(m.SentTime?.ToString("o") ?? "null")}|Attempt={m.AttemptCount}|OccurredOn={m.OccurredOn:o}"));
                try
                {
                    File.WriteAllText("/tmp/outbox-diag.txt", dump);
                }
                catch
                {
                    // best-effort dump; 不影响主断言失败
                }
                Assert.Fail(
                    $"[DIAG] sentCount={sentCount}, attemptSum={attemptSum}, totalRows={allRows.Count}, "
                    + $"distinctIds={allRows.Select(m => m.Id).Distinct().Count()}. "
                    + $"Full row dump written to /tmp/outbox-diag.txt (CI uploads via actions/upload-artifact). "
                    + $"First 3: {string.Join(" | ", sorted.Take(3).Select(m => $"Id={m.Id} Attempt={m.AttemptCount}"))}. "
                    + $"Last 3: {string.Join(" | ", sorted.TakeLast(3).Select(m => $"Id={m.Id} Attempt={m.AttemptCount}"))}.");
            }
        }

        // Assert — 每条消息恰好投递 1 次
        foreach (var id in seededIds)
        {
            sharedPublisher.CallCount(id).Should().Be(
                1,
                $"消息 {id} 在两个 Host 并发跑 Relay 期间必须被 PublisherMock 恰好调用 1 次 — "
                + "这是 #82 核心验收：分布式锁防止了 #74 已知风险 #1 的重复投递");
        }

        sentCount.Should().Be(
            50,
            "全部 50 条消息必须被成功投递（SentTime 非空）");

        attemptSum.Should().Be(
            50,
            "AttemptCount 总和必须 == 50 — 分布式锁让每个 Host 只拾取自己拿到的部分，"
            + "不会出现 'A 拾取 +1, B 又拾取 +1' 的双 ++");
    }

    // ================================================================
    // Scenario 2: Leader 模式 — 两个 Host 并发跑 50 条消息，leader 单实例承担全部投递
    //   Leader 模式从根上消除竞争（全局一把锁）；非 leader 实例的 publisher 不应被调用。
    //   本测试用两个 TrackingOutboxPublisher 区分 hostA / hostB 谁是 leader。
    // ================================================================

    [Fact]
    public async Task TwoRelayHosts_Leader_OnlyOneRelayProcessesMessages()
    {
        // Docker 不可用（fixture 留空 ConnectionString）→ loud-fail（#82 PR #88 教训）：
        // 静默 return 会让"无 Docker 时测试也 Pass"，给后续 review 假象。
        // 现在直接 Assert.Fail 让任何跑 dotnet test 的人知道这些测试没真跑。
        if (string.IsNullOrEmpty(_fixture.ConnectionString))
        {
            Assert.Fail(
                "Requires Docker daemon. Test uses Testcontainers.MsSql to spin up real SQL Server. "
                + "Run on a machine with Docker (e.g. macOS Docker Desktop), "
                + "or skip intentionally with `--filter \"FullyQualifiedName!~OutboxRelayConcurrencyTests\"`.");
        }

        // Arrange — 与 Scenario 1 共用 fixture + schema
        var connectionString = _fixture.ConnectionString;
        await _fixture.EnsureSchemaAsync();
        // 关键：清空前一个 test method 留下的 OutboxMessage 行（PR #88 docker CI 教训）
        await _fixture.TruncateOutboxMessagesAsync();

        // Seed 50 条
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        var seededIds = Enumerable.Range(0, 50)
            .Select(i => $"leader-msg-{i:D3}")
            .ToArray();
        await using (var seedCtx = new TestDbContext(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlServer(connectionString).Options))
        {
            for (int i = 0; i < 50; i++)
            {
                seedCtx.OutboxMessages.Add(NewMessage(seededIds[i], baseTime.AddMilliseconds(i)));
            }
            await seedCtx.SaveChangesAsync();
        }

        // 两个独立 publisher（每个 Host 一个），用以区分谁实际在发 + 共享 L1/L2 + counter
        // （让 Leader 模式 SETNX 真验证"同一时刻仅一个 leader"，且 follower 真能观察到自己不是 leader）
        var publisherA = new TrackingOutboxPublisher();
        var publisherB = new TrackingOutboxPublisher();
        var sharedMemoryCache = new MemoryCache(new MemoryCacheOptions());
        var sharedDistributedCache = new InMemoryDistributedCache();
        var sharedCounter = new L2AtomicCounterForTest(sharedDistributedCache);
        var hostA = BuildHost(
            connectionString, "host-A", OutboxLockProviderKind.Leader, publisherA,
            sharedMemoryCache, sharedDistributedCache, sharedCounter, out _);
        var hostB = BuildHost(
            connectionString, "host-B", OutboxLockProviderKind.Leader, publisherB,
            sharedMemoryCache, sharedDistributedCache, sharedCounter, out _);

        var batchProcessor = typeof(OutboxRelayService<TestDbContext>).GetMethod(
            "ProcessBatchAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "OutboxRelayService<TestDbContext>.ProcessBatchAsync 不可见");

        // OutboxRelayService<TContext> 只以 IHostedService 身份注册（生产代码仅 BackgroundService 消费），
        // 拿实例要走 GetServices<IHostedService>().OfType<...>().First()（PR #88 CI 教训：直接 GetRequiredService<T> 找不到）。
        var relayA = hostA.GetServices<IHostedService>().OfType<OutboxRelayService<TestDbContext>>().First();
        var relayB = hostB.GetServices<IHostedService>().OfType<OutboxRelayService<TestDbContext>>().First();

        const int rounds = 30;
        var taskA = Task.Run(async () =>
        {
            for (int i = 0; i < rounds; i++)
            {
                await (Task<int>)batchProcessor.Invoke(relayA, new object[] { CancellationToken.None })!;
                await Task.Delay(1000);
            }
        });
        var taskB = Task.Run(async () =>
        {
            for (int i = 0; i < rounds; i++)
            {
                await (Task<int>)batchProcessor.Invoke(relayB, new object[] { CancellationToken.None })!;
                await Task.Delay(1000);
            }
        });
        await Task.WhenAll(taskA, taskB);

        // Assert 1: 50 条全被发（任一 publisher 累计）
        var totalCalls = publisherA.AllCalls().Sum(kv => kv.Value)
                       + publisherB.AllCalls().Sum(kv => kv.Value);
        totalCalls.Should().Be(
            50,
            "Leader 模式：50 条消息必须全部被发布（leader 实例的 publisher 承担全部）");

        // Assert 2: 只一个 publisher 被调用（leader 单实例）
        var aCalls = publisherA.AllCalls().Count;
        var bCalls = publisherB.AllCalls().Count;

        // 一个 publisher 必为 0（follower），另一个 ≥ 50（leader）
        (aCalls == 0 ^ bCalls == 0).Should().BeTrue(
            "Leader 模式：只有一个 Relay 实例（leader）能调用 publisher，follower 全程不投递 — "
            + $"实测 A={aCalls} B={bCalls}");

        // Assert 3: leader 的 publisher 每条恰好 1 次
        var leader = aCalls > 0 ? publisherA : publisherB;
        foreach (var id in seededIds)
        {
            leader.CallCount(id).Should().Be(
                1,
                $"Leader 模式：leader 的 publisher 对消息 {id} 必须恰好调用 1 次");
        }
    }
}
