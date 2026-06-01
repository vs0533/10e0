using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;
using TenE0.Core.DynamicFilters;
using TenE0.Core.Permissions.DataFilter;

namespace TenE0.Core.DataContext;

/// <summary>
/// DbContext 基类。
///
/// 职责：
/// - 自动注册 Named Query Filter "SoftDelete"（实现 ISoftDeleteEntity 的实体）
/// - 自动注册 Named Query Filter "DataPrivilege:*"（IEntityFilterContributor 实现）
/// - 暴露 CurrentUserCode/CurrentRoleIds 等只读属性，供过滤表达式动态引用
///
/// 与旧 BaseDataContext 的差异：
/// - 不再含 OnBeforeSaving（已移到 AuditInterceptor）
/// - 不再含 DataInit（已移到 DatabaseInitializerService）
/// - 不持有 IMediator/Logger
/// - 通过构造函数注入 ICurrentUserContext + IEntityFilterContributor（EF Core 10 DI 支持）
/// </summary>
public abstract class BaseDataContext(
    DbContextOptions options,
    ICurrentUserContext currentUser,
    IDataAccessPolicy accessPolicy,
    IEnumerable<IEntityFilterContributor> filterContributors,
    IDynamicFilterProvider dynamicFilterProvider) : DbContext(options)
{
    // ------------------------------------------------------------
    // 暴露给过滤表达式引用的运行时属性 — EF 会在每次查询时读取并参数化
    // ------------------------------------------------------------

    /// <summary>当前用户 code。未登录返回 null。</summary>
    public string? CurrentUserCode => currentUser.UserCode;

    /// <summary>当前用户角色 ID 数组。未登录返回空数组（不是 null，便于 EF Contains 翻译）。</summary>
    public string[] CurrentRoleIds { get; } = currentUser.RoleIds.ToArray();

    /// <summary>当前用户所属组织 ID 数组。默认空数组。</summary>
    public string[] CurrentOrgIds { get; set; } = [];

    /// <summary>是否已认证。</summary>
    public bool IsAuthenticated => currentUser.IsAuthenticated;

    /// <summary>
    /// 当前请求是否应绕过所有数据行过滤器（如超管）。
    /// IEntityFilterContributor 的表达式可在最前面 OR 上 dc.BypassFilters 来短路过滤。
    /// </summary>
    public bool BypassFilters { get; } = accessPolicy.BypassFilters;

    // ------------------------------------------------------------
    // 模型构建
    // ------------------------------------------------------------

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var contributorsByEntity = filterContributors
            .GroupBy(c => c.EntityType)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            // ---- 软删除过滤器 ----
            if (typeof(ISoftDeleteEntity).IsAssignableFrom(entityType.ClrType))
            {
                var parameter = Expression.Parameter(entityType.ClrType, "e");
                var property = Expression.Property(parameter, nameof(ISoftDeleteEntity.IsSoftDelete));
                var condition = Expression.Equal(property, Expression.Constant(false));
                entityType.SetQueryFilter("SoftDelete", Expression.Lambda(condition, parameter));
            }

            // ---- 数据权限过滤器（每个贡献者一个命名过滤器）----
            if (contributorsByEntity.TryGetValue(entityType.ClrType, out var contributors))
            {
                foreach (var contributor in contributors)
                {
                    var filter = contributor.BuildFilter(this);
                    if (filter is null) continue;

                    var name = $"DataPrivilege:{contributor.GetType().Name}";
                    entityType.SetQueryFilter(name, filter);
                }
            }
        }

        // ---- 动态数据过滤器（从数据库加载的规则）----
        dynamicFilterProvider.ApplyDynamicFilters(modelBuilder, this);
    }
}
