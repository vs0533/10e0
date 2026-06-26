using System.Security.Cryptography;
using System.Text;

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

    /// <summary>
    /// 防 timing attack 的兜底 dummy hash —— 启动期生成一次，全程不变。
    ///
    /// 计算方式：用全零盐 + 固定 placeholder 明文 "tene0-timing-attack-dummy"
    /// 跑一遍 PBKDF2，得到的哈希没有任何真实密码能匹配。攻击者探测该 dummy 时
    /// 走完整 PBKDF2 路径，耗时与真实用户场景一致 —— 这正是关闭 timing oracle 的关键。
    ///
    /// 静态 + readonly：进程内只算一次，多实例共享。Mock 框架可通过 IPasswordHasher.DummyHash
    /// 属性单独覆盖。
    /// </summary>
    public string DummyHash => DummyHashValue;

    private static readonly string DummyHashValue = ComputeDummyHash();

    private static string ComputeDummyHash()
    {
        var salt = new byte[SaltSize];   // 全零盐 —— 与任何真实盐都不同，保证不匹配
        var key = Rfc2898DeriveBytes.Pbkdf2(
            "tene0-timing-attack-dummy",
            salt,
            Iterations,
            Algorithm,
            KeySize);

        var buffer = new byte[1 + SaltSize + KeySize];
        buffer[0] = 1;   // version —— 与 Hash() 输出格式一致，可被 Verify() 解析
        salt.CopyTo(buffer, 1);
        key.CopyTo(buffer, 1 + SaltSize);

        return Convert.ToBase64String(buffer);
    }

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

        // #103: 避免 .ToArray() 复制 salt/storedKey —— 直接用 ReadOnlySpan 切片。
        // PBKDF2 的纯 span 重载需要 ReadOnlySpan<byte> password，password 转 UTF8 字节
        // 走 ArrayPool 租借避免每次堆分配（password 通常 <128 字节，租借开销极低）。
        var salt = buffer.AsSpan(1, SaltSize);
        var storedKey = buffer.AsSpan(1 + SaltSize, KeySize);

        var passwordBytes = System.Buffers.ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(password.Length));
        Span<byte> computed = stackalloc byte[KeySize];
        int passwordByteCount;
        try
        {
            passwordByteCount = Encoding.UTF8.GetBytes(password, passwordBytes);
            // void 重载签名：Pbkdf2(ReadOnlySpan password, ReadOnlySpan salt, Span destination, int iterations, HashAlgorithmName)
            Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes.AsSpan(0, passwordByteCount),
                salt,
                computed,
                Iterations,
                Algorithm);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(passwordBytes);
        }

        // 固定时间比较，避免 timing attack。FixedTimeEquals 接受 ReadOnlySpan<byte>，
        // 与 computed (Span) 和 storedKey (ReadOnlySpan) 都兼容，无需 .ToArray()。
        return CryptographicOperations.FixedTimeEquals(computed, storedKey);
    }
}
