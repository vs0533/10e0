using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Api.Events;
using TenE0.Core.Abstractions;
using TenE0.Core.EntityService;
using TenE0.Core.EntityService.Validators;
using TenE0.Core.Events;

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
                // 直接调聚合的 Raise 方法（protected） — 这里我们用反射或者把 Raise 改 public？
                // 更优雅的做法是聚合自己提供 OnCreated 方法。这里用 helper 触发事件。
                DemoEventTrigger.RaiseCreated(demo);
                return Task.CompletedTask;
            }
        }, ct);
        return demo.Id;
    }
}

// 聚合根的 Raise 是 protected，外部无法直接调用 — 这是 DDD 的封装。
// 但 EntityService 创建场景下我们需要触发事件，可在聚合内提供 internal 方法（避免暴露 Raise）。
// 这里为简洁起见用反射 trick，生产中推荐在 DemoEntity 中暴露 OnCreated 方法。
internal static class DemoEventTrigger
{
    private static readonly System.Reflection.MethodInfo RaiseMethod =
        typeof(AggregateRoot).GetMethod("Raise",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    public static void RaiseCreated(DemoEntity demo) =>
        RaiseMethod.Invoke(demo, [new DemoCreatedEvent(demo.Id, demo.Code, demo.Name, demo.OrgId)]);
}
