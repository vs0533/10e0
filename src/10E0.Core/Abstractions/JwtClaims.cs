namespace TenE0.Core.Abstractions;

/// <summary>
/// JWT Claim 类型常量。从旧 GlobalConstans.JwtClaimTypes 迁移并收敛。
///
/// #37 注：业务代码应优先注入 <see cref="ITokenClaimNames"/>，以便在不改 Core
/// 源码的前提下整体替换为 Keycloak / Auth0 / SAML 等不同 IdP 的 claim 命名。
/// 本静态类作为常量兼容层保留，值与 <see cref="DefaultTokenClaimNames"/> 完全一致。
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

    /// <summary>
    /// 组织 ID claim（#155 realtime 推送 / 行级 org 隔离）。
    /// 值为 <see cref="Organizations.TenE0Org"/> 节点的 Id（GUID-N 单值）—— 与 tenant 正交：
    /// org 是全局树，不属于任何 tenant。
    ///
    /// 登录时由 <see cref="Auth.Jwt.Commands.LoginCommandHandler{TUser,TContext}"/> 读取
    /// <see cref="Auth.Jwt.Storage.TenE0User.OrgId"/> 写入本 claim；refresh 用 DB 最新值。
    /// 消费端：
    /// - 业务行级过滤（如 Demo 的 CurrentOrgId）按本 claim 隔离数据；
    /// - 实时推送默认把连接加入 <c>org:{orgId}</c> 组，支持按组织广播。
    /// 缺失时（未绑定组织的用户 / 系统账号）— 不写入 claim，实时组不产出 org 组，过滤走"safe-by-default"。
    /// </summary>
    public const string Org = "org";
}

/// <summary>
/// 缓存键前缀常量。
///
/// #37 注：业务代码应优先注入 <see cref="ICacheKeyNamespace"/>，以支持多租户
/// namespace / 环境隔离。本静态类作为兼容层保留，值与
/// <see cref="DefaultCacheKeyNamespace.UserInfo"/> 完全一致。
/// </summary>
public static class CacheKeys
{
    public const string UserInfo = "user_info";
}
