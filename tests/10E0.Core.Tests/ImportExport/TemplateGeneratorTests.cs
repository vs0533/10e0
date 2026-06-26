using ClosedXML.Excel;
using TenE0.Core.ImportExport.ClosedXml;
using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.Tests.ImportExport;

[Trait("Category", "Unit")]
public sealed class TemplateGeneratorTests
{
    private sealed class TemplateSample
    {
        [ImportColumn("编码", Required = true)]
        public string Code { get; set; } = "";

        [ImportColumn("数量")]
        public int Quantity { get; set; }

        [ImportColumn("启用")]
        public bool Enabled { get; set; }

        [ImportColumn("日期")]
        public DateTime HireDate { get; set; }
    }

    [Fact]
    public async Task GenerateAsync_WritesHeaderRowMatchingAttributes()
    {
        var generator = new ClosedXmlTemplateGenerator();
        using var ms = new MemoryStream();
        await generator.GenerateAsync<TemplateSample>(ms);

        ms.Position = 0;
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheets.First();

        ws.Cell(1, 1).GetValue<string>().Should().Be("编码");
        ws.Cell(1, 2).GetValue<string>().Should().Be("数量");
        ws.Cell(1, 3).GetValue<string>().Should().Be("启用");
        ws.Cell(1, 4).GetValue<string>().Should().Be("日期");
    }

    [Fact]
    public async Task GenerateAsync_RequiredColumnHeader_BoldedAndHighlighted()
    {
        var generator = new ClosedXmlTemplateGenerator();
        using var ms = new MemoryStream();
        await generator.GenerateAsync<TemplateSample>(ms);

        ms.Position = 0;
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheets.First();

        // 编码 必填 → 加粗 + 黄色底色
        var header = ws.Cell(1, 1);
        header.Style.Font.Bold.Should().BeTrue();
        header.Style.Fill.BackgroundColor.Should().Be(XLColor.LightYellow);

        // 数量 非必填 → 不加粗
        ws.Cell(1, 2).Style.Font.Bold.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_IncludesSampleRowByType()
    {
        var generator = new ClosedXmlTemplateGenerator();
        using var ms = new MemoryStream();
        await generator.GenerateAsync<TemplateSample>(ms);

        ms.Position = 0;
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheets.First();

        // 示例行（第 2 行）string 列填 "示例"
        ws.Cell(2, 1).GetValue<string>().Should().Be("示例");
        // int 列填 "0"
        ws.Cell(2, 2).GetValue<int>().Should().Be(0);
        // bool 列示例值为 "true"（ClosedXML 以文本写入，读取为字符串）
        ws.Cell(2, 3).GetValue<string>().ToLowerInvariant().Should().Be("true");
    }

    [Fact]
    public async Task GenerateAsync_OutputIsLoadableXlsx()
    {
        // 回归保护：生成的流必须是合法 xlsx，能被 ClosedXML 重新加载
        var generator = new ClosedXmlTemplateGenerator();
        using var ms = new MemoryStream();
        await generator.GenerateAsync<TemplateSample>(ms);

        ms.Position = 0;
        var act = () => new XLWorkbook(ms);
        act.Should().NotThrow();
    }
}
