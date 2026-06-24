using TenE0.Core.Configuration.Storage;

namespace TenE0.Core.Configuration;

// ============================================================
// 数据字典 DTO
// ============================================================

/// <summary>字典选项 DTO。</summary>
public class DictItemDto
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string? ExtraJson { get; set; }
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
    public string? ParentItemValue { get; set; }

    /// <summary>子选项（树形组装时填充）。</summary>
    public List<DictItemDto> Children { get; set; } = new();
}

/// <summary>字典类型 DTO。</summary>
public class DictTypeDto
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>创建字典类型请求。</summary>
public record DictTypeCreateRequest(
    string Code,
    string Name,
    string? Description,
    bool IsEnabled = true,
    int SortOrder = 0);

/// <summary>更新字典类型请求（null 字段不修改）。</summary>
public record DictTypeUpdateRequest(
    string? Name,
    string? Description,
    bool? IsEnabled,
    int? SortOrder);

/// <summary>创建字典选项请求。</summary>
public record DictItemCreateRequest(
    string Label,
    string Value,
    string? ExtraJson,
    bool IsEnabled = true,
    int SortOrder = 0,
    string? ParentItemValue = null);

/// <summary>更新字典选项请求（null 字段不修改）。</summary>
public record DictItemUpdateRequest(
    string? Label,
    string? Value,
    string? ExtraJson,
    bool? IsEnabled,
    int? SortOrder,
    string? ParentItemValue);

// ============================================================
// 系统参数 DTO
// ============================================================

/// <summary>系统参数 DTO（<see cref="IsHidden"/> 为 true 时端点层应脱敏 Value）。</summary>
public class SystemParameterDto
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public ParameterValueType ValueType { get; set; }
    public string? Description { get; set; }
    public string Group { get; set; } = "";
    public bool IsReadOnly { get; set; }
    public bool IsHidden { get; set; }
}

/// <summary>更新系统参数值请求（仅值，key/类型/只读标志由注册表决定，不接受修改）。</summary>
public record SystemParameterUpdateRequest(string Value);
