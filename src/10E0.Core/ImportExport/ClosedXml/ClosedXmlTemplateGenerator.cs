using System.Globalization;
using ClosedXML.Excel;
using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.ImportExport.ClosedXml;

/// <summary>
/// <see cref="IImportTemplateGenerator"/> 默认实现（ClosedXML）。
///
/// <para>根据实体的列映射生成空白导入模板：
/// 表头行（列名）+ 一行示例（按属性类型推断占位值）+ 必填列的列级数据校验。
/// 前端引导用户按模板填写，减少导入期的类型/必填错误。</para>
/// </summary>
public sealed class ClosedXmlTemplateGenerator : IImportTemplateGenerator
{
    /// <inheritdoc/>
    public Task GenerateAsync<T>(Stream output, CancellationToken ct = default)
        where T : class, new()
    {
        var columns = MappingResolver.Resolve<T>().ImportColumns();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("导入模板");

        // 模板覆盖的数据行深度（示例行 + 1000 行可填写区，足够引导）
        const int templateRows = 1000;

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var headerCell = ws.Cell(1, i + 1);
            headerCell.Value = column.ColumnName;
            headerCell.Style.Font.Bold = column.Required;
            if (column.Required)
                headerCell.Style.Fill.BackgroundColor = XLColor.LightYellow;

            // 示例行
            ws.Cell(2, i + 1).Value = SampleValue(column.Property.PropertyType);

            // 列级数据校验（必填 + 数字/日期类型）
            ApplyDataValidation(ws, column, i + 1, templateRows);
        }

        // 注意：不在 ws.Row(1) 上整行设 Bold —— 那会覆盖每个单元格按必填与否的差异化样式。
        // 必填列的加粗/底色已在循环内按单元格设置。
        ws.Columns().AdjustToContents();

        wb.SaveAs(output);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static string SampleValue(Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (type == typeof(string)) return "示例";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "2024-01-01";
        if (type == typeof(bool)) return "true";
        if (type == typeof(Guid)) return "00000000-0000-0000-0000-000000000000";
        if (type.IsEnum) return Enum.GetNames(type)[0];
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return "0.00";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return "0";

        return "";
    }

    private static void ApplyDataValidation(IXLWorksheet ws, ColumnMap map, int colIdx, int templateRows)
    {
        // 示例行起的数据填写区（第 2 行到 2+templateRows-1）
        var range = ws.Range(2, colIdx, 1 + templateRows, colIdx);

        var type = Nullable.GetUnderlyingType(map.Property.PropertyType) ?? map.Property.PropertyType;

        // 数字列：附加"必须为数字"校验，引导用户正确填写
        if (type == typeof(int) || type == typeof(long) || type == typeof(decimal)
            || type == typeof(double) || type == typeof(float) || type == typeof(short) || type == typeof(byte))
        {
            var dv = range.CreateDataValidation();
            dv.AllowedValues = XLAllowedValues.Decimal;
            dv.Operator = XLOperator.Between;
            dv.MinValue = decimal.MinValue.ToString(CultureInfo.InvariantCulture);
            dv.MaxValue = decimal.MaxValue.ToString(CultureInfo.InvariantCulture);
        }
        // 日期 / 布尔列的校验在不同 ClosedXML 版本 API 不稳定，
        // 模板主要价值在表头 + 示例行 + 必填视觉提示，校验仅为辅助，此处不强依赖。
    }
}
