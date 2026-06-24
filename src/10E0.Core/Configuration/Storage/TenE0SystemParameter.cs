using TenE0.Core.Entities;

namespace TenE0.Core.Configuration.Storage;

/// <summary>
/// 系统参数值类型。<see cref="TenE0SystemParameter.Value"/> 统一字符串存储，读取时按此枚举类型化转换。
/// </summary>
public enum ParameterValueType
{
    /// <summary>字符串（默认）。</summary>
    String = 0,

    /// <summary>整数。</summary>
    Int = 1,

    /// <summary>布尔。</summary>
    Bool = 2,

    /// <summary>十进制数。</summary>
    Decimal = 3,

    /// <summary>JSON 对象/数组，读取时反序列化为目标类型。</summary>
    Json = 4,
}

/// <summary>
/// 系统参数 — Key-Value 存储，支持运行时修改 + 类型化读取。
///
/// <para>
/// 设计（Issue #153 决策点 2）：仅预定义 key（由 <c>ISystemParameterDefinition</c> 注册表声明）
/// 的值可被 Admin 修改；运行时不可新增未定义 key，避免脏数据。
/// <see cref="IsReadOnly"/> 标记的关键参数（如安装时锁定项）拒绝任何运行时修改。
/// </para>
/// </summary>
public class TenE0SystemParameter : AuditedEntity
{
    /// <summary>唯一 key，如 "password.min_length"。</summary>
    public string Key { get; set; } = "";

    /// <summary>值（字符串存储，读取时按 <see cref="ValueType"/> 转换）。</summary>
    public string Value { get; set; } = "";

    /// <summary>值类型，决定读取时的类型化转换方式。</summary>
    public ParameterValueType ValueType { get; set; } = ParameterValueType.String;

    /// <summary>描述。</summary>
    public string? Description { get; set; }

    /// <summary>分组（前端按组展示），默认 "General"。</summary>
    public string Group { get; set; } = "General";

    /// <summary>运行时只读（如安装时锁定的关键参数），拒绝任何运行时修改。</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>是否对前端隐藏（敏感参数不回显，默认 false）。</summary>
    public bool IsHidden { get; set; }
}
