using TenE0.Core.Events;

namespace TenE0.Api.Events;

internal sealed record DemoCreatedEvent(string Id, string Code, string Name, string? OrgId) : IDomainEvent;

internal sealed record DemoPublishedEvent(string Id, string Code, string Name, string PublishedBy, string? OrgId) : IDomainEvent;
