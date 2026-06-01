namespace TenE0.Core.Abstractions;

/// <summary>
/// 数据访问策略 — 决定当前用户在数据行过滤时是否可"绕过"所有 IEntityFilterContributor。
///
/// 引入背景：权限评估（IPermissionEvaluator 的超管短路）只覆盖 RBAC 命令检查，
/// 不会影响 EF Named Query Filter 的数据行过滤。这两个维度独立。
/// 本接口让"超管/审计员等无视行过滤"的策略可被一处定义、全局生效。
///
/// 默认实现（在 Core 注册）始终返回 false（不 bypass）。
/// Permissions 模块注册的实现会读 PermissionsOptions.SuperUserCodes，对超管返回 true。
/// </summary>
public interface IDataAccessPolicy
{
    /// <summary>true 时所有 IEntityFilterContributor 自动跳过。</summary>
    bool BypassFilters { get; }
}

/// <summary>默认实现：从不 bypass。Core 通过 TryAddScoped 注册，Permissions 模块可覆盖。</summary>
internal sealed class DefaultDataAccessPolicy : IDataAccessPolicy
{
    public bool BypassFilters => false;
}
