using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.Abstractions;

namespace TenE0.Api.Handlers;

internal sealed class PublishDemoCommandHandler(IDbContextFactory<DemoDbContext> dcFactory, ICurrentUserContext currentUser, IErrs errs)
    : ICommandHandler<PublishDemoCommand, bool>
{
    public async Task<bool> HandleAsync(PublishDemoCommand command, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        var demo = await dc.Demos.FirstOrDefaultAsync(d => d.Id == command.Id, ct);
        if (demo is null)
        {
            errs.Add("Demo 不存在", code: "NOT_FOUND");
            return false;
        }

        try
        {
            demo.Publish(currentUser.UserCode ?? "anonymous");   // 触发 DemoPublishedEvent
        }
        catch (InvalidOperationException ex)
        {
            errs.Add(ex.Message, code: "INVALID_STATE");
            return false;
        }

        await dc.SaveChangesAsync(ct);   // 业务状态 + OutboxMessage 同事务原子提交
        return true;
    }
}
