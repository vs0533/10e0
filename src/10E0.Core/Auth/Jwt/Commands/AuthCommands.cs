using TenE0.Core.Abstractions;

namespace TenE0.Core.Auth.Jwt.Commands;

// 登录命令 — 无权限要求（公开接口）
//
// CaptchaId / CaptchaCode（#162 验证码）：可选字段，登录端点按 CaptchaOptions.LoginTrigger
// 决定是否强制校验。Disabled 触发策略时这俩字段被忽略。
public sealed record LoginCommand(
    string UserCode,
    string Password,
    string? ClientIp = null,
    string? CaptchaId = null,
    string? CaptchaCode = null)
    : ICommand<AuthResult>;

// 刷新 token — 公开接口（携带 refresh token 即可）
public sealed record RefreshTokenCommand(string RefreshToken, string? ClientIp = null)
    : ICommand<AuthResult>;

// 登出 — 携带 refresh token 撤销
public sealed record LogoutCommand(string RefreshToken)
    : ICommand<Unit>;

/// <summary>
/// 登录/刷新成功的结果。
///
/// 客户端应当：
/// - 把 AccessToken 放 Authorization: Bearer 头每次请求时带上
/// - 把 RefreshToken 安全持久化（移动端钥匙串 / 浏览器 HttpOnly Cookie）
/// - AccessToken 临期时主动调 /auth/refresh 换新对
/// </summary>
public sealed record AuthResult(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string UserCode,
    string DisplayName,
    IReadOnlyList<string> Roles);
