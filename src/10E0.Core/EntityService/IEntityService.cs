using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;

namespace TenE0.Core.EntityService;

/// <summary>
/// 实体写操作服务。
///
/// 与旧 BaseEntityServer + EntityServerFactory 的差异：
/// - 单一接口替代继承体系（CreateEntity/UpdateEntity/DeleteEntity 三个子类合并为三个方法）
/// - 无状态：DbContext 由调用方传入，服务本身可 Singleton
/// - 不依赖 E0Context、CommandManager、MetaContext
/// - 异步全程，与 EF Core 异步 API 对齐
///
/// 使用模式：在 ICommandHandler 中注入 IEntityService，配合 IDbContextFactory 创建 DbContext。
/// </summary>
public interface IEntityService
{
    /// <summary>
    /// 创建实体。
    /// 自动处理：导航属性清理（防注入）、唯一性验证、BeforeSave 钩子。
    /// 时间戳/审计字段由 AuditInterceptor 在 SaveChanges 时自动填充。
    /// </summary>
    /// <returns>true = 成功，false = 验证失败（错误已收集到 IErrs）。</returns>
    Task<bool> CreateAsync<TEntity>(
        DbContext context,
        TEntity entity,
        EntityWriteOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;

    /// <summary>
    /// 更新实体。
    /// 自动处理：ID 存在性检查、字段掩码（PostedProperties）、M:N 关系 diff、唯一性验证。
    /// </summary>
    /// <returns>true = 成功，false = 验证失败或目标不存在。</returns>
    Task<bool> UpdateAsync<TEntity>(
        DbContext context,
        TEntity entity,
        EntityWriteOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;

    /// <summary>
    /// 删除实体。
    /// - 若实体实现 ISoftDeleteEntity，AuditInterceptor 自动转为软删除
    /// - 否则物理删除
    /// </summary>
    Task<bool> DeleteAsync<TEntity>(
        DbContext context,
        TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;
}
