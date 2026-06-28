using TenE0.Api.Domain;
using TenE0.Core.Abstractions;
using TenE0.Core.Permissions;
using TenE0.Core.Queries;

namespace TenE0.Api.Handlers;

// DTOs
internal sealed record CreateDemoDto(string Name, string? OrgId, decimal? Salary);
internal sealed record UpdateDemoDto(string Name, decimal? Salary);
internal sealed record CreateOrgDto(string Code, string Name, string? ParentId, string? Description, int Order);
internal sealed record MoveOrgDto(string? NewParentId);

// Queries / Commands
[RequirePermission(DemoPermissions.View)]
internal sealed record ListDemosQuery : IQuery<List<DemoView>>;

// #184 范本:分页查询走 IEntityQueryService(读侧对称),取代手写 LINQ 分页。
// PagedQuery(pageSize/page)直接复用框架的 PagedQuery;Name 是可选搜索参数。
[RequirePermission(DemoPermissions.View)]
internal sealed record PagedDemosQuery(PagedQuery Paged, string? Name) : IQuery<PagedResult<DemoView>>;

[RequirePermission(DemoPermissions.Create)]
internal sealed record CreateDemoCommand(string Name, string? OrgId, decimal? Salary) : ICommand<string>;

[RequirePermission(DemoPermissions.Update)]
internal sealed record UpdateDemoCommand(string Id, string Name, decimal? Salary) : ICommand<bool>;

[RequirePermission(DemoPermissions.Delete)]
internal sealed record DeleteDemoCommand(string Id) : ICommand<bool>;

[RequirePermission(DemoPermissions.Update)]
internal sealed record PublishDemoCommand(string Id) : ICommand<bool>;

// View / Field-permission map
internal sealed record DemoView(string Id, string Code, string Name, string? OrgId, decimal? Salary, DateTimeOffset? CreateTime);

internal static class DemoFieldPermissions
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        [nameof(DemoEntity.Salary)] = DemoPermissions.ManageSalary,
    };
}
