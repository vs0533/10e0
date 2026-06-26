using Microsoft.EntityFrameworkCore;
using TenE0.Core.Configuration.Storage;

namespace TenE0.Core.Configuration;

/// <summary>
/// 数据字典服务实现 — 查询部分（partial）。缓存读路径走 <see cref="Caching.IMultiLevelCache.GetOrSetAsync"/>。
/// </summary>
public sealed partial class DataDictionaryService<TContext>
    where TContext : DbContext
{
    public async Task<IReadOnlyList<DictItemDto>> GetItemsAsync(
        string dictTypeCode, bool onlyEnabled = true, bool asTree = false, CancellationToken cancellationToken = default)
    {
        // 缓存只缓存"仅启用 + 平铺"的完整列表；带 onlyEnabled=false 或 asTree=true 时在内存二次加工，
        // 避免缓存键爆炸（这两个维度组合数有限，且非启用/树形是低频管理操作）。
        var all = await cache.GetOrSetAsync(
            ItemCacheKey(dictTypeCode),
            ct => LoadItemsFromDbAsync(dictTypeCode, ct),
            ItemCacheOptions(),
            cancellationToken);

        var source = all ?? [];
        if (onlyEnabled)
            source = source.Where(i => i.IsEnabled).ToList();

        if (!asTree)
        {
            return source
                .OrderBy(i => i.SortOrder)
                .ThenBy(i => i.Value, StringComparer.Ordinal)
                .Select(CloneDto)
                .ToList();
        }

        // 树形组装（复用 MenuService.BuildTree 范式：平铺 → 按 ParentItemValue 分组 → 递归）
        var byParent = source
            .GroupBy(i => i.ParentItemValue)
            .ToDictionary(g => g.Key ?? string.Empty, g => g.OrderBy(i => i.SortOrder).ThenBy(i => i.Value, StringComparer.Ordinal).ToList());
        return (byParent.GetValueOrDefault(string.Empty) ?? [])
            .Select(root => BuildNode(root, byParent))
            .ToList();
    }

    public async Task<DictItemDto?> GetItemByValueAsync(
        string dictTypeCode, string value, bool onlyEnabled = true, CancellationToken cancellationToken = default)
    {
        var items = await GetItemsAsync(dictTypeCode, onlyEnabled, asTree: false, cancellationToken);
        return items.FirstOrDefault(i => string.Equals(i.Value, value, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<DictTypeDto>> GetTypesAsync(CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);
        // 注意：不在 EF 查询里用 StringComparer（InMemory/SQL 均无法翻译），
        // 按数据库默认字符串排序即可（code 多为 ASCII，与 ordinal 实际一致）。
        return await dc.Set<TenE0DictType>()
            .AsNoTracking()
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Code)
            .Select(t => new DictTypeDto
            {
                Id = t.Id,
                Code = t.Code,
                Name = t.Name,
                Description = t.Description,
                IsEnabled = t.IsEnabled,
                SortOrder = t.SortOrder,
            })
            .ToListAsync(cancellationToken);
    }

    // ============================================================
    // 内部
    // ============================================================

    private async ValueTask<List<DictItemDto>?> LoadItemsFromDbAsync(string dictTypeCode, CancellationToken ct)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        return await dc.Set<TenE0DictItem>()
            .AsNoTracking()
            .Where(i => i.DictTypeCode == dictTypeCode)
            .Select(i => new DictItemDto
            {
                Id = i.Id,
                Label = i.Label,
                Value = i.Value,
                ExtraJson = i.ExtraJson,
                IsEnabled = i.IsEnabled,
                SortOrder = i.SortOrder,
                ParentItemValue = i.ParentItemValue,
            })
            .ToListAsync(ct);
    }

    private static DictItemDto CloneDto(DictItemDto src) => new()
    {
        Id = src.Id,
        Label = src.Label,
        Value = src.Value,
        ExtraJson = src.ExtraJson,
        IsEnabled = src.IsEnabled,
        SortOrder = src.SortOrder,
        ParentItemValue = src.ParentItemValue,
    };

    private static DictItemDto BuildNode(DictItemDto node, Dictionary<string, List<DictItemDto>> byParent)
    {
        var result = CloneDto(node);
        if (byParent.TryGetValue(node.Value, out var children))
            result.Children = children.Select(c => BuildNode(c, byParent)).ToList();
        return result;
    }
}
