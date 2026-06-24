namespace TenE0.Core.Auditing;

/// <summary>
/// 敏感字段脱敏过滤器。
///
/// <para>
/// <see cref="AuditLogInterceptor"/> 在序列化字段 diff 前，对每个属性名调用 <see cref="IsSensitive"/>
/// 判定，命中则用 <see cref="Mask"/> 把值替换为占位符，避免 Password/Token/Secret/SigningKey 等
/// 敏感值明文落入审计表（审计表本身是高价值泄露目标）。
/// </para>
///
/// <para>
/// 默认实现 <see cref="DefaultAuditFieldFilter"/> 覆盖常见敏感命名。业务方可在 DI 中
/// <c>services.Replace(...)</c> 注册自定义实现替换默认规则（如对特定实体字段加业务级脱敏）。
/// </para>
/// </summary>
public interface IAuditFieldFilter
{
    /// <summary>属性名是否属于敏感字段（需脱敏处理）。</summary>
    bool IsSensitive(string propertyName);

    /// <summary>
    /// 对敏感值脱敏。默认返回固定占位符 "***"。
    /// 调用方保证：<paramref name="propertyName"/> 已通过 <see cref="IsSensitive"/> 判定为敏感。
    /// </summary>
    object? Mask(string propertyName, object? value);
}
