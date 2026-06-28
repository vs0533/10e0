using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.Abstractions;
using TenE0.Core.EntityService;
using TenE0.Core.Queries;

namespace TenE0.Api.Handlers;

/// <summary>
/// #184 范本:分页查询 Handler,走 <see cref="IEntityQueryService"/>。
///
/// 对比 <see cref="ListDemosQueryHandler"/> 的手写 LINQ —— 这里把分页 / 排序 / 筛选
/// 委托给框架读服务,Handler 只关心"查什么 + 投影成什么"。
/// 行级权限 / 软删除 / 租户过滤器由 EF 自动附加,无需手写 Where。
/// </summary>
internal sealed class PagedDemosQueryHandler(
    IDbContextFactory<DemoDbContext> dcFactory,
    IEntityQueryService querySvc)
    : ICommandHandler<PagedDemosQuery, PagedResult<DemoView>>
{
    public async Task<PagedResult<DemoView>> HandleAsync(PagedDemosQuery query, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);

        // 读选项:可选按 Name 模糊搜 + 默认按 CreateTime 降序。
        // Name 是字符串字段,白名单校验通过(实体真实属性);CreateTime 由 TimedEntity 提供。
        var options = new EntityReadOptions
        {
            Filters = query.Name is { } name
                ? [new ReadFilter(nameof(DemoEntity.Name), ReadOperator.Contains, name)]
                : null,
            OrderBy = [new ReadOrderBy("CreateTime", Descending: true)],
        };

        return await querySvc.PagedAsync<DemoEntity, DemoView>(
            dc,
            query.Paged,
            selector: d => new DemoView(d.Id, d.Code, d.Name, d.OrgId, d.Salary, d.CreateTime),
            options: options,
            cancellationToken: ct);
    }
}
