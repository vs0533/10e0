using Microsoft.Extensions.Options;
using TenE0.Core.ImportExport;
using TenE0.Core.ImportExport.Csv;
using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.Tests.ImportExport;

[Trait("Category", "Unit")]
public sealed class CsvExporterTests
{
    private sealed class CsvSample
    {
        [ExportColumn("名称", Order = 1)]
        public string Name { get; set; } = "";

        [ExportColumn("备注", Order = 2)]
        public string? Note { get; set; }

        [ExportColumn("金额", Order = 3, Format = "N2")]
        public decimal Amount { get; set; }
    }

    private static CsvExporter CreateExporter(IExportFieldFilter? filter = null)
        => new(Options.Create(new ImportExportOptions()), filter ?? Mock.Of<IExportFieldFilter>());

    private static string ReadAll(Stream s)
    {
        s.Position = 0;
        using var reader = new StreamReader(s, leaveOpen: false);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task ExportAsync_WritesHeaderAndRows()
    {
        var sut = CreateExporter();
        var data = new List<CsvSample>
        {
            new() { Name = "甲", Note = "n1", Amount = 10m },
        };

        var export = await sut.ExportAsync(data);

        export.Format.Should().Be(ExportFormat.Csv);
        var content = ReadAll(export.Content);
        content.Should().Contain("名称,备注,金额");
        content.Should().Contain("甲,n1,10.00");
    }

    [Fact]
    public async Task ExportAsync_FieldWithComma_IsQuoted()
    {
        var sut = CreateExporter();
        var data = new List<CsvSample>
        {
            new() { Name = "a,b", Note = null, Amount = 1m },
        };

        var content = ReadAll((await sut.ExportAsync(data)).Content);

        content.Should().Contain("\"a,b\"");
    }

    [Fact]
    public async Task ExportAsync_FieldWithQuote_DoublesQuoteAndWraps()
    {
        var sut = CreateExporter();
        var data = new List<CsvSample>
        {
            new() { Name = "say \"hi\"", Note = null, Amount = 1m },
        };

        var content = ReadAll((await sut.ExportAsync(data)).Content);

        content.Should().Contain("\"say \"\"hi\"\"\"");
    }

    [Fact]
    public async Task ExportAsync_FieldWithNewline_IsQuotedWithEmbeddedNewline()
    {
        var sut = CreateExporter();
        var data = new List<CsvSample>
        {
            new() { Name = "line1\nline2", Note = null, Amount = 1m },
        };

        var content = ReadAll((await sut.ExportAsync(data)).Content);

        content.Should().Contain("\"line1\nline2\"");
    }

    [Fact]
    public async Task ExportAsync_MaskSensitiveTrue_AppliesFilter()
    {
        var filter = new Mock<IExportFieldFilter>();
        filter.Setup(f => f.ShouldMask("Name")).Returns(true);
        filter.Setup(f => f.Mask("Name", It.IsAny<object?>())).Returns("***");
        var sut = CreateExporter(filter.Object);

        var data = new List<CsvSample>
        {
            new() { Name = "secret", Note = null, Amount = 1m },
        };

        var content = ReadAll((await sut.ExportAsync(data, new ExportOptions { MaskSensitive = true })).Content);

        content.Should().Contain("***");
        content.Should().NotContain("secret");
    }

    [Fact]
    public async Task ExportAsync_NoHeader_OmitsHeaderRow()
    {
        var sut = CreateExporter();
        var data = new List<CsvSample>
        {
            new() { Name = "x", Note = null, Amount = 1m },
        };

        var content = ReadAll((await sut.ExportAsync(data, new ExportOptions { HasHeader = false })).Content);

        content.Should().NotContain("名称");
        content.Should().Contain("x");
    }
}
