using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.ImportExport.ClosedXml;

/// <summary>
/// <see cref="IExcelExporter"/> 默认实现（ClosedXML，MIT 许可）。
///
/// <para><b>大文件降级</b>：<see cref="ExportAsync{T}(IQueryable{T}, ExportOptions?, CancellationToken)"/>
/// 先 <c>CountAsync</c> 取总数，超过 <see cref="ImportExportOptions.LargeExportThreshold"/> 时改用
/// <see cref="ICsvExporter"/> 产出 CSV，<see cref="ExportStream.Format"/> 标记为
/// <see cref="ExportFormat.Csv"/> 并附降级原因，让调用方据此时设置 Content-Type。</para>
///
/// <para><b>流式分批</b>：未降级时按 <see cref="ImportExportOptions.ExportBatchSize"/> 分页
/// <c>ToListAsync</c>，逐批写入 ClosedXL，避免一次性 <c>ToList</c> 内存爆炸。</para>
/// </summary>
public sealed class ClosedXmlExcelExporter(
    IOptions<ImportExportOptions> options,
    IExportFieldFilter fieldFilter,
    ICsvExporter csvExporter) : IExcelExporter
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

        var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(options.SheetName);
        WriteHeader(ws, columns, options);

        var rowIndex = 2;
        foreach (var item in data)
        {
            WriteRow(ws, rowIndex, item, columns, options);
            rowIndex++;
            ct.ThrowIfCancellationRequested();
        }

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return new ExportStream(ms, ExportFormat.Xlsx);
    }

    /// <inheritdoc/>
    public async Task<ExportStream> ExportAsync<T>(
        IQueryable<T> query,
        ExportOptions? options = null,
        CancellationToken ct = default)
        where T : class
    {
        options ??= new ExportOptions();

        var total = await query.CountAsync(ct);
        if (total > _options.MaxExportRows)
        {
            throw new InvalidOperationException(
                $"导出行数 {total} 超过上限 {_options.MaxExportRows}，请缩小查询范围或联系管理员调整。");
        }

        // 大文件自动降级为 CSV（避免 .xlsx 压缩开销与内存膨胀）
        if (total > _options.LargeExportThreshold)
        {
            var csvStream = await csvExporter.ExportAsync(query, options, ct);
            return csvStream with
            {
                DowngradedReason = $"行数 {total} 超过阈值 {_options.LargeExportThreshold}，已自动降级为 CSV",
            };
        }

        var columns = MappingResolver.Resolve<T>().ExportColumns();
        var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(options.SheetName);
        WriteHeader(ws, columns, options);

        var batchSize = Math.Max(1, _options.ExportBatchSize);
        var rowIndex = 2;
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
                WriteRow(ws, rowIndex, item, columns, options);
                rowIndex++;
            }

            ct.ThrowIfCancellationRequested();
            if (batch.Count < batchSize) break;
            page++;
        }

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return new ExportStream(ms, ExportFormat.Xlsx);
    }

    private static void WriteHeader(
        IXLWorksheet ws,
        IReadOnlyList<ColumnMap> columns,
        ExportOptions options)
    {
        if (!options.HasHeader) return;

        for (var i = 0; i < columns.Count; i++)
            ws.Cell(1, i + 1).Value = columns[i].ColumnName;

        ws.Row(1).Style.Font.Bold = true;
    }

    private void WriteRow<T>(
        IXLWorksheet ws,
        int rowIndex,
        T item,
        IReadOnlyList<ColumnMap> columns,
        ExportOptions options)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var value = column.Property.GetValue(item);

            if (options.MaskSensitive && fieldFilter.ShouldMask(column.Property.Name))
                value = fieldFilter.Mask(column.Property.Name, value);

            var cell = ws.Cell(rowIndex, i + 1);
            SetCellValue(cell, value, column.Format);
        }
    }

    /// <summary>
    /// 把值写入单元格，按格式串/类型选择合适的方式。
    /// ClosedXML 推荐：日期/数字保留原生类型（便于 Excel 排序/计算），仅套用 DisplayFormat；
    /// 其余走 ToString。
    /// </summary>
    private static void SetCellValue(IXLCell cell, object? value, string? format)
    {
        switch (value)
        {
            case null:
                cell.Value = Blank.Value;
                break;
            case DateTimeOffset dto:
                cell.Value = dto.DateTime;
                if (!string.IsNullOrEmpty(format)) cell.Style.DateFormat.Format = format;
                break;
            case DateTime dt:
                cell.Value = dt;
                if (!string.IsNullOrEmpty(format)) cell.Style.DateFormat.Format = format;
                break;
            case decimal d:
                cell.Value = d;
                if (!string.IsNullOrEmpty(format)) cell.Style.NumberFormat.Format = format;
                break;
            case double db:
                cell.Value = db;
                if (!string.IsNullOrEmpty(format)) cell.Style.NumberFormat.Format = format;
                break;
            case float f:
                cell.Value = f;
                if (!string.IsNullOrEmpty(format)) cell.Style.NumberFormat.Format = format;
                break;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                cell.Value = Convert.ToInt64(value);
                if (!string.IsNullOrEmpty(format)) cell.Style.NumberFormat.Format = format;
                break;
            case bool b:
                cell.Value = b;
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }
}
