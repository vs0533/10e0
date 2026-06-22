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
                // issue #93：改调 AggregateRoot.RaiseInternal (internal + InternalsVisibleTo)，
                // 替换之前的反射调 protected Raise。业务方法应继续走 protected Raise；框架级
                // 入口仅用于 EntityService.BeforeSaveAsync 等聚合外部触发场景。
                DemoEventTrigger.RaiseCreated(demo);
                return Task.CompletedTask;
            }
        }, ct);
        return demo.Id;
    }
}

// issue #93 修复：之前用 BindingFlags.NonPublic 反射调 protected Raise，签名变更
// 会让反射静默运行时崩。改为调 AggregateRoot.RaiseInternal（internal +
// InternalsVisibleTo 暴露给 10E0.Api），零反射、IDE 可识别、签名变更编译期报错。
internal static class DemoEventTrigger
{
    public static void RaiseCreated(DemoEntity demo) =>
        demo.RaiseInternal(new DemoCreatedEvent(demo.Id, demo.Code, demo.Name, demo.OrgId));
}
