namespace TenE0.Core.Abstractions;

/// <summary>
/// JWT Claim 类型常量。从旧 GlobalConstans.JwtClaimTypes 迁移并收敛。
/// </summary>
public static class JwtClaims
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Role = "role";
    public const string UserType = "user_type";

    /// <summary>
    /// 角色版本号快照 claim（#7 instant permission revocation）。
    /// 值是 JSON 序列化的 <c>Dictionary&lt;string, long&gt;</c>，key 为 roleCode、value 为签发时
    /// 的 <c>TenE0Role.Version</c>。例：<c>{"editor":7,"viewer":12}</c>。
    /// </summary>
    public const string RoleVersion = "role_versions";

    /// <summary>
    /// 租户 ID claim（#11 multi-tenancy）。
    /// 业务用户在登录时被赋一个 TenantId，签发 JWT 时写入此 claim。
    /// 后续请求由 <see cref="Auth.HttpTenantContext"/> 读取并交给 EF Tenant Named Filter。
    /// 缺失时（系统账号 / 多租户关闭）— Filter 走"safe-by-default"分支隐藏所有 <see cref="IMultiTenantEntity"/> 行。
    /// </summary>
    public const string TenantId = "tenant_id";
}

/// <summary>
/// 缓存键前缀常量。
/// </summary>
public static class CacheKeys
{
    public const string UserInfo = "user_info";
}
