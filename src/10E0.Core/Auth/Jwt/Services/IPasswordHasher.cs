namespace TenE0.Core.Auth.Jwt.Services;

/// <summary>
/// 密码哈希器。
///
/// 默认实现使用 PBKDF2（ASP.NET Core 内置算法），同样的哈希函数能被任何 ASP.NET Core 应用验证。
/// </summary>
public interface IPasswordHasher
{
    /// <summary>明文 → 哈希（含盐 + 算法标识）。</summary>
    string Hash(string password);

    /// <summary>验证明文与哈希是否匹配。</summary>
    bool Verify(string password, string hash);
}
