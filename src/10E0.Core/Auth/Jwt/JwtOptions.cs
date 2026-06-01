namespace TenE0.Core.Auth.Jwt;

/// <summary>
/// JWT 配置。
///
/// 生产部署建议：
/// - <see cref="SigningKey"/> 至少 32 字节随机字符串，从环境变量或密钥管理服务读取，不要硬编码
/// - <see cref="AccessTokenLifetime"/> 15-60 分钟（短，便于撤销）
/// - <see cref="RefreshTokenLifetime"/> 7-30 天（长，让用户少登录）
/// </summary>
public sealed class JwtOptions
{
    public required string Issuer { get; set; }
    public required string Audience { get; set; }
    public required string SigningKey { get; set; }

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);
}
