# Events/Outbox/ — Outbox Pattern 实现

领域事件的同事务持久化、异步发布、多实例安全（行级锁 / 分布式锁 / Leader Election 三选一）、毒消息管理。

## 文件说明

| 文件 | 职责 |
|------|------|
| `OutboxInterceptor.cs` | EF Core `SaveChangesInterceptor`：在 SaveChanges 前读取所有 `AggregateRoot.PendingEvents`，序列化为 `OutboxMessage` 行插入，与业务数据**同一事务**原子提交 |
| `OutboxMessage.cs` | 消息实体：`EventType` (AssemblyQualifiedName) / `Payload` (JSON) / `OccurredOn` / `SentTime` / `AttemptCount` / `LastError` / **`LockedUntil`** (行锁到期) / **`LockedByInstance`** (持有者) |
| `OutboxModelBuilderExtensions.cs` | EF Core 表映射与 `ConfigureTenE0OutboxTables` 扩展 |
| `OutboxRelayService.cs` | 后台 `BackgroundService<TContext>`：pick 候选 → 抢锁 → publish → 释放 → SaveChanges。`internal ProcessBatchAsync` 暴露给测试（`InternalsVisibleTo`） |
| `OutboxSchemaSeeder.cs` | 启动期 `IDataSeeder`（Order=0）：幂等 ALTER 既有数据库，补齐 #80/#85 引入的 `LockedUntil` / `LockedByInstance` 列与 `(LockedUntil, OccurredOn)` 复合索引（SqlServer 用 `sys.columns`/`sys.indexes` 探测，Postgres 用 `information_schema` + `IF NOT EXISTS`） |
| `IOutboxPublisher.cs` | 发布者接口（替换投递机制：进程内 / Kafka / CAP / RabbitMQ） |
| `InProcessOutboxPublisher.cs` | 默认进程内发布：反序列化 JSON → 路由到 `IDomainEventDispatcher` |
| `IOutboxAdmin.cs` | 死信（Poison Message）管理契约：**Get** / **Retry** / **Export** 三个操作；阈值复用 `OutboxRelayOptions.MaxAttempts`，禁止硬编码 |
| `OutboxAdminService.cs` | `IOutboxAdmin` 的泛型实现 `OutboxAdminService<TContext>`，从根容器的 `IDbContextFactory<TContext>` 解析 DbContext，避免重复声明泛型约束 |
| `OutboxPoisonMessageDto.cs` | 死信导出 DTO（不可变 record）：仅排障关键字段（Id / EventType / Payload / OccurredOn / AttemptCount / LastError），与持久化层解耦 |
| `IOutboxLock.cs` | 行级锁抽象：`TryAcquireAsync(messageId, instanceId, lease, ct)` / `ReleaseAsync(...)`。契约：返回 false 时**不应** ++ `AttemptCount`，由真正持有者处理，租约过期后另一实例接管 |
| `OutboxLockProvider.cs` | 静态选择器 + `IOutboxRowLockResolver<TContext>` 契约 + `OutboxRowLockResolver<TContext>` 默认实现 + `AddOutboxRowLock<TContext>()` DI 扩展。按 `Database.ProviderName` 字符串命名匹配（`SqlServer` → `SqlServerOutboxLock<>`, `Npgsql` → `PostgresOutboxLock<>`, 其他 → `NoOpOutboxLock`） |
| `OutboxLockProviderKind.cs` | provider 选择枚举：`None` / `RowLock` / `Distributed` / `Leader`。由 `OutboxRelayOptions.LockProvider` 配置驱动 |
| `OutboxLockOptions.cs` | 行级锁运行参数 POCO（`LockLeaseDuration` / `LockInstanceId`）—— 兼容层，权威来源已迁移到 `OutboxRelayOptions` |
| `NoOpOutboxLock.cs` | 无锁实现（0/1 实例部署默认值） |
| `SqlServerOutboxLock.cs` | SQL Server 行级锁实现：`WITH (UPDLOCK, ROWLOCK, READPAST)` 模式 |
| `PostgresOutboxLock.cs` | PostgreSQL 行级锁实现：`SELECT ... FOR UPDATE SKIP LOCKED` |
| `DistributedOutboxLock.cs` | 基于 `IMultiLevelCache` L2 的应用层分布式锁：`SET key NX EX` 原子 SETNX，`GetAsync` 纯读 ownership 检查（**不用 `GetOrSetAsync`**——见 PR #88 bot review Critical #1） |
| `LeaderElector.cs` | Leader Election 模式：`IMultiLevelCache` L2 持久化 `{prefix}:election` key。`TryAcquireAsync` = SETNX 抢主 / 续约；`ReleaseAsync` = **no-op**（per-message Release 会 evict leader 触发双 publish 真实 bug，PR #88 教训） |

## 消息生命周期

```
业务 Raise(event)
    → AggregateRoot._pendingEvents
    → OutboxInterceptor.SavingChangesAsync
    → OutboxMessage 行（同事务）
    → SaveChanges 提交

[BackgroundService] OutboxRelayService<TContext>
    每轮:
      1. pick 候选（SentTime IS NULL AND AttemptCount < MaxAttempts, 按 OccurredOn, BatchSize）
      2. 对每条 → IOutboxLock.TryAcquireAsync(msg.Id, instanceId, LockLeaseDuration)
           ├── false（他人持有 / 非 leader）→ skip，本轮不 ++AttemptCount
           └── true（独占）→ ++AttemptCount → IOutboxPublisher.PublishAsync → 成功: SentTime=now; 失败: LastError=Truncate(ex.Message, 2000)
      3. IOutboxLock.ReleaseAsync（实现多为 no-op，由 lease 过期自然让出；行级锁 provider 才真删）
      4. dc.SaveChangesAsync()

超过 MaxAttempts → 毒消息（SentTime 仍 NULL，AttemptCount ≥ 阈值）
                   → 不再被 Relay 拾取
                   → IOutboxAdmin.GetPoisonMessagesAsync() 排障
                   → IOutboxAdmin.RetryPoisonMessageAsync(id) 重置（AttemptCount=0, LastError=null, SentTime 不动）
                   → 下轮 Relay 重新拾取
```

## 多实例安全：四种 Lock Provider

`OutboxRelayOptions.LockProvider` 决定 DI 注入哪种 `IOutboxLock`：

| Kind | 实现 | 适用场景 | 存储 |
|------|------|----------|------|
| `None` | `NoOpOutboxLock` | 0/1 实例部署（默认） | — |
| `RowLock` | `SqlServerOutboxLock` / `PostgresOutboxLock`（按 `Database.ProviderName` 自动匹配） | 同库多实例 Relay | OutboxMessages 表行锁 |
| `Distributed` | `DistributedOutboxLock` | 跨库 / 不想动表结构 | L2 `IDistributedCache`（Redis） |
| `Leader` | `LeaderElector` | 全局只一个 Relay 跑投递，其余空闲 | L2 `IDistributedCache` 持久化 `{prefix}:election` |

**设计抉择**：
- **None vs Leader 看起来都"全局安全"**：None 是 0/1 实例假设（代码层信任）；Leader 是 N 实例假设 + 全局唯一执行（runtime 层强制）
- **RowLock vs Distributed**：RowLock 走 DB 事务强一致；Distributed 走 cache 高吞吐但需容忍 cache 漂移
- **Leader 模式 Release no-op** 是关键 bug 修复（PR #88）：早期实现 `RemoveAsync` 删 leader key → hostA 处理完 msg-000 → Release 删 key → hostB 立即抢主 → publish msg-000 第二次 → exactly-once 失败

## 关键设计决策

- **对比旧 MediatR 扩展**：旧版用 `MediatorExtension` 在 SaveChanges 后直接分发，有丢失事件风险。新版同事务落库，零丢失
- **`OccurredOn` 时间戳**用于排序和调试，不参与业务逻辑
- **Admin 与 Relay 查询条件对偶**：Relay 取 `SentTime IS NULL AND AttemptCount < MaxAttempts`；Admin 取 `SentTime IS NULL AND AttemptCount >= MaxAttempts`。共用 `OutboxRelayOptions.MaxAttempts`，禁止各自硬编码
- **Admin 重置不动 `SentTime`**：重试只清零 `AttemptCount` + `LastError`，保持 `SentTime == null`，下轮 Relay 自然拾取 —— 避免引入新状态字段
- **不验证（已知简化）**：跨进程 Redis 时钟漂移、Redis 集群脑裂等运维问题；L1 cache TTL 短窗口期可能让过期判定出现微小偏差（L2 是真相源）
- **Release 普遍 no-op**：除 `SqlServerOutboxLock` / `PostgresOutboxLock` 走真删外，`DistributedOutboxLock` / `LeaderElector` 都 no-op —— lock key 由 lease 过期自然让出。**生产部署必须配置非零 `LockLeaseDuration`**，否则 key 永驻 Redis 撑爆内存

## 已知风险（已缓解）

| 风险 | 状态 | 缓解措施 |
|------|------|----------|
| 多实例部署竞争同一消息 | ✅ 已解决（PR #85/#86/#88） | 4 种 lock provider，按部署形态选型 |
| 毒消息永远积压 | ✅ 已解决（PR #77） | `IOutboxAdmin` 提供 Get/Retry/Export，运维脚本或 endpoint 消费 |
| Schema 升级既有库漏字段 | ✅ 已解决（PR #85） | `OutboxSchemaSeeder`（Order=0）启动期幂等 ALTER |
| `GetOrSetAsync` 在 ownership 检查路径污染 L2 | ✅ 已解决（PR #88 review fix） | `IMultiLevelCache.GetAsync` 纯读 API 替代 |
| Docker socket 探测在 OrbStack 失败 | ✅ 已解决（PR #89） | `SqlServerContainerFixture.TryResolveDockerEndpoint` 探测 4 路径 + 注入 `DOCKER_HOST` env |

## 集成要点（依赖模块）

- **`IMultiLevelCache`**（`src/10E0.Core/Caching/`，PR #64）：`DistributedOutboxLock` + `LeaderElector` 依赖 L2（`IDistributedCache`）做互斥
- **`IAtomicCounter`**（PR #64）：备用，未来可作为"全局自增消息序号"实现幂等
- **`IDistributedCache`**（ASP.NET Core 标准）：`IMultiLevelCache` L2 真值源；生产用 Redis，测试用 `Microsoft.Extensions.Caching.Memory.MemoryDistributedCache` 或自建 `InMemoryDistributedCache`
- **`SqlServerContainerFixture`**（`tests/10E0.Core.Tests/Events/Outbox/`，PR #88）：Testcontainers.MsSql 起真实 SQL Server 跑 `OutboxRelayConcurrencyTests` 真并发"每消息恰好一次"验证

## 真并发验收测试（CI 强制）

```bash
# 跑 Requires=Docker 测试（仅在有 Docker daemon 环境下）
dotnet test --filter "Requires=Docker"
```

测试位置：`tests/10E0.Core.Tests/Events/Outbox/OutboxRelayConcurrencyTests.cs`

- 两个独立 `IServiceProvider` + 独立 `DbContext` + 共享 `IMultiLevelCache` L2 = 真分布式锁验证
- 30 轮 × 50 条消息并发跑
- 断言：Publisher 每条消息恰好被调 1 次（exactly-once 语义）

CI 由 `.github/workflows/docker-integration-tests.yml` 单独跑（ubuntu-latest 自带 Docker daemon）。
本地需 Docker Desktop / OrbStack / colima 任意一种开着，`SqlServerContainerFixture` 自动探测 socket。