using System.Security.Cryptography;

namespace TenE0.Core.Auth.Jwt.Services;

/// <summary>
/// PBKDF2-SHA256 密码哈希实现。
///
/// 格式：base64( version | salt | derivedKey )
///   - version: 1 字节（当前 1，便于将来升级算法）
///   - salt:    16 字节（每个密码随机）
///   - derived: 32 字节（迭代后的密钥）
///
/// 迭代次数 100000 — OWASP 2023+ 推荐值，单次哈希约 100ms 在普通服务器上。
/// 5 年后建议升到 600000 或者换 Argon2id。
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySize);

        var buffer = new byte[1 + SaltSize + KeySize];
        buffer[0] = 1;   // version
        salt.CopyTo(buffer, 1);
        key.CopyTo(buffer, 1 + SaltSize);

        return Convert.ToBase64String(buffer);
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash))
            return false;

        byte[] buffer;
        try { buffer = Convert.FromBase64String(hash); }
        catch (FormatException) { return false; }

        if (buffer.Length != 1 + SaltSize + KeySize || buffer[0] != 1)
            return false;

        var salt = buffer.AsSpan(1, SaltSize).ToArray();
        var storedKey = buffer.AsSpan(1 + SaltSize, KeySize).ToArray();
        var computed = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySize);

        // 固定时间比较，避免 timing attack
        return CryptographicOperations.FixedTimeEquals(computed, storedKey);
    }
}
