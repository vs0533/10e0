# Auth/Jwt/Services/ — JWT 令牌与密码服务

## 文件说明

| 文件 | 职责 |
|------|------|
| `IJwtTokenService.cs` | 令牌服务接口 |
| `JwtTokenService.cs` | HS256 JWT 签发：生成 access token (JWT) + refresh token (opaque)。Access token 含 `sub`/`name`/`role`/`user_type` claims |
| `IPasswordHasher.cs` | 密码哈希接口 |
| `Pbkdf2PasswordHasher.cs` | PBKDF2 密码哈希实现 |

## 令牌设计

- **Access Token**：JWT 格式，HS256 签名，包含用户编码、角色、用户类型
- **Refresh Token**：opaque 字符串，SHA256 哈希后存储到 `TenE0RefreshToken` 表
- Access token 短期（默认 30 分钟），Refresh token 长期（默认 7 天）
