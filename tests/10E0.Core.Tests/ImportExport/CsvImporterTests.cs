using System.Text;
using TenE0.Core.ImportExport;
using TenE0.Core.ImportExport.Csv;
using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.Tests.ImportExport;

[Trait("Category", "Unit")]
public sealed class CsvImporterTests
{
    private sealed class CsvImportSample
    {
        [ImportColumn("编码", Required = true)] [ExportColumn("编码", Order = 1)]
        public string Code { get; set; } = "";

        [ImportColumn("数量")] [ExportColumn("数量", Order = 2)]
        public int Quantity { get; set; }

        [ImportColumn("备注")] [ExportColumn("备注", Order = 3)]
        public string? Note { get; set; }
    }

    private static Stream ToStream(string csv)
        => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    [Fact]
    public async Task ReadAsync_ValidCsv_ParsesRows()
    {
        // 备注 "带,逗号" 必须引号包裹（RFC 4180），否则会被当作字段分隔
        var csv = "编码,数量,备注\r\nA001,10,普通\r\nA002,20,\"带,逗号\"\r\n";

        var importer = new CsvImporter();
        var rows = new List<ImportRow<CsvImportSample>>();
        await foreach (var row in importer.ReadAsync<CsvImportSample>(ToStream(csv)))
            rows.Add(row);

        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r => r.IsValid.Should().BeTrue());
        rows[0].Data!.Code.Should().Be("A001");
        rows[1].Data!.Note.Should().Be("带,逗号", "引号包裹的逗号字段应正确解析");
    }

    [Fact]
    public async Task ReadAsync_QuotedFieldWithEmbeddedNewline_SingleRow()
    {
        var csv = "编码,数量,备注\r\nA001,1,\"line1\nline2\"\r\n";

        var importer = new CsvImporter();
        var rows = new List<ImportRow<CsvImportSample>>();
        await foreach (var row in importer.ReadAsync<CsvImportSample>(ToStream(csv)))
            rows.Add(row);

        rows.Should().HaveCount(1, "引号内的换行不应分行");
        rows[0].Data!.Note.Should().Be("line1\nline2");
    }

    [Fact]
    public async Task ReadAsync_EscapedQuote_PreservesSingleQuote()
    {
        var csv = "编码,数量,备注\r\nA001,1,\"say \"\"hi\"\"\"\r\n";

        var importer = new CsvImporter();
        var rows = new List<ImportRow<CsvImportSample>>();
        await foreach (var row in importer.ReadAsync<CsvImportSample>(ToStream(csv)))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].Data!.Note.Should().Be("say \"hi\"");
    }

    [Fact]
    public async Task ReadAsync_MissingRequired_CollectsError()
    {
        var csv = "编码,数量,备注\r\n,1,n\r\n";

        var importer = new CsvImporter();
        var rows = new List<ImportRow<CsvImportSample>>();
        await foreach (var row in importer.ReadAsync<CsvImportSample>(ToStream(csv)))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].IsValid.Should().BeFalse();
        rows[0].Errors.Should().ContainMatch("*编码*不能为空*");
    }

    [Fact]
    public async Task ReadAsync_TypeConversionFailure_CollectsError()
    {
        var csv = "编码,数量,备注\r\nA001,abc,n\r\nA002,5,n\r\n";

        var importer = new CsvImporter();
        var rows = new List<ImportRow<CsvImportSample>>();
        await foreach (var row in importer.ReadAsync<CsvImportSample>(ToStream(csv)))
            rows.Add(row);

        rows.Should().HaveCount(2);
        rows[0].IsValid.Should().BeFalse();
        rows[0].Errors.Should().ContainMatch("*数量*无法转换*");
        rows[1].Data!.Quantity.Should().Be(5);
    }

    [Fact]
    public async Task ReadAsync_BlankRowsIgnored()
    {
        var csv = "编码,数量,备注\r\nA001,1,n\r\n,,\r\nA002,2,n\r\n";

        var importer = new CsvImporter();
        var rows = new List<ImportRow<CsvImportSample>>();
        await foreach (var row in importer.ReadAsync<CsvImportSample>(ToStream(csv)))
            rows.Add(row);

        rows.Should().HaveCount(2, "空行被忽略");
    }

    [Fact]
    public async Task ReadAsync_LfOnlyLineEndings_Work()
    {
        // 部分系统用 LF 而非 CRLF
        var csv = "编码,数量,备注\nA001,1,n\nA002,2,n\n";

        var importer = new CsvImporter();
        var rows = new List<ImportRow<CsvImportSample>>();
        await foreach (var row in importer.ReadAsync<CsvImportSample>(ToStream(csv)))
            rows.Add(row);

        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadAsync_RoundtripWithExporter_PreservesData()
    {
        var exporter = new CsvExporter(
            Microsoft.Extensions.Options.Options.Create(new ImportExportOptions()),
            Mock.Of<IExportFieldFilter>());

        var original = new List<CsvImportSample>
        {
            new() { Code = "RT1", Quantity = 11, Note = "含,逗号" },
            new() { Code = "RT2", Quantity = 22, Note = "含\"引号" },
        };

        var export = await exporter.ExportAsync(original);

        var importer = new CsvImporter();
        var rows = new List<ImportRow<CsvImportSample>>();
        await foreach (var row in importer.ReadAsync<CsvImportSample>(export.Content))
            rows.Add(row);

        rows.Should().HaveCount(2);
        rows[0].Data!.Code.Should().Be("RT1");
        rows[0].Data!.Note.Should().Be("含,逗号");
        rows[1].Data!.Code.Should().Be("RT2");
        rows[1].Data!.Note.Should().Be("含\"引号");
    }
}
