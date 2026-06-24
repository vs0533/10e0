using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.ImportExport.Csv;

/// <summary>
/// <see cref="ICsvExporter"/> 默认实现（手写 RFC 4180，不依赖 CsvHelper）。
///
/// <para>导出语义：<see cref="CsvWriter"/> 做字段转义，<see cref="MappingResolver"/> 决定列，
/// <see cref="IExportFieldFilter"/> 做脱敏（同 Excel 路径）。</para>
/// </summary>
public sealed class CsvExporter(
    IOptions<ImportExportOptions> options,
    IExportFieldFilter fieldFilter) : ICsvExporter
{
    private readonly ImportExportOptions _options = options.Value;

    /// <inheritdoc/>
    public async Task<ExportStream> ExportAsync<T>(
        IEnumerable<T> data,
        ExportOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ExportOptions();
        var columns = MappingResolver.Resolve<T>().ExportColumns();

        var ms = new MemoryStream();
        await using (var writer = CreateWriter(ms, options.Encoding))
        {
            var csv = new CsvWriter(writer);

            if (options.HasHeader)
                csv.WriteRow(columns.Select(c => c.ColumnName));

            foreach (var item in data)
            {
                csv.WriteRow(columns.Select(c =>
                {
                    var value = c.Property.GetValue(item);
                    if (options!.MaskSensitive && fieldFilter.ShouldMask(c.Property.Name))
                        value = fieldFilter.Mask(c.Property.Name, value);
                    return CsvWriter.FormatValue(value, c.Format);
                }));
                ct.ThrowIfCancellationRequested();
            }

            await writer.FlushAsync(ct);
        }

        ms.Position = 0;
        return new ExportStream(ms, ExportFormat.Csv);
    }

    /// <inheritdoc/>
    public async Task<ExportStream> ExportAsync<T>(
        IQueryable<T> query,
        ExportOptions? options = null,
        CancellationToken ct = default)
        where T : class
    {
        options ??= new ExportOptions();
        var columns = MappingResolver.Resolve<T>().ExportColumns();

        var ms = new MemoryStream();
        await using (var writer = CreateWriter(ms, options.Encoding))
        {
            var csv = new CsvWriter(writer);

            if (options.HasHeader)
                csv.WriteRow(columns.Select(c => c.ColumnName));

            var batchSize = Math.Max(1, _options.ExportBatchSize);
            var page = 1;
            while (true)
            {
                var batch = await query
                    .Skip((page - 1) * batchSize)
                    .Take(batchSize)
                    .ToListAsync(ct);

                if (batch.Count == 0) break;

                foreach (var item in batch)
                {
                    csv.WriteRow(columns.Select(c =>
                    {
                        var value = c.Property.GetValue(item);
                        if (options!.MaskSensitive && fieldFilter.ShouldMask(c.Property.Name))
                            value = fieldFilter.Mask(c.Property.Name, value);
                        return CsvWriter.FormatValue(value, c.Format);
                    }));
                }

                ct.ThrowIfCancellationRequested();
                if (batch.Count < batchSize) break;
                page++;
            }

            await writer.FlushAsync(ct);
        }

        ms.Position = 0;
        return new ExportStream(ms, ExportFormat.Csv);
    }

    private static StreamWriter CreateWriter(Stream stream, System.Text.Encoding encoding)
        => new(stream, encoding, leaveOpen: true) { NewLine = "\r\n" };
}
