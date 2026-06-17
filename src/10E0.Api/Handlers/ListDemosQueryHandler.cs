using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.Abstractions;

namespace TenE0.Api.Handlers;

internal sealed class ListDemosQueryHandler(IDbContextFactory<DemoDbContext> dcFactory)
    : ICommandHandler<ListDemosQuery, List<DemoView>>
{
    public async Task<List<DemoView>> HandleAsync(ListDemosQuery query, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        return await dc.Demos
            .Select(d => new DemoView(d.Id, d.Code, d.Name, d.OrgId, d.Salary, d.CreateTime))
            .ToListAsync(ct);
    }
}
