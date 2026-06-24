namespace TenE0.Core.Configuration;

/// <summary>
/// 数据字典服务 — 字典类型与选项的 CRUD + 查询，带多级缓存。
///
/// <para>
/// 业务代码通过此服务读写字典，而非直接操作 <c>dc.DictTypes</c> —— 缓存失效由服务统一维护。
/// 写操作顺序：<c>SaveChangesAsync</c> → 精准失效该 typeCode 的缓存 → 派发 <see cref="DictChangedEvent"/>。
/// </para>
/// </summary>
public interface IDataDictionaryService
{
    /// <summary>获取字典类型的全部选项（默认仅启用的）。</summary>
    /// <param name="dictTypeCode">字典类型 Code。</param>
    /// <param name="onlyEnabled">是否只返回 <c>IsEnabled</c> 的选项。</param>
    /// <param name="asTree">是否组装为树形（按 <c>ParentItemValue</c>）。false 时按 <c>SortOrder</c> 平铺返回。</param>
    Task<IReadOnlyList<DictItemDto>> GetItemsAsync(
        string dictTypeCode,
        bool onlyEnabled = true,
        bool asTree = false,
        CancellationToken cancellationToken = default);

    /// <summary>按 Value 精确取单个选项（默认仅启用）。</summary>
    Task<DictItemDto?> GetItemByValueAsync(
        string dictTypeCode,
        string value,
        bool onlyEnabled = true,
        CancellationToken cancellationToken = default);

    // ---------- 字典类型 CRUD ----------

    /// <summary>获取全部字典类型。</summary>
    Task<IReadOnlyList<DictTypeDto>> GetTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>新增字典类型。</summary>
    Task<DictTypeDto> AddTypeAsync(DictTypeCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>更新字典类型（仅非 null 字段）。</summary>
    Task UpdateTypeAsync(string code, DictTypeUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>删除字典类型及其全部选项。</summary>
    Task DeleteTypeAsync(string code, CancellationToken cancellationToken = default);

    // ---------- 字典选项 CRUD ----------

    /// <summary>新增字典选项。</summary>
    Task<DictItemDto> AddItemAsync(
        string dictTypeCode,
        DictItemCreateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>更新字典选项（仅非 null 字段）。</summary>
    Task UpdateItemAsync(
        string dictTypeCode,
        string itemValue,
        DictItemUpdateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>删除字典选项。</summary>
    Task DeleteItemAsync(string dictTypeCode, string itemValue, CancellationToken cancellationToken = default);

    /// <summary>移动字典选项到新父级（<paramref name="newParentItemValue"/> 为 null = 移到根）。</summary>
    Task MoveItemAsync(
        string dictTypeCode,
        string itemValue,
        string? newParentItemValue,
        CancellationToken cancellationToken = default);
}
