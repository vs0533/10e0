using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Auth.Jwt.Services;

/// <summary>
/// IJwtTokenService 默认实现 — HS256 对称签名。
///
/// 如需 RS256/ES256（公钥验证、多服务共用）：
/// - 替换 SigningCredentials 为 RsaSecurityKey / ECDsaSecurityKey
/// - 把 JwtOptions 改为 PrivateKey/PublicKey 字段
///
/// #37: 注入 <see cref="ITokenClaimNames"/> 让 claim 名（sub / name / role / user_type /
/// role_versions / tenant_id）可整体替换为不同 IdP 风格（Keycloak preferred_username /
/// Auth0 groups / SAML 自定义）。默认实现 <see cref="JwtClaimsTokenClaimNames"/> 与遗留
/// <see cref="JwtClaims"/> 常量字字对齐，向后兼容。
/// </summary>
public sealed class JwtTokenService(
    IOptions<JwtOptions> options,
    TimeProvider timeProvider,
    ITokenClaimNames claimNames) : IJwtTokenService
{
    private readonly JwtOptions _options = options.Value;
    private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(options.Value.SigningKey));

    public IssuedTokens Issue(
        string userCode,
        string displayName,
        UserType userType,
        IReadOnlyList<string> roles,
        IReadOnlyDictionary<string, long> roleVersions,
        string? tenantId = null,
        string? orgId = null)
    {
        var now = timeProvider.GetUtcNow();
        var accessExpires = now.Add(_options.AccessTokenLifetime);
        var refreshExpires = now.Add(_options.RefreshTokenLifetime);

        // ----- Access Token (JWT) -----
        // #37: claim 名全部走 ITokenClaimNames，让 IdP 替换不需改 Core 源码。
        var claims = new List<Claim>
        {
            new(claimNames.Subject, userCode),
            new(claimNames.Name, displayName),
            new(claimNames.UserType, userType.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
        };
        foreach (var role in roles)
            claims.Add(new Claim(claimNames.Role, role));

        // #7: 嵌入角色版本号快照（紧凑 JSON claim）
        if (roleVersions is { Count: > 0 })
        {
            var json = System.Text.Json.JsonSerializer.Serialize(roleVersions);
            claims.Add(new Claim(claimNames.RoleVersion, json));
        }

        // #11: 租户 ID（仅在非空非空白时写入，避免 EF Filter 误比对空串）
        if (!string.IsNullOrWhiteSpace(tenantId))
            claims.Add(new Claim(claimNames.TenantId, tenantId));

        // #155: 组织节点 Id（仅在非空非空白时写入；与 tenant 正交 —— org 全局树）
        if (!string.IsNullOrWhiteSpace(orgId))
            claims.Add(new Claim(claimNames.Org, orgId));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: accessExpires.UtcDateTime,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        // ----- Refresh Token (opaque) -----
        var (refresh, refreshHash, _) = GenerateRefreshToken();

        return new IssuedTokens(
            accessToken,
            accessExpires,
            refresh,
            refreshHash,
            refreshExpires);
    }

    public (string Token, string Hash, DateTimeOffset ExpiresAt) GenerateRefreshToken()
    {
        // 32 字节随机 → base64url，约 43 字符
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Base64UrlEncoder.Encode(raw);
        var hash = HashRefreshToken(token);
        var expires = timeProvider.GetUtcNow().Add(_options.RefreshTokenLifetime);
        return (token, hash, expires);
    }

    public string HashRefreshToken(string refreshToken)
    {
        var bytes = Encoding.UTF8.GetBytes(refreshToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
