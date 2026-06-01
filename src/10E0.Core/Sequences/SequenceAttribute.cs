namespace TenE0.Core.Sequences;

/// <summary>
/// 标记实体属性为流水号字段 — Create 时由 EntityService 自动生成并填充。
///
/// 用法：
///   public sealed class Order : AuditedEntity
///   {
///       [Sequence("order", "ORD-{yyyyMMdd}-{0000}")]
///       public string OrderNumber { get; set; } = "";
///
///       public required string Title { get; set; }
///   }
///
/// 行为：
/// - 仅在 CreateAsync 且属性值为空时填充；非空则保留客户端传入值
/// - Update 不重新生成（流水号一旦分配不应变更）
/// - 字段必须是 string 类型
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SequenceAttribute(string sequenceKey, string format) : Attribute
{
    /// <summary>序列业务 key，相同 key 共享一个序号空间。</summary>
    public string SequenceKey { get; } = sequenceKey;

    /// <summary>格式串，详见 <see cref="ISequenceGenerator"/>。</summary>
    public string Format { get; } = format;
}
