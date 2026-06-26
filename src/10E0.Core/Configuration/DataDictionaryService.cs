using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Caching;
using TenE0.Core.Configuration.Storage;
using TenE0.Core.Events;

namespace TenE0.Core.Configuration;

/// <summary>
/// 数据字典服务实现 — CRUD 部分。
///
/// <para>
/// 写后失效策略：每个 typeCode 的选项列表独立缓存（key 经 <see cref="ICacheKeyNamespace.DictItemsKey"/>），
/// 写操作只精准失效当前 typeCode，避免全量失效抖动（Issue #153 决策点 3）。失效后派发
/// <see cref="DictChangedEvent"/> 供订阅者（审计/通知）响应。
/// </para>
/// </summary>
public sealed partial class DataDictionaryService<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IMultiLevelCache cache,
    ICacheKeyNamespace keyNamespace,
    IOptions<ConfigurationOptions> options,
    IDomainEventDispatcher? eventDispatcher) : IDataDictionaryService
    where TContext : DbContext
{
    private readonly ConfigurationOptions _options = options.Value;

    private CacheOptions ItemCacheOptions() => new()
    {
        L1Duration = TimeSpan.FromSeconds(5),
        L2Duration = _options.DictCacheL2,
    };

    private string ItemCacheKey(string dictTypeCode) => keyNamespace.DictItemsKey(dictTypeCode);

    /// <summary>写后：失效该 typeCode 的选项缓存并派发变更事件。</summary>
    private async Task InvalidateAsync(string dictTypeCode, string change, CancellationToken ct)
    {
        await cache.RemoveAsync(ItemCacheKey(dictTypeCode), ct);
        if (eventDispatcher is not null)
            await eventDispatcher.DispatchAsync(new DictChangedEvent(dictTypeCode, change), ct);
    }

    // ============================================================
    // 字典类型 CRUD
    // ============================================================

    public async Task<DictTypeDto> AddTypeAsync(DictTypeCreateRequest request, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var exists = await dc.Set<TenE0DictType>().AnyAsync(t => t.Code == request.Code, cancellationToken);
        if (exists)
            throw new InvalidOperationException($"字典类型已存在：{request.Code}");

        var type = new TenE0DictType
        {
            Code = request.Code,
            Name = request.Name,
            Description = request.Description,
            IsEnabled = request.IsEnabled,
            SortOrder = request.SortOrder,
        };
        dc.Set<TenE0DictType>().Add(type);
        await dc.SaveChangesAsync(cancellationToken);
        return ToDto(type);
    }

    public async Task UpdateTypeAsync(string code, DictTypeUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var type = await dc.Set<TenE0DictType>()
            .FirstOrDefaultAsync(t => t.Code == code, cancellationToken)
            ?? throw new InvalidOperationException($"字典类型不存在：{code}");

        if (request.Name is not null) type.Name = request.Name;
        if (request.Description is not null) type.Description = request.Description;
        if (request.IsEnabled.HasValue) type.IsEnabled = request.IsEnabled.Value;
        if (request.SortOrder.HasValue) type.SortOrder = request.SortOrder.Value;

        await dc.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(code, "type-updated", cancellationToken);
    }

    public async Task DeleteTypeAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var type = await dc.Set<TenE0DictType>()
            .FirstOrDefaultAsync(t => t.Code == code, cancellationToken)
            ?? throw new InvalidOperationException($"字典类型不存在：{code}");

        // 软删除类型 + 其下全部选项（Remove → AuditInterceptor 转 soft-delete）
        dc.Set<TenE0DictType>().Remove(type);
        var items = await dc.Set<TenE0DictItem>()
            .Where(i => i.DictTypeCode == code)
            .ToListAsync(cancellationToken);
        dc.Set<TenE0DictItem>().RemoveRange(items);

        await dc.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(code, "type-removed", cancellationToken);
    }

    // ============================================================
    // 字典选项 CRUD
    // ============================================================

    public async Task<DictItemDto> AddItemAsync(
        string dictTypeCode, DictItemCreateRequest request, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        _ = await dc.Set<TenE0DictType>()
            .FirstOrDefaultAsync(t => t.Code == dictTypeCode, cancellationToken)
            ?? throw new InvalidOperationException($"字典类型不存在：{dictTypeCode}");

        var dup = await dc.Set<TenE0DictItem>()
            .AnyAsync(i => i.DictTypeCode == dictTypeCode && i.Value == request.Value, cancellationToken);
        if (dup)
            throw new InvalidOperationException($"字典选项已存在：{dictTypeCode}/{request.Value}");

        var item = new TenE0DictItem
        {
            DictTypeCode = dictTypeCode,
            Label = request.Label,
            Value = request.Value,
            ExtraJson = request.ExtraJson,
            IsEnabled = request.IsEnabled,
            SortOrder = request.SortOrder,
            ParentItemValue = request.ParentItemValue,
        };
        dc.Set<TenE0DictItem>().Add(item);
        await dc.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(dictTypeCode, "item-added", cancellationToken);
        return ToDto(item);
    }

    public async Task UpdateItemAsync(
        string dictTypeCode, string itemValue, DictItemUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var item = await dc.Set<TenE0DictItem>()
            .FirstOrDefaultAsync(i => i.DictTypeCode == dictTypeCode && i.Value == itemValue, cancellationToken)
            ?? throw new InvalidOperationException($"字典选项不存在：{dictTypeCode}/{itemValue}");

        if (request.Label is not null) item.Label = request.Label;
        if (request.Value is not null) item.Value = request.Value;
        if (request.ExtraJson is not null) item.ExtraJson = request.ExtraJson;
        if (request.IsEnabled.HasValue) item.IsEnabled = request.IsEnabled.Value;
        if (request.SortOrder.HasValue) item.SortOrder = request.SortOrder.Value;
        if (request.ParentItemValue is not null) item.ParentItemValue = request.ParentItemValue;

        await dc.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(dictTypeCode, "item-updated", cancellationToken);
    }

    public async Task DeleteItemAsync(string dictTypeCode, string itemValue, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var item = await dc.Set<TenE0DictItem>()
            .FirstOrDefaultAsync(i => i.DictTypeCode == dictTypeCode && i.Value == itemValue, cancellationToken)
            ?? throw new InvalidOperationException($"字典选项不存在：{dictTypeCode}/{itemValue}");

        dc.Set<TenE0DictItem>().Remove(item);
        await dc.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(dictTypeCode, "item-removed", cancellationToken);
    }

    public async Task MoveItemAsync(
        string dictTypeCode, string itemValue, string? newParentItemValue, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var item = await dc.Set<TenE0DictItem>()
            .FirstOrDefaultAsync(i => i.DictTypeCode == dictTypeCode && i.Value == itemValue, cancellationToken)
            ?? throw new InvalidOperationException($"字典选项不存在：{dictTypeCode}/{itemValue}");

        if (newParentItemValue is not null && newParentItemValue == itemValue)
            throw new InvalidOperationException("不能将选项移动到自身之下");

        item.ParentItemValue = newParentItemValue;
        await dc.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(dictTypeCode, "item-moved", cancellationToken);
    }

    // ============================================================
    // 映射
    // ============================================================

    private static DictTypeDto ToDto(TenE0DictType t) => new()
    {
        Id = t.Id,
        Code = t.Code,
        Name = t.Name,
        Description = t.Description,
        IsEnabled = t.IsEnabled,
        SortOrder = t.SortOrder,
    };

    private static DictItemDto ToDto(TenE0DictItem i) => new()
    {
        Id = i.Id,
        Label = i.Label,
        Value = i.Value,
        ExtraJson = i.ExtraJson,
        IsEnabled = i.IsEnabled,
        SortOrder = i.SortOrder,
        ParentItemValue = i.ParentItemValue,
    };
}
