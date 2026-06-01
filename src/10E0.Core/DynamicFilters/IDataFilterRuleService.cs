using TenE0.Core.DynamicFilters.Storage;

namespace TenE0.Core.DynamicFilters;

/// <summary>
/// 数据过滤规则管理服务。
///
/// 提供规则的 CRUD 操作。修改规则后需要重启应用（或重新创建 DbContext 模型）
/// 才能让新的过滤表达式生效，因为 EF Model 在首次使用时缓存。
/// </summary>
public interface IDataFilterRuleService
{
    /// <summary>获取所有过滤规则。</summary>
    Task<IReadOnlyList<TenE0DataFilterRule>> GetAllAsync(CancellationToken ct = default);

    /// <summary>获取指定实体的过滤规则。</summary>
    Task<IReadOnlyList<TenE0DataFilterRule>> GetByEntityAsync(string entityTypeName, CancellationToken ct = default);

    /// <summary>根据 ID 获取单条规则。</summary>
    Task<TenE0DataFilterRule?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>创建过滤规则。</summary>
    Task<TenE0DataFilterRule> CreateAsync(DataFilterRuleCreateRequest request, CancellationToken ct = default);

    /// <summary>更新过滤规则。null 字段表示不修改。</summary>
    Task UpdateAsync(string id, DataFilterRuleUpdateRequest request, CancellationToken ct = default);

    /// <summary>删除过滤规则。</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>启用/禁用规则。</summary>
    Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default);
}
