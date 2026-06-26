# Security 模块

安全防刷三件套（issue #162）：限流 + 登录失败锁定 + 验证码。三个子模块各自独立、可单独启停。

## 子模块

### RateLimiting/
基于 .NET 10 内置 `RateLimiter`（`Microsoft.AspNetCore.RateLimiting`）。**不引入** 第三方限流库。
- `RateLimitOptions` / `RateLimitRule` / `PartitionKind`：声明式规则 + 分区维度（Ip / User / IpAndEndpoint / UserAndEndpoint）。
- `PartitionPolicyProvider`：最长前缀匹配选规则 + 构造 `RateLimitPartition<string>`（`System.Threading.RateLimiting`）。
- `RateLimitResponseWriter`：429 → 统一 `ApiResult<T>` + `Retry-After`。
- `RateLimitingExtensions`：`AddTenE0RateLimiting` + `UseTenE0RateLimiting`（pipeline 必须在 UseAuthentication 之后）。

### LoginProtection/
- `LoginProtectionOptions`：阈值 / 锁定时长 / 滑动窗口。
- `ILoginAttemptStore` / `InMemoryLoginAttemptStore`：存储抽象 + 进程内默认实现（多副本需 Replace 为 Redis `INCR`）。
- `LoginProtector`：核心逻辑（EnsureNotLocked / RecordFailure / RecordSuccess）。
- `AccountLockedException`：映射为 423 + `AUTH_LOCKED`。

### Captcha/
- `ICaptchaProvider` / `CaptchaResult` / `CaptchaKind`：验证码抽象。
- `ImageCaptchaProvider`：图形验证码（ImageSharp，复用 Files 模块依赖）。
- `SliderCaptchaProvider`：滑块验证码。
- `CaptchaStore`：`IDistributedCache` 存储，一次性消费防重放。
- `CaptchaOptions` / `CaptchaTrigger`：触发策略（Disabled / Always / AfterFailures）。

## 关键决策

1. **限流用 .NET 10 内置**，不引入 `AspNetCoreRateLimit`（已废弃维护）。
2. **`.NET 10` 端点扩展改名**：`EnableRateLimiting` → `RequireRateLimiting`（本次接入用新 API）。
3. **验证码存储用 `IDistributedCache` 而非 `IMultiLevelCache`**：验证码需要一次性消费，单层避免 L1/L2 删除时机不一致的重放窗口。
4. **`RateLimitPartition` 在 `System.Threading.RateLimiting` 命名空间**，`OnRejectedContext` / `AddRateLimiter` 在 `Microsoft.AspNetCore.RateLimiting`。
5. **多副本 race 警告**：内置 limiter 与 `InMemoryLoginAttemptStore` 都是进程内，多副本需外层网关限流 / Redis 计数。
6. **三模块默认全关**：`TenE0Options` 三个 opt-in 开关，避免强制引入开销；demo Program.cs 显式 opt-in。

## 测试

- Core.Tests/Security/：分区策略 + 登录锁定 + 验证码生成/校验 + DI 注册（约 50 个用例）。
- Api.Tests/CaptchaEndpointsAcceptanceTests：`/captcha/image` / `/captcha/slider` 端到端。
- LoginCommandHandlerTests 新增锁定集成用例（锁定抛异常 / 失败计数）。
- ApiErrorMapperAcceptanceTests 新增 `AccountLockedException → 423` 用例。
