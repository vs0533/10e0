namespace TenE0.Core.ImportExport;

/// <summary>
/// Excel 导入器抽象（默认实现 ClosedXmlExcelImporter）。
///
/// <para>逐行流式读取（<see cref="IAsyncEnumerable{T}"/>），避免一次性把整张表加载进内存。
/// 行级解析错误（类型转换失败 / 必填缺失）收集进 <see cref="ImportRow{T}.Errors"/>，不抛断流，
/// 让调用方（如 <see cref="ImportExecutor"/>）决定是收集错误继续还是整体回滚。</para>
/// </summary>
public interface IExcelImporter
{
    /// <summary>
    /// 从 .xlsx 流逐行读取为 <typeparamref name="T"/>。
    /// </summary>
    /// <typeparam name="T">目标实体类型。列映射由 attribute / fluent 提供。</typeparam>
    IAsyncEnumerable<ImportRow<T>> ReadAsync<T>(
        Stream excelStream,
        ImportOptions? options = null,
        CancellationToken ct = default)
        where T : class, new();
}
