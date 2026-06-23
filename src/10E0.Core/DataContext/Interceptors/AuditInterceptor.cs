using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Abstractions;

namespace TenE0.Core.DataContext.Interceptors;

/// <summary>
/// SaveChanges 拦截器：统一处理时间戳填充 + 软删除转换。
///
/// 替代旧设计的两个痛点：
/// 1. BaseDataContext.OnBeforeSaving() 被注释掉，时间戳逻辑散落在 EntityServer
/// 2. Delete 操作没有自动转软删除，需要手动处理
///
/// TimeProvider 注入：测试可用 FakeTimeProvider 完全控制时间。
///
/// #95 captive-dependency 修复：注入 <see cref="IServiceProvider"/> +
/// <see cref="IHttpContextAccessor"/>（都是 Singleton）+ <see cref="TimeProvider"/>，
/// 在 SavingChanges 时通过 <c>HttpContext.RequestServices.GetService&lt;ICurrentUserContext&gt;()</c>
/// 按需解析当前请求 scope 的 ICurrentUserContext —— 避免 Singleton 拦截器钉死
/// Scoped 的 HttpCurrentUserContext。
/// </summary>
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpContextAccessor _accessor;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// DI 构造（Singleton）：<see cref="IServiceProvider"/> 是所有 DI 容器都注册的内建服务，
    /// <see cref="IHttpContextAccessor"/> 由 AddHttpContextAccessor() 注册为 Singleton，
    /// <see cref="TimeProvider"/> 由 AddTenE0Core() 注册为 Singleton。
    /// 三个都是 Singleton，拦截器作为 Singleton 注入它们不构成 captive dependency。
    /// </summary>
    public AuditInterceptor(
        IServiceProvider serviceProvider,
        IHttpContextAccessor httpContextAccessor,
        TimeProvider timeProvider)
    {
        _serviceProvider = serviceProvider;
        _accessor = httpContextAccessor;
        _timeProvider = timeProvider;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
            ApplyAudit(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
            ApplyAudit(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// 解析当前 actor：从当前请求 scope 的 HttpContext.RequestServices 取 ICurrentUserContext。
    /// 无 HTTP 上下文（启动期 Seeder / OutboxRelay 后台 Worker）→ actor = null（系统上下文）。
    /// 注意：不要回退到 _serviceProvider（root scope）拿 ICurrentUserContext，那会触发
    /// "Cannot resolve scoped service from root provider"。
    /// </summary>
    private string? ResolveActor()
    {
        var http = _accessor.HttpContext;
        if (http is null)
        {
            // 启动期 Seeder / 后台 Worker：无 HTTP 上下文，actor 留 null。
            return null;
        }
        var user = http.RequestServices.GetService<ICurrentUserContext>();
        return user is { IsAuthenticated: true } ? user.UserCode : null;
    }

    private void ApplyAudit(DbContext context)
    {
        var now = _timeProvider.GetUtcNow();
        var actor = ResolveActor();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            // 优先处理软删除（如果实体实现了 ISoftDeleteEntity 且当前状态是 Deleted）
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeleteEntity softDelete)
            {
                entry.State = EntityState.Modified;
                softDelete.IsSoftDelete = true;
                softDelete.DeleteTime = now;
                softDelete.DeleteBy = actor;
                continue;
            }

            if (entry.Entity is ITimerEntity timed)
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        timed.CreateTime ??= now;
                        timed.CreateBy ??= actor;
                        break;
                    case EntityState.Modified:
                        timed.UpdateTime = now;
                        timed.UpdateBy = actor;
                        break;
                }
            }
        }
    }
}
