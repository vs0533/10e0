namespace TenE0.Core.ImportExport;

/// <summary>
/// 导入导出模块全局配置（DI 注入，<c>AddTenE0ImportExport</c> 注册）。
/// </summary>
public sealed class ImportExportOptions
{
    /// <summary>
    /// 导出流式分批大小（行/批）。默认 5000。
    /// 越大内存占用越高、往返数据库次数越少；越小内存越省但往返越多。
    /// </summary>
    public int ExportBatchSize { get; set; } = 5000;

    /// <summary>
    /// 大文件降级阈值（行）。默认 100000。
    /// <see cref="IExcelExporter.ExportAsync{T}(IQueryable{T}, ExportOptions?, CancellationToken)"/>
    /// 检测到总行数超过此值时自动降级为 CSV，避免 .xlsx 压缩开销与内存膨胀。
    /// </summary>
    public int LargeExportThreshold { get; set; } = 100_000;

    /// <summary>导出分页大小上限（防止恶意大页）。默认 100000。</summary>
    public int MaxExportRows { get; set; } = 100_000;
}
