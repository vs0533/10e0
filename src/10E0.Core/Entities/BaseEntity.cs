using TenE0.Core.Abstractions;

namespace TenE0.Core.Entities;

/// <summary>
/// 实体基类。仅包含主键，其他横切字段由对应接口标记后由拦截器/EF 配置统一处理。
/// </summary>
public abstract class BaseEntity : IBaseEntity
{
    /// <summary>主键，默认使用 GUID。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>
/// 带时间戳的实体基类（自动填充 CreateTime/UpdateTime/CreateBy/UpdateBy）。
/// </summary>
public abstract class TimedEntity : BaseEntity, ITimerEntity
{
    public DateTimeOffset? CreateTime { get; set; }
    public string? CreateBy { get; set; }
    public DateTimeOffset? UpdateTime { get; set; }
    public string? UpdateBy { get; set; }
}

/// <summary>
/// 软删除 + 时间戳实体基类（最常用）。
/// 删除操作会被 AuditInterceptor 转为标记删除，查询自动过滤。
/// </summary>
public abstract class AuditedEntity : TimedEntity, ISoftDeleteEntity
{
    public bool IsSoftDelete { get; set; }
    public DateTimeOffset? DeleteTime { get; set; }
    public string? DeleteBy { get; set; }
}

/// <summary>
/// 树形结构 + 完整审计字段。组织架构、菜单、分类常用。
/// </summary>
public abstract class TreeAuditedEntity : AuditedEntity, ITreeEntity
{
    public string? ParentId { get; set; }
}
