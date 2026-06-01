# Auth/Jwt/ — JWT 认证核心

JWT 令牌签发与验证的配置。

## 文件说明

| 文件 | 职责 |
|------|------|
| `JwtOptions.cs` | JWT 配置 POCO：`Issuer`、`Audience`、`SigningKey`、`AccessTokenLifetime`、`RefreshTokenLifetime` |

## 子目录

| 目录 | 职责 |
|------|------|
| `Commands/` | 登录/刷新/登出的 CQRS 命令与处理器 |
| `Services/` | JWT 令牌服务、密码哈希服务 |
| `Storage/` | 用户/角色/刷新令牌实体 + EF 映射 |

## 配置方式

通过 `AddTenE0JwtAuth<TUser>()` 扩展方法注册，读取 `appsettings.json` 中的 `Jwt` 配置节。
