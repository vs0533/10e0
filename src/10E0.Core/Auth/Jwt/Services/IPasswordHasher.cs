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

    /// <summary>
    /// 一个固定的、与任何真实密码都不匹配的预生成哈希。
    ///
    /// 用于防止 timing attack：在用户名为空时仍需调用一次 <see cref="Verify"/>，
    /// 传入此 dummy hash 既能保证 Verify 的执行路径/耗时与真实场景一致，
    /// 又能确保结果必为 false（因为没有任何明文能匹配这个固定的盐+派生密钥）。
    ///
    /// 业务侧示例（#97 防 timing attack 短路）：
    /// <code>
    /// var hashToCheck = user?.PasswordHash ?? passwordHasher.DummyHash;
    /// var verified = passwordHasher.Verify(cmd.Password, hashToCheck);
    /// </code>
    /// </summary>
    string DummyHash { get; }
}
