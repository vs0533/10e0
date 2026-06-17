using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.Abstractions;
using TenE0.Core.EntityService;
using TenE0.Core.EntityService.Validators;

namespace TenE0.Api.Handlers;

internal sealed class UpdateDemoCommandHandler(IDbContextFactory<DemoDbContext> dcFactory, IEntityService entitySvc)
    : ICommandHandler<UpdateDemoCommand, bool>
{
    public async Task<bool> HandleAsync(UpdateDemoCommand command, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        var demo = new DemoEntity { Id = command.Id, Name = command.Name, Salary = command.Salary };

        // 客户端实际提交了哪些字段：演示场景下 Name 总改；Salary 仅在提供值时改
        var posted = new HashSet<string> { nameof(DemoEntity.Name) };
        if (command.Salary.HasValue) posted.Add(nameof(DemoEntity.Salary));

        return await entitySvc.UpdateAsync(dc, demo, new EntityWriteOptions
        {
            PostedProperties = posted,
            UniqueValidators = [Unique.Field(demo, x => x.Name)],
            FieldPermissions = DemoFieldPermissions.Map,
        }, ct);
    }
}
