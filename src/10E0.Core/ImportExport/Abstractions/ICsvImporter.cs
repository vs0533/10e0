namespace TenE0.Core.ImportExport;

/// <summary>
/// CSV 导入器抽象（默认实现 <see cref="Csv.CsvImporter"/>，手写 RFC 4180 状态机解析）。
/// </summary>
public interface ICsvImporter
{
    /// <summary>从 CSV 流逐行读取为 <typeparamref name="T"/>。</summary>
    IAsyncEnumerable<ImportRow<T>> ReadAsync<T>(
        Stream csvStream,
        ImportOptions? options = null,
        CancellationToken ct = default)
        where T : class, new();
}
