using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Api.Events;
using TenE0.Core.Abstractions;
using TenE0.Core.EntityService;
using TenE0.Core.EntityService.Validators;

namespace TenE0.Api.Handlers;

internal sealed class CreateDemoCommandHandler(IDbContextFactory<DemoDbContext> dcFactory, IEntityService entitySvc)
    : ICommandHandler<CreateDemoCommand, string>
{
    public async Task<string> HandleAsync(CreateDemoCommand command, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        var demo = new DemoEntity { Name = command.Name, OrgId = command.OrgId, Salary = command.Salary };

        // BeforeSaveAsync 在 SaveChanges 之前、流水号已分配之后触发 — 此时 demo.Code 才是有效的
        await entitySvc.CreateAsync(dc, demo, new EntityWriteOptions
        {
            UniqueValidators = [Unique.Field(demo, x => x.Name)],
            FieldPermissions = command.Salary.HasValue ? DemoFieldPermissions.Map : null,
            BeforeSaveAsync = _ =>
            {
                // 业务方法应继续走 protected Raise；BeforeSaveAsync 钩子在聚合外部触发，
                // 走 internal RaiseInternal（issue #93 替代之前的 BindingFlags.NonPublic 反射）。
                demo.RaiseInternal(new DemoCreatedEvent(demo.Id, demo.Code, demo.Name, demo.OrgId));
                return Task.CompletedTask;
            }
        }, ct);
        return demo.Id;
    }
}
