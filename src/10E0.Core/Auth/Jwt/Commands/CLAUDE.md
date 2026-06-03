# Auth/Jwt/Commands/ — 认证命令与处理器

登录/刷新/登出的 CQRS 命令定义和处理逻辑。

## 文件说明

| 文件 | 职责 |
|------|------|
| `AuthCommands.cs` | 命令 record 定义：`LoginCommand`、`RefreshTokenCommand`、`LogoutCommand` |
| `LoginCommandHandler.cs` | 登录处理：验证用户名密码（PBKDF2）、查角色、签发令牌、存 RefreshToken 哈希。**防计时攻击**（无论用户是否存在都执行哈希） |
| `RefreshTokenCommandHandler.cs` | 刷新处理（PR #6 OWASP rotation 模式）：<br>1. 验证 opaque refresh token 与存储 SHA256 哈希匹配<br>2. **Rotation**：同事务撤销旧 token（`RevokedReason=rotated`），写入新 token（`ReplacedByTokenHash` 形成链）<br>3. **重放检测**：已撤销 token 被复用时，撤销该用户**全部**活跃 token，标记 `RevokedReason=token_reuse_detected`<br>4. **Sliding expiration**（默认开启）：新 token `ExpiresAt = now + RefreshTokenLifetime` |
| `LogoutCommandHandler.cs` | 登出处理：撤销 refresh token |

## 安全设计

- 密码验证使用 PBKDF2（`Pbkdf2PasswordHasher`）
- Refresh token 存储 SHA256 哈希，不存明文
- 登录时无论用户是否存在都执行密码哈希运算，防止计时攻击
- **Refresh token 一次性使用 + rotation（PR #6）**：旧 token 同事务撤销（`RevokedReason=rotated`），新 token 立刻签发，`ReplacedByTokenHash` 形成链
- **重放检测（reuse detection，PR #6）**：检测到旧 revoked token 再次使用时，撤销该用户所有活跃 token（OWASP 推荐响应）
