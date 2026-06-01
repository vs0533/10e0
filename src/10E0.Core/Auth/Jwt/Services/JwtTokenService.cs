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
/// </summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider timeProvider) : IJwtTokenService
{
    private readonly JwtOptions _options = options.Value;
    private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(options.Value.SigningKey));

    public IssuedTokens Issue(string userCode, string displayName, UserType userType, IReadOnlyList<string> roles)
    {
        var now = timeProvider.GetUtcNow();
        var accessExpires = now.Add(_options.AccessTokenLifetime);
        var refreshExpires = now.Add(_options.RefreshTokenLifetime);

        // ----- Access Token (JWT) -----
        var claims = new List<Claim>
        {
            new(JwtClaims.Subject, userCode),
            new(JwtClaims.Name, displayName),
            new(JwtClaims.UserType, userType.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
        };
        foreach (var role in roles)
            claims.Add(new Claim(JwtClaims.Role, role));

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
