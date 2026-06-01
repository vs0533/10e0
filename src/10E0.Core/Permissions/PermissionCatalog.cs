namespace TenE0.Core.Permissions;

/// <summary>
/// 权限目录 — 启动时聚合所有 IPermissionProvider 的定义，提供运行时查询。
/// Singleton，启动后只读。
///
/// 用途：
/// - 权限管理 API 列出所有可授予的权限
/// - 校验 grant 操作的 PermissionKey 是否合法（防止脏数据）
/// </summary>
public sealed class PermissionCatalog
{
    private readonly Dictionary<string, PermissionDefinition> _byKey;

    public PermissionCatalog(IEnumerable<IPermissionProvider> providers)
    {
        _byKey = providers
            .SelectMany(p => p.Define())
            .GroupBy(d => d.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    /// <summary>所有已定义的权限。</summary>
    public IReadOnlyCollection<PermissionDefinition> All => _byKey.Values;

    /// <summary>按 key 查找；不存在返回 null。</summary>
    public PermissionDefinition? Find(string key) =>
        _byKey.TryGetValue(key, out var def) ? def : null;

    /// <summary>是否包含指定 key。</summary>
    public bool Contains(string key) => _byKey.ContainsKey(key);

    /// <summary>按分组聚合（用于 UI 树形展示）。</summary>
    public IReadOnlyDictionary<string, List<PermissionDefinition>> ByGroup =>
        _byKey.Values
            .GroupBy(d => d.Group ?? "default")
            .ToDictionary(g => g.Key, g => g.ToList());
}
