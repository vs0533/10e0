namespace TenE0.Core.ImportExport;

/// <summary>
/// 导出敏感字段脱敏过滤器。
///
/// <para><b>独立于审计模块的 <c>IAuditFieldFilter</c></b>（issue #152），导出语义自洽：
/// 业务方可单独覆盖导出脱敏规则而不影响审计落库。默认实现 <see cref="ExportFieldFilter"/>
/// 转发到 <c>IAuditFieldFilter</c>（若已注册），未启用审计模块时直通不脱敏。</para>
/// </summary>
public interface IExportFieldFilter
{
    /// <summary>该属性导出时是否需要脱敏。</summary>
    bool ShouldMask(string propertyName);

    /// <summary>对敏感值脱敏。<paramref name="propertyName"/> 调用方保证已通过 <see cref="ShouldMask"/>。</summary>
    object? Mask(string propertyName, object? value);
}
