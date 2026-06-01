using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// </summary>
public sealed class AuditInterceptor(
    ICurrentUserContext currentUser,
    TimeProvider timeProvider) : SaveChangesInterceptor
{
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

    private void ApplyAudit(DbContext context)
    {
        var now = timeProvider.GetUtcNow();
        var actor = currentUser.IsAuthenticated ? currentUser.UserCode : null;

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
