using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.Abstractions;
using TenE0.Core.EntityService;

namespace TenE0.Api.Handlers;

internal sealed class DeleteDemoCommandHandler(IDbContextFactory<DemoDbContext> dcFactory, IEntityService entitySvc)
    : ICommandHandler<DeleteDemoCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteDemoCommand command, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        return await entitySvc.DeleteAsync(dc, new DemoEntity { Id = command.Id }, ct);
    }
}
