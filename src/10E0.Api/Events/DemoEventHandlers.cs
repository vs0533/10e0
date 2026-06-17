using Microsoft.Extensions.Logging;
using TenE0.Core.Events;

namespace TenE0.Api.Events;

internal sealed class DemoCreatedAuditHandler(ILogger<DemoCreatedAuditHandler> logger)
    : IDomainEventHandler<DemoCreatedEvent>
{
    public Task HandleAsync(DemoCreatedEvent evt, CancellationToken ct)
    {
        logger.LogInformation("[EVENT] DemoCreated 收到：Id={Id} Code={Code} Name={Name} Org={Org}",
            evt.Id, evt.Code, evt.Name, evt.OrgId);
        return Task.CompletedTask;
    }
}

internal sealed class DemoPublishedNotificationHandler(ILogger<DemoPublishedNotificationHandler> logger)
    : IDomainEventHandler<DemoPublishedEvent>
{
    public Task HandleAsync(DemoPublishedEvent evt, CancellationToken ct)
    {
        logger.LogInformation("[EVENT] DemoPublished 收到（发通知/索引/推送等）：Id={Id} Code={Code} 由 {By} 发布",
            evt.Id, evt.Code, evt.PublishedBy);
        return Task.CompletedTask;
    }
}

internal sealed class DemoPublishedAuditHandler(ILogger<DemoPublishedAuditHandler> logger)
    : IDomainEventHandler<DemoPublishedEvent>
{
    public Task HandleAsync(DemoPublishedEvent evt, CancellationToken ct)
    {
        logger.LogInformation("[EVENT] DemoPublished 收到（写审计日志）：Id={Id} By={By} At=now",
            evt.Id, evt.PublishedBy);
        return Task.CompletedTask;
    }
}
