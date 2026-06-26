using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Auditing;
using TenE0.Core.ImportExport;
using TenE0.Core.ImportExport.ClosedXml;
using TenE0.Core.ImportExport.Csv;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 导入导出模块 DI 注册扩展（issue #154）。
///
/// <para>一次性注册默认实现（调用方一行 <see cref="AddTenE0ImportExport"/> 即可获得完整能力）：
/// <list type="bullet">
/// <item><see cref="IExcelExporter"/> / <see cref="IExcelImporter"/>（ClosedXML）。</item>
/// <item><see cref="ICsvExporter"/> / <see cref="ICsvImporter"/>（手写 RFC 4180）。</item>
/// <item><see cref="IImportTemplateGenerator"/>（ClosedXML 模板生成）。</item>
/// <item><see cref="IExportFieldFilter"/>：默认包装 <see cref="IAuditFieldFilter"/>（审计模块未启用时直通）。</item>
/// <item><see cref="ImportExecutor"/>：通用导入执行器（接收外部 DbContext/Factory）。</item>
/// </list>
/// </para>
///
/// <para><b>关键约束</b>：无 <c>&lt;TContext&gt;</c> 泛型 —— 导入导出是纯流处理，不绑定 DbContext；
/// <see cref="ImportExecutor"/> 在调用时接收 <c>IDbContextFactory&lt;TContext&gt;</c>。</para>
/// </summary>
public static class ImportExportExtensions
{
    public static IServiceCollection AddTenE0ImportExport(
        this IServiceCollection services,
        Action<ImportExportOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<ImportExportOptions>();

        services.TryAddScoped<IExcelExporter, ClosedXmlExcelExporter>();
        services.TryAddScoped<IExcelImporter, ClosedXmlExcelImporter>();
        services.TryAddScoped<ICsvExporter, CsvExporter>();
        services.TryAddScoped<ICsvImporter, CsvImporter>();
        services.TryAddScoped<IImportTemplateGenerator, ClosedXmlTemplateGenerator>();

        // ExportFieldFilter 默认包装 IAuditFieldFilter；审计模块未注册时 auditFilter 解析为 null，直通不脱敏
        services.TryAddSingleton<IExportFieldFilter>(sp =>
            new ExportFieldFilter(sp.GetService<IAuditFieldFilter>()));

        services.TryAddScoped<ImportExecutor>();

        return services;
    }
}
