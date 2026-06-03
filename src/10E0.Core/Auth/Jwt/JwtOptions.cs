namespace TenE0.Core.Auth.Jwt;

/// <summary>
/// JWT 配置。
///
/// 生产部署建议：
/// - <see cref="SigningKey"/> 至少 32 字节随机字符串，从环境变量或密钥管理服务读取，不要硬编码
/// - <see cref="AccessTokenLifetime"/> 15-60 分钟（短，便于撤销）
/// - <see cref="RefreshTokenLifetime"/> 7-30 天（长，让用户少登录）
/// - <see cref="RefreshTokenRotationEnabled"/> 强烈建议保持 true（OWASP 推荐）。
///   关闭后会回退到非轮换模式：旧 refresh token 不过期，窃取后仍可长期复用。
/// </summary>
public sealed class JwtOptions
{
    public required string Issuer { get; set; }
    public required string Audience { get; set; }
    public required string SigningKey { get; set; }

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// 是否启用 refresh token 旋转（OWASP 推荐）。
    /// true：每次 refresh 成功后旧 token 立即失效，检测到重放则撤销该用户所有 token
    /// false：refresh token 不轮换（不推荐，仅用于特殊兼容场景）
    /// </summary>
    public bool RefreshTokenRotationEnabled { get; set; } = true;

    /// <summary>
    /// 滑动过期：成功 refresh 时新 token 的过期时间是否刷新为 now + RefreshTokenLifetime。
    /// 仅在 <see cref="RefreshTokenRotationEnabled"/> = true 时生效。
    /// </summary>
    public bool SlidingRefreshExpiration { get; set; } = true;
}
