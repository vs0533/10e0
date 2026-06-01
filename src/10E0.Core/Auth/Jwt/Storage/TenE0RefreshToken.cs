using TenE0.Core.Entities;

namespace TenE0.Core.Auth.Jwt.Storage;

/// <summary>
/// Refresh Token 持久化记录。
///
/// 安全设计要点：
/// - 存的是 token 的 SHA-256 哈希值，不是原始 token（DB 泄露不暴露）
/// - 每次刷新都旋转（Rotate）：旧 token 标记 RevokedAt，生成新 token，<see cref="ReplacedByTokenHash"/> 串成链
/// - 检测到已 revoked 的 token 再次使用 → 强信号：token 泄露，撤销整条链
/// - Logout = 单条标记 revoked；强制下线一个用户 = 标记该用户所有 active token
/// </summary>
public sealed class TenE0RefreshToken : AuditedEntity
{
    /// <summary>token 的 SHA-256 (Base64) 哈希值，作为查找索引。</summary>
    public required string TokenHash { get; set; }

    /// <summary>归属用户。</summary>
    public required string UserCode { get; set; }

    /// <summary>过期时间。</summary>
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>撤销时间（null = 仍有效）。</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>被哪个 token 替代（链式追踪，便于检测 token 重放）。</summary>
    public string? ReplacedByTokenHash { get; set; }

    /// <summary>来源 IP（可选，审计用）。</summary>
    public string? CreatedByIp { get; set; }

    /// <summary>当前 token 是否仍可用。</summary>
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;
}
