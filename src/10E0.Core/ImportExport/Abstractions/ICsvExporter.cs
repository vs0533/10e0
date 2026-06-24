namespace TenE0.Core.ImportExport;

/// <summary>
/// CSV 导出器抽象（默认实现 <see cref="Csv.CsvExporter"/>，手写 RFC 4180，不依赖 CsvHelper）。
/// </summary>
public interface ICsvExporter
{
    /// <summary>导出内存数据集为 CSV 流。</summary>
    Task<ExportStream> ExportAsync<T>(
        IEnumerable<T> data,
        ExportOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// 导出 <see cref="IQueryable{T}"/>（典型：EF Core DbSet）为 CSV 流。
    /// 流式分批加载，常作为 <see cref="IExcelExporter"/> 大文件降级目标。
    /// </summary>
    Task<ExportStream> ExportAsync<T>(
        IQueryable<T> query,
        ExportOptions? options = null,
        CancellationToken ct = default)
        where T : class;
}
