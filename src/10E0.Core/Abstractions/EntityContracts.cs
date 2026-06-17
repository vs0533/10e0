namespace TenE0.Core.Abstractions;

/// <summary>
/// 所有实体的根接口。承担"统一主键访问"职责，便于反射/拦截器统一处理。
/// 继承自旧 E0 的 IBaseEntity。
/// </summary>
public interface IBaseEntity
{
    /// <summary>
    /// 实体主键。统一使用 string（GUID 字面量）以兼容多种 ID 策略（GUID/雪花/业务编号）。
    /// 旧实现里 Id 类型不统一，这里强制收敛。
    /// </summary>
    string Id { get; set; }
}

/// <summary>
/// 时间戳实体接口。AuditInterceptor 自动填充。
/// 旧实现里 CreateTime / CreateBy 等字段散落在 EntityServer 手动赋值，新版统一拦截。
/// </summary>
public interface ITimerEntity : IBaseEntity
{
    DateTimeOffset? CreateTime { get; set; }
    string? CreateBy { get; set; }

    DateTimeOffset? UpdateTime { get; set; }
    string? UpdateBy { get; set; }
}

/// <summary>
/// 软删除实体接口。
/// - AuditInterceptor 自动把 Delete 操作转为 Update（IsSoftDelete=true）
/// - OnModelCreating 自动注册 Named Query Filter "SoftDelete"，查询时自动过滤
/// - 需要查询已删除数据时调用 .IgnoreQueryFilters()（注意：这会同时绕过行级安全过滤器）
/// </summary>
public interface ISoftDeleteEntity : IBaseEntity
{
    bool IsSoftDelete { get; set; }
    DateTimeOffset? DeleteTime { get; set; }
    string? DeleteBy { get; set; }
}

/// <summary>
/// 树形结构实体接口（轻量版本，用于"父子关系"）。
/// 重型场景请实现 ITreeEntityHierarchy（HierarchyId 支持，由 10E0.Core.SqlServer 提供）。
/// </summary>
public interface ITreeEntity : IBaseEntity
{
    string? ParentId { get; set; }
}

/// <summary>
/// 多租户实体接口（#11 multi-tenancy）。
///
/// 业务方让实体实现此接口后：
/// - <see cref="TenE0.Core.DataContext.BaseDataContext"/> 在 OnModelCreating 时
///   自动为该实体注册名为 <c>Tenant</c> 的 Named Query Filter：
///   <c>e.TenantId == currentTenantId OR BypassFilters == true</c>
/// - 跨租户查询在应用层不需要写条件；EF 自动追加
/// - 超管（<c>IDataAccessPolicy.BypassFilters == true</c>）可见全租户数据
/// - .IgnoreQueryFilters("Tenant") 旁路租户过滤
///
/// 字段约束：
/// - <see cref="TenantId"/> 必填（写入侧由业务方在创建时赋值；Filter 侧用 Bypass 短路）
/// - 租户上下文由 <see cref="ITenantContext"/> 解析，默认从 JWT "tenant_id" claim 读取
/// </summary>
public interface IMultiTenantEntity : IBaseEntity
{
    /// <summary>
    /// 租户 ID。Guid 字符串 / 业务编码皆可。
    /// 业务方在 Insert 时必须赋值；Query 侧由 EF Tenant Named Filter 自动比对。
    /// </summary>
    string TenantId { get; set; }
}
