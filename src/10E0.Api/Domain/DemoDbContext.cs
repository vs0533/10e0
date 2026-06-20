using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;
using TenE0.Core.DataContext;
using TenE0.Core.DynamicFilters;
using TenE0.Core.Permissions.DataFilter;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Api.Domain;

/// <summary>
/// 业务 DbContext — 继承框架的 TenE0SystemDbContext 后，框架全部表自动接入。
/// 这里只声明业务自己的 Demos 表 + 取 CurrentOrgId 的便捷属性。
/// </summary>
internal sealed class DemoDbContext(
    DbContextOptions<DemoDbContext> options,
    ICurrentUserContext currentUser,
    IDataAccessPolicy accessPolicy,
    IHttpContextAccessor httpContextAccessor,
    IEnumerable<IEntityFilterContributor> filters,
    IDynamicFilterProvider dynamicFilterProvider,
    ITenantContext tenantContext)
    : TenE0SystemDbContext<AppUser, TenE0Role>(options, currentUser, accessPolicy, filters, dynamicFilterProvider, tenantContext)
{
    public string? CurrentOrgId { get; } =
        httpContextAccessor.HttpContext?.User?.FindFirstValue("org");

    public DbSet<DemoEntity> Demos => Set<DemoEntity>();
}

/// <summary>
/// 数据行过滤器：按 Org 隔离。
/// </summary>
internal sealed class DemoOrgScopedFilter : EntityFilterContributor<DemoEntity>
{
    protected override Expression<Func<DemoEntity, bool>>? Build(BaseDataContext context)
    {
        var dc = (DemoDbContext)context;
        return entity =>
            dc.BypassFilters
            || !dc.IsAuthenticated
            || entity.OrgId == null
            || entity.OrgId == dc.CurrentOrgId;
    }
}
