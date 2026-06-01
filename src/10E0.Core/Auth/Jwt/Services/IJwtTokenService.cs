namespace TenE0.Core.Auth.Jwt.Services;

/// <summary>JWT token 服务 — 签发 access token + refresh token。</summary>
public interface IJwtTokenService
{
    /// <summary>
    /// 为指定用户签发一对新 token。
    /// </summary>
    /// <param name="userCode">用户唯一码。</param>
    /// <param name="displayName">用户显示名（写入 name claim）。</param>
    /// <param name="userType">用户类型（个人/单位）。</param>
    /// <param name="roles">用户拥有的角色 code 列表。</param>
    IssuedTokens Issue(string userCode, string displayName, TenE0.Core.Abstractions.UserType userType, IReadOnlyList<string> roles);

    /// <summary>
    /// 生成一个新的 refresh token 字符串和其 SHA-256 哈希。
    /// 调用方负责把 hash 持久化到 TenE0RefreshToken 表。
    /// </summary>
    (string Token, string Hash, DateTimeOffset ExpiresAt) GenerateRefreshToken();

    /// <summary>计算指定 refresh token 字符串的哈希值（用于 DB 查找）。</summary>
    string HashRefreshToken(string refreshToken);
}

/// <summary>签发的 token 对。</summary>
public sealed record IssuedTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    string RefreshTokenHash,
    DateTimeOffset RefreshTokenExpiresAt);
