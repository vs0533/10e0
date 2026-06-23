# Caching/ — 多级缓存 + 原子计数器

业务缓存的统一抽象层：L1 (进程内 `IMemoryCache`) + L2 (分布式 `IDistributedCache`) + 工厂回源；以及配套的全局原子计数器。所有 Outbox 分布式锁、权限版本号、权限缓存等场景都走这套接口。

## 文件说明

| 文件 | 职责 |
|------|------|
| `IMultiLevelCache.cs` | 多级缓存抽象：`GetOrSetAsync` (L1→L2→factory 三级回源) / `TrySetAsync` (SETNX) / `SetAsync` (覆盖) / `RemoveAsync` (双清) / **`GetAsync`** (纯读，专为分布式锁 ownership 检查设计，PR #88 新增) |
| `IAtomicCounter.cs` | 原子计数器：`IncrementAsync` (Redis `INCR` / 内存 `Interlocked.Increment` / EF `UPDATE OUTPUT INSERTED.Value`) / `GetAsync` |
| `CacheOptions.cs` | TTL 策略 record：`L1Duration` / `L2Duration`；提供 `CacheOptions.Default` (L1=5s / L2=5min) |
| `CachingOptions.cs` | L1 容量策略 record：`SizeLimit` (默认 16 MB) / `CompactionPercentage` (0.05)；通过 `AddTenE0Caching(opts => opts with { ... })` 委托或 `AddTenE0Caching(configure)` 配置节绑定（PR #101 防 OOM 兜底） |
| `CacheEntryOptionsExtensions.cs` | `IMemoryCacheEntryOptions` / `IDistributedCacheEntryOptions` 的快速构造扩展 |
| `DefaultCachingImplementations.cs` | 默认实现：`MultiLevelCache` (PR #88 修复 `_setnxGate` 必须 `static` 锁) + `DistributedAtomicCounter` |

## 读路径分层

```
GetOrSetAsync<T>(key, factory, opts)
   │
   ├─ L1 命中? → 直接返回（微秒级）
   │
   ├─ L2 命中? → JSON 反序列化 → 回填 L1 → 返回
   │
   └─ L1 + L2 都 miss → factory() 回源（DB / RPC / 计算）
                              → 双写 L2 + L1
                              → 返回
```

**TTL 推荐配置**：`L1Duration` < `L2Duration`（如 L1=5s / L2=5min）—— L1 短过期让 stale 数据快速回源；L2 长过期避免每次都打 DB / RPC。

## 设计决策

- **L1 / L2 都必须非 null**：`MultiLevelCache` 构造强制要求两者；如只需单层请直接注入 `IMemoryCache` 或 `IDistributedCache`。
- **JSON 序列化统一 `System.Text.Json`**：跨进程兼容（L2 用 Redis / SQL Server 时序列化/反序列化两端都是 .NET）。
- **`GetOrSetAsync` 不分独立的 Get/Set**：避免业务方写"先查后写"产生竞态；impl 用 L1 TTL 控制最终一致性。
- **`RemoveAsync` 双清**：L1 + L2 同时移除；只清 L1 会导致 stale L2 在下次 L1 miss 时回流污染。
- **`TrySetAsync` 必须原子**：用 `static readonly object _setnxGate` 锁住"L1 check + L2 check + L2 set + L1 set"全序列，让同进程多线程并发 SETNX 严格只一个 success。**为什么必须是 `static`**（PR #88 教训）：测试场景用同进程两个 `ServiceProvider` 模拟"两个 Relay 实例共享同 cache 后端"，每个 `ServiceProvider` 各自 `new` 一个 `MultiLevelCache` → 实例级锁不跨 host 共享 → SETNX race 仍存在；`static` 锁让所有 `MultiLevelCache` 实例共享同一进程内锁。生产部署不同进程各自 `static` 锁不共享，但生产 Redis SETNX 天然原子（`SET key NX EX` 在 Redis 端单步），跨进程不需要本锁。
- **`GetAsync` 纯读契约**（PR #88 bot review Critical #1）：分布式锁 ownership 检查必须用 `GetAsync`，**不能用 `GetOrSetAsync`**。原因：`GetOrSetAsync` 在 L1+L2 miss 时会调 factory，factory 返回非 null 会回写 L2 → 如果 key 恰好 lease 过期 / Redis LRU 失效，factory 写自己的 instanceId → 后续读到"自己" → `string.Equals` 命中 → 调用方误以为抢到锁 → **exactly-once 失败**。`GetAsync` 是纯读契约，L1 miss + L2 miss 时返回 null，调用方直接判定"非自己 owner"，永远不会污染 L2。
- **`DistributedAtomicCounter` 用 `Get → 解析 → Set` 三步**（**非原子**！）：当前实现适用于单进程 `MemoryDistributedCache`；多副本部署 / Redis 后端需要业务项目替换为 `INCR` 原生命令实现（通过 IDistributedCache 的 Redis 扩展或自建接口）。**不抛 Redis Lua 脚本依赖是为了保持与 `IDistributedCache` 抽象的兼容性**。

## 集成要点

- **`Outbox` 多实例锁**：所有 lock provider（`DistributedOutboxLock` / `LeaderElector`）的 L2 真值源都是 `IMultiLevelCache`；详见 `Events/Outbox/CLAUDE.md` 的"多实例安全"小节。
- **`Permissions` 角色版本号**（#7）：`IPermissionCache.InvalidateAllAsync` 用 `IAtomicCounter.IncrementAsync` 替换"读-改-写"version stamp 竞态，5s L1 cache + 原子版本号双重保险。
- **`Permissions` 业务缓存**：从 `IDistributedCache` 解耦到 `IMultiLevelCache`，业务项目可注入自定义实现（.NET 8+ HybridCache / Memcached / 自建多级链）。

## 已知陷阱

1. **不要在分布式锁 ownership 检查路径用 `GetOrSetAsync`**（PR #88 真 bug）：见设计决策"GetAsync 纯读契约"。
2. **`TrySetAsync` 的 `static` 锁不是银弹**：仅保护同进程多线程 SETNX；跨进程要靠 L2 真值源（Redis SETNX 原子）。
3. **`DistributedAtomicCounter` 不是真原子**：多副本部署必须替换；生产 Redis 实现走 `INCR` 命令。
4. **`L1Duration = 0` / `L2Duration = 0` 短路**：`MultiLevelCache.TrySetAsync` 在 `L2Duration <= 0` 时直接返回 `false`（避免空 set 占用内存）；同样 `SetL1` / `SetL2Async` 也会跳过对应层。

## 测试覆盖

- `tests/10E0.Core.Tests/Caching/DefaultCachingImplementationsTests.cs` — L1 命中 / L2 命中回填 L1 / L1+L2 miss 调 factory / SETNX 互斥 / Remove 双清 / GetAsync 纯读（不调 factory）
- `tests/10E0.Core.Tests/Events/Outbox/TestFakes/` — 并发测试 Fake：
  - `InMemoryDistributedCache` + 自测 — 进程内 `IDistributedCache` 实现
  - `L1L2CacheForTest` — L1+L2 双层 fake，让 Outbox 真实并发测试无需 Redis
  - `L2AtomicCounterForTest` — 进程内 `IAtomicCounter` fake