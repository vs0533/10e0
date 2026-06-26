namespace TenE0.Core.ImportExport;

/// <summary>
/// Excel 导出器抽象（默认实现 ClosedXmlExcelExporter）。
/// </summary>
public interface IExcelExporter
{
    /// <summary>
    /// 导出内存数据集为 .xlsx 流。
    /// </summary>
    /// <typeparam name="T">行类型。列映射由 attribute / fluent 提供，详见 <see cref="Mapping.MappingResolver"/>。</typeparam>
    Task<ExportStream> ExportAsync<T>(
        IEnumerable<T> data,
        ExportOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// 导出 <see cref="IQueryable{T}"/>（典型：EF Core DbSet）为 .xlsx 流。
    ///
    /// <para>流式分批加载（默认每批 <c>ImportExportOptions.ExportBatchSize</c> 行），避免一次性
    /// <c>ToList</c> 内存爆炸。行数超过 <c>ImportExportOptions.LargeExportThreshold</c> 时自动降级为
    /// CSV 导出，<see cref="ExportStream.Format"/> 标记为 <see cref="ExportFormat.Csv"/>。</para>
    /// </summary>
    Task<ExportStream> ExportAsync<T>(
        IQueryable<T> query,
        ExportOptions? options = null,
        CancellationToken ct = default)
        where T : class;
}
