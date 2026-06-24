using ClosedXML.Excel;
using TenE0.Core.ImportExport;
using TenE0.Core.ImportExport.ClosedXml;
using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.Tests.ImportExport;

[Trait("Category", "Unit")]
public sealed class ClosedXmlExcelImporterTests
{
    private sealed class ImportSample
    {
        [ImportColumn("编码", Required = true)] [ExportColumn("编码", Order = 1)]
        public string Code { get; set; } = "";

        [ImportColumn("数量")] [ExportColumn("数量", Order = 2)]
        public int Quantity { get; set; }

        [ImportColumn("启用")] [ExportColumn("启用", Order = 3)]
        public bool Enabled { get; set; }

        [ImportIgnore]
        public string ShouldNotImport { get; set; } = "";
    }

    private static Stream BuildXlsx(Action<IXLWorksheet> configure)
    {
        var ms = new MemoryStream();
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        configure(ws);
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task ReadAsync_ValidRows_YieldsParsedEntities()
    {
        var stream = BuildXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "编码";
            ws.Cell(1, 2).Value = "数量";
            ws.Cell(1, 3).Value = "启用";

            ws.Cell(2, 1).Value = "A001";
            ws.Cell(2, 2).Value = 10;
            ws.Cell(2, 3).Value = true;

            ws.Cell(3, 1).Value = "A002";
            ws.Cell(3, 2).Value = 20;
            ws.Cell(3, 3).Value = false;
        });

        var importer = new ClosedXmlExcelImporter();
        var rows = new List<ImportRow<ImportSample>>();
        await foreach (var row in importer.ReadAsync<ImportSample>(stream))
            rows.Add(row);

        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.IsValid.Should().BeTrue());

        rows[0].Data!.Code.Should().Be("A001");
        rows[0].Data!.Quantity.Should().Be(10);
        rows[0].Data!.Enabled.Should().BeTrue();

        rows[1].Data!.Code.Should().Be("A002");
        rows[1].Data!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task ReadAsync_MissingRequiredColumn_CollectsError()
    {
        var stream = BuildXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "编码";
            ws.Cell(1, 2).Value = "数量";

            ws.Cell(2, 1).Value = "";          // 必填为空
            ws.Cell(2, 2).Value = 5;
        });

        var importer = new ClosedXmlExcelImporter();
        var rows = new List<ImportRow<ImportSample>>();
        await foreach (var row in importer.ReadAsync<ImportSample>(stream))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].IsValid.Should().BeFalse();
        rows[0].Errors.Should().ContainMatch("*编码*不能为空*");
    }

    [Fact]
    public async Task ReadAsync_TypeConversionFailure_CollectsErrorNotThrow()
    {
        var stream = BuildXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "编码";
            ws.Cell(1, 2).Value = "数量";

            ws.Cell(2, 1).Value = "A001";
            ws.Cell(2, 2).Value = "不是数字";   // int 转换失败

            ws.Cell(3, 1).Value = "A002";
            ws.Cell(3, 2).Value = 30;          // 正常
        });

        var importer = new ClosedXmlExcelImporter();
        var rows = new List<ImportRow<ImportSample>>();
        await foreach (var row in importer.ReadAsync<ImportSample>(stream))
            rows.Add(row);

        rows.Should().HaveCount(2);
        rows[0].IsValid.Should().BeFalse("数量列转换失败");
        rows[0].Errors.Should().ContainMatch("*数量*无法转换*");

        rows[1].IsValid.Should().BeTrue("后续行不受错误行影响");
        rows[1].Data!.Quantity.Should().Be(30);
    }

    [Fact]
    public async Task ReadAsync_BlankRows_IgnoredByDefault()
    {
        var stream = BuildXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "编码";
            ws.Cell(1, 2).Value = "数量";

            ws.Cell(2, 1).Value = "A001";
            ws.Cell(2, 2).Value = 1;

            // 第 3 行全空
            ws.Cell(4, 1).Value = "A002";
            ws.Cell(4, 2).Value = 2;
        });

        var importer = new ClosedXmlExcelImporter();
        var rows = new List<ImportRow<ImportSample>>();
        await foreach (var row in importer.ReadAsync<ImportSample>(stream))
            rows.Add(row);

        rows.Should().HaveCount(2, "空行被忽略");
    }

    [Fact]
    public async Task ReadAsync_ColumnsMatchByNameNotOrder()
    {
        // 表头顺序与实体声明顺序相反 —— 按列名匹配应仍正确
        var stream = BuildXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "启用";
            ws.Cell(1, 2).Value = "数量";
            ws.Cell(1, 3).Value = "编码";

            ws.Cell(2, 1).Value = true;
            ws.Cell(2, 2).Value = 7;
            ws.Cell(2, 3).Value = "X001";
        });

        var importer = new ClosedXmlExcelImporter();
        var rows = new List<ImportRow<ImportSample>>();
        await foreach (var row in importer.ReadAsync<ImportSample>(stream))
            rows.Add(row);

        rows.Should().HaveCount(1);
        var entity = rows[0].Data!;
        entity.Code.Should().Be("X001");
        entity.Quantity.Should().Be(7);
        entity.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_ExportRoundtrip_ImportReimports()
    {
        // 导出 → 再导入，验证往返一致性
        var exporter = new ClosedXmlExcelExporter(
            Microsoft.Extensions.Options.Options.Create(new ImportExportOptions()),
            Mock.Of<IExportFieldFilter>(),
            Mock.Of<ICsvExporter>());

        var original = new List<ImportSample>
        {
            new() { Code = "R1", Quantity = 100, Enabled = true },
            new() { Code = "R2", Quantity = 200, Enabled = false },
        };

        var export = await exporter.ExportAsync(original);

        var importer = new ClosedXmlExcelImporter();
        var rows = new List<ImportRow<ImportSample>>();
        await foreach (var row in importer.ReadAsync<ImportSample>(export.Content))
            rows.Add(row);

        rows.Should().HaveCount(2);
        rows[0].Data!.Code.Should().Be("R1");
        rows[0].Data!.Quantity.Should().Be(100);
        rows[1].Data!.Code.Should().Be("R2");
    }
}
