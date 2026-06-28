# Scheduling/ — 定时任务调度框架

Cron 驱动的定时任务框架（issue #164）：声明式静态任务（`[Scheduled]`）+ 动态任务 Admin API、持久化调度、多实例集群行级锁、失败重试、执行历史、可观测性埋点。

## 设计取舍

- **用 Cronos（轻量 ~30KB，MIT，只做 Cron 解析）而非 Quartz.NET / Hangfire**：Quartz 自带调度器/持久化/集群，与本框架已有的 Outbox Lock + EF Core 体系重复；Hangfire 依赖其作业存储（Redis/SQL），与 EF Core 耦合差，且 Dashboard 商业版才有。
- **集群锁复用 Outbox Lock 模式**（#80/#81）：`IJobLock` 与 `IOutboxLock` 同设计模式（None/RowLock/Distributed），保持架构一致性。当前实现 None + RowLock；Distributed（Redis SETNX）留后续。
- **动态任务安全**：`JobType` 必须实现 `IScheduledJob` 且所在程序集在白名单内（`SchedulingOptions.AllowedAssemblies`，默认回退到 `JobAssemblies`），防止反射注入任意代码。

## 文件说明

| 文件 | 职责 |
|------|------|
| `ScheduledAttribute.cs` | `[Scheduled("0 0 9 * * ?")]` 静态任务标记，含 Description/Code/MaxRetries |
| `IScheduledJob.cs` | 任务接口 + `JobContext`（job/attempt/parameters） |
| `ScheduledJobBase.cs` | 抽象基类，提供日志样板（继承非强制） |
| `Entities/TenE0ScheduledJob.cs` | 任务定义实体（`AuditedEntity` + `IMultiTenantEntity`）：Code/Cron/JobType/ParametersJson/IsEnabled/Mode/MaxRetries/RetryInterval/LastRunAt/NextRunAt/LastRunStatus/LockedByInstance/LockedUntil |
| `Entities/TenE0JobExecution.cs` | 执行历史实体（每次完整执行含重试写一行）：JobId/StartedAt/FinishedAt/Status/ErrorMessage/Attempt/InstanceId |
| `Entities/SchedulingModelBuilderExtensions.cs` | `ConfigureTenE0SchedulingTables()` 表映射 + 列长常量（列长权威源） |
| `CronExtensions.cs` | 封装 Cronos：`Parse` / `GetNextOccurrence` / `IsValid`；`CronFormatException` → `ArgumentException` |
| `IJobLock.cs` | 集群锁抽象：`TryAcquireAsync(jobCode, instanceId, lease, ct)` / `ReleaseAsync` / `IsRunningAsync` |
| `NoOpJobLock.cs` | 无锁实现（0/1 实例默认） |
| `RowJobLock.cs` | 行级锁（LINQ InMemory + SQL 双路径，复用 `SqlServerOutboxLock` 模式） |
| `SchedulingOptions.cs` | 运行参数：ScanInterval/TimeZone/JobTimeout/LockLeaseDuration/LockProvider/LockInstanceId/AllowedAssemblies/JobAssemblies |
| `JobExecutor.cs` | 执行器：从 DI 解析 handler → 重试 → 历史记录 → 超时 → 失败触发 `JobFailedEvent` → 更新 NextRunAt |
| `IScheduler.cs` / `Scheduler.cs` | 管理面 CRUD + 手动触发（Admin API 用） |
| `SchedulerWorker.cs` | 后台 `BackgroundService`：每 ScanInterval 扫描 NextRunAt<=now → 抢锁 → 执行 |
| `StaticJobRegistrar.cs` | 扫描 `[Scheduled]` 程序集，启动期幂等 upsert（`IDataSeeder` Order=1） |
| `JobFailedEvent.cs` | 重试耗尽领域事件（经 Outbox 异步分发，关联 #152 审计 / #155 推送） |

## 任务生命周期

```
代码标 [Scheduled]  →  StaticJobRegistrar（IDataSeeder，启动期）
                        ↓ 幂等 upsert 到 TenE0ScheduledJob 表（Code 唯一键）
                        ↓ 新 Code 插入；已存在更新 Cron/Name（保留 LastRunAt/Status 历史）

[BackgroundService] SchedulerWorker（每 ScanInterval）
    每轮:
      1. pick（IsEnabled AND NextRunAt <= now），Take 50
      2. 对每个 job → IJobLock.TryAcquireAsync(job.Code, instanceId, LockLeaseDuration)
           ├── false（他人持有 / 租约未到）→ skip，本轮不动状态
           └── true（独占）→ JobExecutor.ExecuteAsync:
                  for attempt in 1..MaxRetries:
                    try: handler.ExecuteAsync → 成功标记 Success; break
                    catch: 非末次 → Task.Delay(RetryInterval) 重试；末次 → Failed
                    超时（CancellationTokenSource.CancelAfter(JobTimeout)）→ Timeout，停止重试
                  记录 TenE0JobExecution（Running→最终状态）
                  失败 → 触发 JobFailedEvent（Outbox 异步分发）
                  更新 job.NextRunAt = Cron.GetNextOccurrence(now)
      3. IJobLock.ReleaseAsync（实现层按 jobCode+instanceId 校验所有权）
```

## 集群协调：两种 Lock Provider

`SchedulingOptions.LockProvider` 决定 DI 注入哪种 `IJobLock`：

| Kind | 实现 | 适用场景 | 存储 |
|------|------|----------|------|
| `None`（默认） | `NoOpJobLock` | 0/1 实例部署 | — |
| `RowLock` | `RowJobLock<TContext>` | 同库多实例调度 | `TenE0ScheduledJobs` 行锁（`LockedByInstance`/`LockedUntil` 列） |
| `Distributed` | 预留（当前回退 None） | 跨库 / Redis | 未实现，留后续 |

**锁租约语义**：租约 = 单个任务**单次执行**的最长独占窗口（SchedulerWorker 对每个 job 单独 TryAcquire/Release，不是整批持锁）。需满足 `LockLeaseDuration ≥ JobTimeout`：租约 < JobTimeout → 任务未跑完锁已过期，另一实例重复拾取（双重执行）；启动期 `ValidateOnStart` 校验此约束。默认两者均 5 分钟（崩溃后接管延迟 5 分钟，可接受）。租约过期后任何实例下轮 TryAcquire 即可重新拾取。

## 关键设计决策

- **历史记录粒度**：每次完整执行（含内部所有重试）写**一行** `TenE0JobExecution`，`Attempt` 字段记录最终成功/失败的尝试序号。避免重试爆炸历史表，同时保留重试信息。
- **静态任务幂等 upsert 保留运维历史**：代码改 Cron 后重启，`StaticJobRegistrar` 更新 `CronExpression` 但**保留** `LastRunAt`/`LastRunStatus`/`NextRunAt`（运维历史不可因代码改动清零）。
- **代码已删的 Static 任务不物理删除**：改为 `IsEnabled = false`（保留运维历史，需手动清理），避免误删。
- **静态任务不可通过 API 修改**：`Scheduler.UpdateJobAsync` 对 `Mode = Static` 的任务抛异常（其定义在代码中，改代码后重启生效）。
- **手动触发去重**：`Scheduler.TriggerJobAsync` 先查 `IJobLock.IsRunningAsync`，任务正被某实例执行时拒绝触发（避免手动 + 定时重叠）。`IsRunningAsync` 在 `NoOpJobLock` 下恒 false（单实例靠 `BackgroundService` 串行保证）。
- **时区**：Cron 默认用 UTC（`SchedulingOptions.TimeZone`，避免夏令时坑），业务需本地时区时显式配置。
- **超时硬截断**：`JobExecutor` 用局部 `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter(JobTimeout)`（默认 5 分钟），超时即取消 handler 并标记 `Timeout`，防僵死任务占锁。CTS 用局部 `using`（非实例字段），避免并发执行时字段互相覆盖。
- **任务参数**：`JobContext.Parameters` 是 `JsonElement?`（从 `ParametersJson` 解析），任务用 `context.GetParameters<T>()()` 反序列化为强类型。非法 JSON 按无参数处理（不阻断执行）。
- **白名单 fail-secure**：`AllowedAssemblies` 控制 JobType 反射白名单。语义：`null`（未配置）+ `JobAssemblies` 也空 → 不限制（opt-in demo 零配置可用）；`null` 但 `JobAssemblies` 非空 → 用 `JobAssemblies` 作白名单；显式设 `[]` 或不含本程序集 → **拒绝**（运维显式收紧，防反射注入任意代码）。
- **失败事件顺序**：`JobFailedEvent` 在所有 DB 提交（历史行 + 任务定义）**之后**触发。`IDomainEventDispatcher` 当前是 InProcess 直连 handler（非 Outbox 持久化），若在提交前发事件、后续 SaveChanges 回滚会导致"事件已发但状态未落库"的不一致。放最后保证一致性。
- **租户过滤**：`TenE0ScheduledJob`/`TenE0JobExecution` 实现 `IMultiTenantEntity`，但后台调度在无 HTTP 请求的线程跑，`ITenantContext`（请求作用域）无值。因此 `StaticJobRegistrar.SeedAsync`、`JobExecutor` 的所有读写路径都 `IgnoreQueryFilters()`（系统级全局任务，跨租户）。

## 集成要点（依赖模块）

- **Cronos**（外部 NuGet，MIT）：Cron 解析 + 下次执行计算。
- **`TenE0SystemDbContext`**：自动注册 `TenE0ScheduledJob` + `TenE0JobExecution` 表 + `ConfigureTenE0SchedulingTables()`。
- **`IDataSeeder`**（`Hosting/`）：`StaticJobRegistrar` Order=1（Outbox SchemaSeeder Order=0 之后，业务 seeder Order=10+ 之前）。
- **`IDomainEventDispatcher`**（`Events/`）：`JobExecutor` 失败时触发 `JobFailedEvent`（InProcess 直连 handler，在 DB 提交后触发）；未启用领域事件时 no-op。
- **`TenE0Metrics`**（`Observability/`）：`tene0.job.executed` Counter（tag job_code + result）+ `tene0.job.active` ObservableGauge；未注册时 no-op。
- **Outbox `IOutboxLock` 模式**（`Events/Outbox/`）：`RowJobLock` 复用 `SqlServerOutboxLock` 的 LINQ + SQL 双路径设计。**表名不硬编码**：SQL 路径通过 `ctx.Model.FindEntityType().GetTableName()` 从 EF 元数据读回（实体改名 / ToTable 约定调整时原始 SQL 与 LINQ 映射始终一致）。

## 启用方式

```csharp
// Program.cs
builder.Services.AddTenE0All<AppUser, DemoDbContext>(builder.Configuration, opt =>
{
    opt.Scheduling = true;
    opt.SchedulingOptions = sched =>
    {
        sched.ScanInterval = TimeSpan.FromSeconds(30);
        sched.LockProvider = JobLockProviderKind.RowLock; // 多实例部署时启用
    };
});
```

静态任务示例：

```csharp
[Scheduled("0 0 2 * * ?", Description = "每天凌晨清理临时文件")]
public class CleanupTempFilesJob : ScheduledJobBase
{
    protected override Task ExecuteJobAsync(JobContext context, CancellationToken ct)
    {
        // 清理逻辑...
        return Task.CompletedTask;
    }
}
```

Admin API（全部 `[RequireAdmin]`）：

| 端点 | 用途 |
|------|------|
| `GET /admin/scheduler/jobs` | 任务列表 |
| `POST /admin/scheduler/jobs` | 创建动态任务 |
| `PUT /admin/scheduler/jobs/{id}` | 修改（Cron/启用/参数，仅动态任务） |
| `POST /admin/scheduler/jobs/{id}/trigger` | 手动立即触发 |
| `POST /admin/scheduler/jobs/{id}/enable` | 启用 |
| `POST /admin/scheduler/jobs/{id}/disable` | 暂停 |
| `GET /admin/scheduler/jobs/{id}/executions` | 执行历史 |

## 已知简化（后续可扩展）

- **Distributed 锁未实现**：`IJobLock` 接口已预留 `JobLockProviderKind.Distributed`，配置时回退 None（不抛异常）。后续可加基于 `IMultiLevelCache` L2 SETNX 的实现（复刻 `DistributedOutboxLock`）。
- **无独立 Schema seeder**：本模块是新建表，`EnsureCreatedAsync` 全新库自动建表；既有库升级需手工迁移或切 `Migrate()`（与 Outbox 的 ALTER seeder 不同，无需补列）。
- **任务参数无类型校验**：`ParametersJson` 是自由 JSON，`JobContext.Parameters` 是 `object?`，具体类型由任务约定。后续可加 `[Scheduled]` 的参数类型声明做反序列化校验。
- **Workflow `TimeoutProcessor` 未迁移**：issue #164 标注 #159 工作流超时处理器可改用本模块（从固定轮询升级为 Cron），留后续协同工作。
