# Auth/ — 当前用户上下文实现

提供 `ICurrentUserContext` 的两种实现，覆盖 HTTP 请求和非 HTTP 场景。

## 文件说明

| 文件 | 职责 |
|------|------|
| `HttpCurrentUserContext.cs` | **HTTP 场景**：从 `HttpContext.User.ClaimsPrincipal` 读取 JWT claims，零 I/O |
| `AmbientCurrentUserContext.cs` | **非 HTTP 场景**：基于 `AsyncLocal` 的上下文，用于后台任务、测试。通过 `Impersonate(userCode)` 设置 |

## 设计决策

- **对比旧 `E0Context.CurrentUser`**：旧版用 Lazy 属性 + `.Result` 同步加载，有死锁风险。新版同步属性只读 Claims，异步方法走缓存
- **`AmbientCurrentUserContext`** 使用 `AsyncLocal<ClaimsPrincipal>`，在 `using` 块内有效，离开自动恢复

## 子目录

| 目录 | 职责 |
|------|------|
| `Jwt/` | JWT 认证全流程（令牌签发、密码哈希、登录/刷新/登出命令） |
