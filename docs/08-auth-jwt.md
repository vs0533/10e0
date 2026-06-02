# JWT 认证

10E0 内置完整的 JWT 令牌认证体系：Access Token + Refresh Token 双令牌机制，搭配密码哈希、令牌旋转、重放检测等安全能力。

---

## 令牌设计

### Access Token（JWT）

HS256 对称签名的 JWT，默认有效期 30 分钟。Claims 结构：

| Claim | 类型 | 说明 |
|-------|------|------|
| `sub` | `JwtClaims.Subject` | 用户编码（UserCode），全局唯一标识 |
| `name` | `JwtClaims.Name` | 显示名称 |
| `user_type` | `JwtClaims.UserType` | `Person`（个人）或 `Unit`（单位） |
| `role` | `JwtClaims.Role` | 用户所属角色，可多个 |
| `jti` | 标准 JWT | 唯一令牌 ID |
| `iat` | 标准 JWT | 签发时间戳 |

### Refresh Token（Opaque）

32 字节随机数 → base64url 编码（约 43 字符），非 JWT，不携带任何负载信息。**数据库中只存储 SHA-256 哈希值**，原始令牌仅在签发时返回给客户端一次。默认有效期 14 天。

```
原始 token:  A7xK9m...（32B random → base64url）
DB 存储:     base64(SHA-256(token))   ← 泄露 DB 无法还原原始令牌
```

---

## 一行注册

通过 `AddTenE0Identity<TUser, TContext>()` 零摩擦配置，内部自动注册 JWT、权限、组织树等完整模块：

```csharp
// Program.cs — 使用框架内置 TenE0User（不扩展用户字段）
builder.Services.AddTenE0Identity<DemoDbContext>(opt =>
{
    opt.Jwt.Issuer = "10E0.Api";
    opt.Jwt.Audience = "10E0.Api";
    opt.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"]
        ?? "dev-secret-CHANGE-ME-in-production-must-be-at-least-32-bytes-long";
    opt.Jwt.AccessTokenLifetime = TimeSpan.FromMinutes(30);
    opt.Jwt.RefreshTokenLifetime = TimeSpan.FromDays(14);
    opt.Permissions.SuperUserRoles.Add("super_admin");
});
```

也可单独注册 JWT 模块（不包含权限/组织）：

```csharp
builder.Services.AddTenE0JwtAuth<TUser, TContext>(jwt =>
{
    jwt.Issuer = "10E0.Api";
    jwt.Audience = "10E0.Api";
    jwt.SigningKey = "...";
});
```

### JwtOptions 配置项

| 属性 | 默认值 | 说明 |
|------|--------|------|
| `Issuer` | `"10E0"` | JWT 签发方 |
| `Audience` | `"10E0"` | JWT 受众 |
| `SigningKey` | 必填 | HS256 签名密钥，至少 32 字节 |
| `AccessTokenLifetime` | 30 分钟 | 短生命周期，便于撤销 |
| `RefreshTokenLifetime` | 14 天 | 长期令牌，减少重新登录 |

> **生产环境**：`SigningKey` 必须从环境变量或密钥管理服务读取，禁止硬编码。

---

## API 端点

### POST `/auth/login`

登录认证。

**请求**：

```json
{
  "userCode": "admin",
  "password": "111111"
}
```

**成功响应**（200）：

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "accessTokenExpiresAt": "2026-06-02T12:00:00+08:00",
  "refreshToken": "A7xK9mP2qR5vW8yB...",
  "refreshTokenExpiresAt": "2026-06-16T11:30:00+08:00",
  "userCode": "admin",
  "displayName": "系统管理员",
  "roles": ["super_admin", "manager"]
}
```

**错误响应**（401）：

```json
{ "error": { "message": "用户名或密码错误", "code": "AUTH_INVALID" } }
```

### POST `/auth/refresh`

刷新令牌对。客户端 Access Token 临期时主动调用，用 Refresh Token 换取新令牌对。

**请求**：

```json
{ "refreshToken": "A7xK9mP2qR5vW8yB..." }
```

**响应**：与登录成功一致，返回新 `accessToken` + 新 `refreshToken`。

> 刷新后旧 Refresh Token **立即失效**（令牌旋转）。客户端需丢弃旧令牌，用新令牌对替换。

### POST `/auth/logout`

登出，撤销当前 Refresh Token。

**请求**：

```json
{ "refreshToken": "A7xK9mP2qR5vW8yB..." }
```

**响应**（200）：

```json
{ "ok": true }
```

### GET `/whoami`

读取当前登录用户身份（需在请求头带 `Authorization: Bearer <accessToken>`）。

**响应**（200）：

```json
{
  "user": "admin",
  "authenticated": true,
  "roles": ["super_admin", "manager"]
}
```

---

## 登录流程

```
用户提交 userCode + password
       │
       ▼
查询用户（WHERE UserCode = cmd.UserCode）
       │
       ├─→ 用户不存在 ─── 仍执行 PBKDF2 验证（防 timing attack）
       │                    → 返回 AUTH_INVALID
       │
       ▼
PBKDF2-SHA256 验证密码哈希
       │
       ├─→ 不匹配 → 返回 AUTH_INVALID
       │
       ▼
检查 IsActive
       │
       ├─→ 已禁用 → 返回 AUTH_DISABLED
       │
       ▼
查用户角色（TenE0UserRole 表）
       │
       ▼
签发令牌对：
  - Access Token：JWT（sub/name/user_type/role 写入 claims）
  - Refresh Token：32B 随机 → base64url → SHA-256 哈希存 DB
       │
       ▼
返回 AuthResult（含两个令牌、过期时间、用户信息）
```

**核心安全措施**：`Pbkdf2PasswordHasher.Verify()` 内部使用 `CryptographicOperations.FixedTimeEquals()` 进行恒定时间比较，且用户不存在时也会运行一次 Verify（耗时可忽略的 dummy 分支），杜绝按响应时间探测有效账号。

---

## 刷新流程

```
客户端提交 refreshToken
       │
       ▼
计算 SHA-256 → 查 TenE0RefreshToken 表
       │
       ├─→ 未找到 → 返回 TOKEN_INVALID
       │
       ▼
检查 RevokedAt
       │
       ├─→ 已撤销 → 🚨 重放检测！
       │            标记该用户全部活跃 token 为 revoked
       │            返回 TOKEN_REVOKED（需重新登录）
       │
       ▼
检查 ExpiresAt
       │
       ├─→ 已过期 → 返回 TOKEN_EXPIRED
       │
       ▼
验证用户仍存在且 IsActive
       │
       ├─→ 账号不可用 → 返回 AUTH_DISABLED
       │
       ▼
令牌旋转（Token Rotation）：
  - 旧 token 标记 RevokedAt + ReplacedByTokenHash
  - 签发新令牌对（新 JWT + 新 refresh token）
  - 新 refresh token 哈希写入 DB
       │
       ▼
返回新 AuthResult
```

**ReplacedByTokenHash** 字段串联新旧令牌，形成可追溯的替换链。一旦检测到已撤销的令牌被重放，说明令牌可能泄露，系统将撤销该用户全部活跃 token 并强制重新登录。

---

## 登出流程

```
客户端提交 refreshToken
       │
       ▼
计算 SHA-256 → 查 TenE0RefreshToken
       │
       ├─→ 未找到或已撤销 → 幂等返回 ok
       │
       ▼
标记 RevokedAt = now，保存
       │
       ▼
返回 { ok: true }
```

---

## 安全机制一览

| 机制 | 说明 | 实现位置 |
|------|------|----------|
| **防计时攻击** | 用户不存在时也执行密码哈希验证，让"有效用户"和"无效用户"分支耗一致；密码比较使用 `FixedTimeEquals` | `LoginCommandHandler` + `Pbkdf2PasswordHasher` |
| **哈希存储** | Refresh Token 存 SHA-256(Base64)，不存明文；密码存 PBKDF2 摘要 | `TenE0RefreshToken.TokenHash` / `TenE0User.PasswordHash` |
| **令牌旋转** | 每次刷新都签发新 token 对，旧 token 立即撤销 | `RefreshTokenCommandHandler` |
| **重放检测** | 已撤销的 refresh token 再次出现 → 撤销用户全部 token + 告警日志 | `RefreshTokenCommandHandler` |
| **PBKDF2-SHA256** | 10 万次迭代，16 字节随机盐，32 字节派生密钥，格式 `base64(version\|salt\|key)` | `Pbkdf2PasswordHasher` |
| **短期 Access Token** | 默认 30 分钟，即使泄露影响窗口有限 | `JwtOptions.AccessTokenLifetime` |
| **HS256 签名** | 对称密钥签名，验证 Issuer/Audience/Lifetime | `JwtTokenService` |

---

## 扩展用户实体

业务方可继承 `TenE0User` 添加自定义字段，框架的登录/刷新/JWT 流程自动使用扩展类型：

```csharp
using TenE0.Core.Auth.Jwt.Storage;

// Program.cs（或独立文件）中定义
internal sealed class AppUser : TenE0User
{
    public string? Avatar { get; set; }
    public string? Department { get; set; }
    public DateOnly? Birthday { get; set; }
}
```

注册时指定扩展类型：

```csharp
builder.Services.AddTenE0Identity<AppUser, DemoDbContext>(opt =>
{
    opt.Jwt.SigningKey = "...";
});
```

EF Core 使用 TPH（Table-Per-Hierarchy）策略，`AppUser` 与 `TenE0User` 共享同一张表，通过 Discriminator 列区分。

---

## ICurrentUserContext

框架提供两种当前用户上下文实现：

### HttpCurrentUserContext（HTTP 场景）

HTTP 请求场景下默认注册。直接从 `HttpContext.User.ClaimsPrincipal` 读取 JWT claims，**零 I/O、零阻塞**：

```csharp
public class SomeService(ICurrentUserContext user)
{
    public string? WhoAmI()
    {
        // 同步属性直接从 ClaimsPrincipal 读，无 I/O
        var code = user.UserCode;       // JWT sub claim
        var type = user.UserType;       // JWT user_type claim
        var roles = user.RoleIds;       // JWT role claims
        var authed = user.IsAuthenticated;

        return code;
    }

    public async Task<UserInfo?> GetDetails(CancellationToken ct)
    {
        // 仅当需要"用户详情对象"时才异步加载（走分布式缓存 + DB）
        return await user.GetUserInfoAsync(ct) as UserInfo;
    }
}
```

### AmbientCurrentUserContext（非 HTTP 场景）

基于 `AsyncLocal<ClaimsPrincipal>`，跨 `await` 流转且各异步任务互不污染。用于后台任务、消息队列消费者、控制台应用、单元测试等没有 `HttpContext` 的场景：

```csharp
public class CourseExpireJob(ICurrentUserContextSetter setter, ICommandDispatcher dispatcher)
{
    public async Task Run()
    {
        var principal = AmbientCurrentUserContext.BuildPrincipal("system", ["super_admin"]);
        using (setter.Impersonate(principal))
        {
            // 此 using 块内所有命令的 ICurrentUserContext 都返回 system 用户
            await dispatcher.SendAsync(new ExpireCoursesCommand(), CancellationToken.None);
        }
    }
}
```

### 对比旧版

| 新版 | 旧 E0Context.CurrentUser |
|------|--------------------------|
| 同步属性零 I/O | `Lazy<T>` + `.Result` 同步阻塞，有死锁风险 |
| `GetUserInfoAsync()` 异步 + 缓存 | 每次访问都走 DB |
| 独立注入，无循环引用 | 与 AuthFactory/E0Context 紧耦合 |
| 单类两种上下文（HTTP / Ambient） | 工厂 + 多个 Auth 子类 |

---

## 数据表

| 表 | 说明 |
|----|------|
| `TenE0Users` | 用户表（含 TPH 继承的扩展字段） |
| `TenE0Roles` | 角色表 |
| `TenE0UserRoles` | 用户-角色多对多 |
| `TenE0RefreshTokens` | Refresh Token 持久化（TokenHash/UserCode/RevokedAt/ReplacedByTokenHash） |

> Access Token（JWT）不自含撤销能力，依赖短期过期控制；Refresh Token 持久化到表，支持撤销、旋转、重放检测。
