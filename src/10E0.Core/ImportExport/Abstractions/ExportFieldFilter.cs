using TenE0.Core.Auditing;

namespace TenE0.Core.ImportExport;

/// <summary>
/// <see cref="IExportFieldFilter"/> 默认实现。
///
/// <para>当审计模块（#152）已注册 <see cref="IAuditFieldFilter"/> 时，转发脱敏判定；
/// 否则（审计未启用）直通 —— 不脱敏任何字段，由业务方自行用
/// <c>[ExportIgnore]</c> 或注册自定义 <see cref="IExportFieldFilter"/> 控制。</para>
///
/// <para>注册为 Singleton；业务方可 <c>services.Replace(...)</c> 覆盖。</para>
/// </summary>
public sealed class ExportFieldFilter : IExportFieldFilter
{
    private readonly IAuditFieldFilter? _auditFilter;

    /// <param name="auditFilter">审计脱敏过滤器（可选；审计模块未启用时为 null）。</param>
    public ExportFieldFilter(IAuditFieldFilter? auditFilter = null)
    {
        _auditFilter = auditFilter;
    }

    /// <inheritdoc/>
    public bool ShouldMask(string propertyName)
        => _auditFilter is not null && _auditFilter.IsSensitive(propertyName);

    /// <inheritdoc/>
    public object? Mask(string propertyName, object? value)
        => _auditFilter is not null ? _auditFilter.Mask(propertyName, value) : value;
}
