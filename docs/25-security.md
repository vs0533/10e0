# 25 — 安全防刷三件套（限流 + 登录失败锁定 + 验证码）

企业级框架的认证端点不能裸奔。`/auth/login` 若无限流 / 无失败锁定 / 无验证码，单 IP 即可无限暴力撞库。本模块（issue #162）一次性补齐三道防线，全部基于 .NET 10 内置基础设施，不引入第三方限流库。

代码位于 `TenE0.Core.Security`（`RateLimiting` / `LoginProtection` / `Captcha` 三个子模块），DI / pipeline 扩展对齐 `AddTenE0Xxx` / `UseTenE0Xxx` 既有风格。

---

## 架构

```
客户端 ──▶ 限流（RateLimiter）          # 第一道：IP/User 配额，超限 429
        │
        ▼
   /auth/login 端点
        │
        ├─▶ 验证码校验（ICaptchaProvider）  # 第二道：Always / AfterFailures 触发
        │
        ▼
   LoginCommandHandler
        ├─▶ LoginProtector.EnsureNotLocked  # 第三道：锁定中直接拒
        ├─▶ 密码校验
        │     ├─ 失败 → RecordFailure（计数 + 阈值锁定）
        │     └─ 成功 → RecordSuccess（清零）
        └─▶ 发 token
```

三道防线各自独立、可单独启停（`TenE0Options` 三个开关），默认全关（按需启用），避免强制引入不必要的开销。

---

## 1. 限流（RateLimiting）

基于 .NET 10 内置 `RateLimiter`（`Microsoft.AspNetCore.RateLimiting`），**不引入** 已废弃维护的 `AspNetCoreRateLimit` 第三方库。

### 分区策略

按 `PartitionKind` 多维度分区：

| Kind | key 格式 | 用途 |
|------|----------|------|
| `Ip` | `ip:{ip}` | 防匿名刷（默认全局） |
| `User` | `user:{name}`（未登录回退 `user:anon\|{ip}`） | 按已认证用户配额 |
| `IpAndEndpoint` | `ip-ep:{ip}\|{path}` | 同 IP 不同端点独立配额 |
| `UserAndEndpoint` | `user-ep:{user}\|{path}` | 同用户不同端点独立配额 |

### 默认规则（`RateLimitOptions.DefaultRules`）

- **全局**：每 IP 每分钟 100 次
- **`/auth/login`**：每 IP 每分钟 10 次（防撞库）
- **`/auth/refresh`**：每用户每分钟 5 次
- **`/captcha/image` / `/captcha/slider`**：每 IP 每分钟 30 次（防刷验证码）
- **`/files/upload`**：每用户每分钟 30 次

### 429 响应

`RateLimitResponseWriter.OnRejectedAsync` 把 429 写成统一 `ApiResult<T>` 信封（与 `TenE0ExceptionHandler` / `ForbiddenResponseWriter` 同风格），附 `Retry-After: 60` 头：

```json
{"success":false,"errorCode":"RATE_LIMITED","errorMessage":"请求过于频繁，请稍后再试。"}
```

### 接入

```csharp
// Program.cs（在 AddTenE0All 里 opt-in）
opt.RateLimiting = true;
opt.RateLimitingOptions = r =>
{
    r.GlobalRules.Add(new RateLimitRule(PartitionKind.Ip, 200, TimeSpan.FromMinutes(1)));
};

// pipeline（必须放在 UseAuthentication 之后、UseAuthorization 之前）
app.UseTenE0RateLimiting();
```

端点级显式启用：`.RequireRateLimiting(RateLimitingExtensions.PolicyName)`（.NET 10 把 `EnableRateLimiting` 改名为 `RequireRateLimiting`）。

> **多副本注意**：内置 `RateLimiter` 是进程内的。多副本部署每副本独立配额（阈值翻倍）。生产 Redis 场景需自行 `services.Configure<RateLimiterOptions>` 接入分布式 limiter，或外层网关限流。

---

## 2. 登录失败锁定（LoginProtection）

滑动窗口内累计失败次数达阈值 → 锁定账号 N 分钟；锁定期拒绝任何登录；成功登录清零。

### 配置（`LoginProtectionOptions`）

| 属性 | 默认 | 说明 |
|------|------|------|
| `LockoutEnabled` | `true` | 总开关 |
| `MaxFailedAttempts` | `5` | 滑动窗口内最大失败次数 |
| `LockoutDuration` | `15min` | 锁定时长 |
| `SlidingWindow` | `10min` | 计数滑动窗口（超出窗口的失败不计入） |

### 核心契约（`LoginProtector`）

```csharp
// LoginCommandHandler 内部已自动接入（opt-in 后）：
await protector.EnsureNotLockedAsync(userCode, ct);   // 锁定中抛 AccountLockedException
if (!passwordValid)
    await protector.RecordFailureAsync(userCode, ct); // 计数 + 触发锁定
else
    await protector.RecordSuccessAsync(userCode, ct); // 清零
```

`AccountLockedException` 由 `TenE0ExceptionHandler` 映射为 **423 Locked + `AUTH_LOCKED`**，前端可据 `LockedUntil` 提示"剩余 N 分钟自动解锁"。

### 存储抽象（`ILoginAttemptStore`）

默认 `InMemoryLoginAttemptStore`（单实例足够）。多副本部署必须 `services.Replace(...)` 切到 Redis `INCR` 实现，否则每副本独立计数、阈值翻倍绕过（注释中明确标注此约束，对应 `DistributedAtomicCounter` 同款警告风格）。

### 与 #153 系统参数集成

`MaxFailedAttempts` / `LockoutDuration` 可由业务方在 `LoginProtector` 之外用 `ISystemParameterStore` 动态覆盖（运维改值无需发版）。

---

## 3. 验证码（Captcha）

图形验证码 + 滑块验证码抽象，复用 Files 模块已引入的 `SixLabors.ImageSharp`（MIT），不引入专门验证码库。

### 抽象（`ICaptchaProvider`）

```csharp
public interface ICaptchaProvider
{
    CaptchaKind Kind { get; }
    Task<CaptchaResult> GenerateAsync(CancellationToken ct);           // 生成 + 落缓存
    Task<bool> ValidateAsync(string captchaId, string userInput, ct);  // 一次性消费
}
```

**一次性消费**：`ValidateAsync` 命中即删缓存项，防同一个验证码重放多次。存储走 `IDistributedCache`（短 TTL，默认 5 分钟），生产 Redis 自动多副本共享。

### 触发策略（`CaptchaOptions.LoginTrigger`）

| 策略 | 说明 |
|------|------|
| `Disabled`（默认） | 登录不要验证码 |
| `Always` | 每次登录都要 |
| `AfterFailures` | 同账号失败 N 次后才要（配合 `LoginProtector` 计数，默认 N=3） |

### 端点

- `GET /captcha/image` → PNG + `X-Captcha-Id` 头
- `GET /captcha/slider` → 背景图 PNG + `X-Captcha-Id` / `X-Slider-Width` / `X-Slider-Height` 头

登录端点接入：`LoginCommand` 新增可选 `CaptchaId` / `CaptchaCode` 字段，`AuthEndpoints` 按 `LoginTrigger` 决定是否强制校验。

### 替换实现

业务方可 `services.Replace(...)` 切到第三方（极验 / 阿里云 / Cloudflare Turnstile）实现，替换 OCR 防护算法而不改接入点。

---

## 快速开始

```csharp
// Program.cs
builder.Services.AddTenE0All<AppUser, DemoDbContext>(builder.Configuration, opt =>
{
    // ... 其它配置 ...

    // #162 防刷三件套（按需 opt-in）
    opt.RateLimiting = true;
    opt.LoginProtection = true;
    opt.LoginProtectionOptions = lp =>
    {
        lp.MaxFailedAttempts = 5;
        lp.LockoutDuration = TimeSpan.FromMinutes(15);
    };
    opt.Captcha = true;
    opt.CaptchaOptions = cap =>
    {
        cap.LoginTrigger = CaptchaTrigger.AfterFailures; // 失败 3 次后才要验证码
    };
});

// pipeline：限流放在 UseAuthentication 之后
app.UseAuthentication();
app.UseTenE0RateLimiting();
app.UseAuthorization();

// 路由：验证码端点
app.MapAuthEndpoints()
   .MapCaptchaEndpoints();
```

---

## 错误码（`ErrorCodes`）

| 常量 | 值 | 触发点 |
|------|----|--------|
| `AuthLocked` | `AUTH_LOCKED` | 账号锁定（423） |
| `CaptchaInvalid` | `CAPTCHA_INVALID` | 验证码错误 / 过期 |
| `CaptchaRequired` | `CAPTCHA_REQUIRED` | 需要验证码但客户端未提供 |
| `RateLimited` | `RATE_LIMITED` | 触发限流（429） |

---

## 协同点

- **#153 系统参数**：限流配额 / 锁定阈值可由运维动态调整，无需发版。
- **#152 审计日志**：锁定 / 异常登录失败写入 `IAuditLogSink`（`LoginCommandHandler` 已埋点）。
- **#155 实时推送**：账号被异地登录失败 / 被锁定时，业务方可订阅领域事件推送给用户（"您的账号在异地登录失败"）。
