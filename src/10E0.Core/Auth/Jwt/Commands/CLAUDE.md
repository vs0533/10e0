# Auth/Jwt/Commands/ — 认证命令与处理器

登录/刷新/登出的 CQRS 命令定义和处理逻辑。

## 文件说明

| 文件 | 职责 |
|------|------|
| `AuthCommands.cs` | 命令 record 定义：`LoginCommand`、`RefreshTokenCommand`、`LogoutCommand` |
| `LoginCommandHandler.cs` | 登录处理：验证用户名密码（PBKDF2）、查角色、签发令牌、存 RefreshToken 哈希。**防计时攻击**（无论用户是否存在都执行哈希） |
| `RefreshTokenCommandHandler.cs` | 刷新处理：验证 opaque refresh token 与存储的 SHA256 哈希匹配，签发新令牌对 |
| `LogoutCommandHandler.cs` | 登出处理：撤销 refresh token |

## 安全设计

- 密码验证使用 PBKDF2（`Pbkdf2PasswordHasher`）
- Refresh token 存储 SHA256 哈希，不存明文
- 登录时无论用户是否存在都执行密码哈希运算，防止计时攻击
- Refresh token 一次性使用，刷新后旧 token 立即失效
