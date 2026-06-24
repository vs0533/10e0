using ClosedXML.Excel;
using Microsoft.Extensions.Options;
using TenE0.Core.ImportExport;
using TenE0.Core.ImportExport.ClosedXml;
using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.Tests.ImportExport;

[Trait("Category", "Unit")]
public sealed class ClosedXmlExcelExporterTests
{
    private sealed class ExportSample
    {
        [ExportColumn("姓名", Order = 1)]
        public string Name { get; set; } = "";

        [ExportColumn("薪资", Order = 2, Format = "N2")]
        public decimal Salary { get; set; }

        [ExportColumn("入职日期", Order = 3, Format = "yyyy-MM-dd")]
        public DateTime HireDate { get; set; }

        [ExportIgnore]
        public string Password { get; set; } = "secret";
    }

    private static ClosedXmlExcelExporter CreateExporter(
        ImportExportOptions? options = null,
        IExportFieldFilter? fieldFilter = null,
        ICsvExporter? csvExporter = null)
    {
        options ??= new ImportExportOptions();
        fieldFilter ??= Mock.Of<IExportFieldFilter>();
        csvExporter ??= Mock.Of<ICsvExporter>();
        return new ClosedXmlExcelExporter(
            Options.Create(options),
            fieldFilter,
            csvExporter);
    }

    [Fact]
    public async Task ExportAsync_SmallDataset_WritesHeaderAndRows()
    {
        var sut = CreateExporter();
        var data = new List<ExportSample>
        {
            new() { Name = "张三", Salary = 1234.5m, HireDate = new DateTime(2024, 1, 15) },
            new() { Name = "李四", Salary = 6789m, HireDate = new DateTime(2024, 2, 20) },
        };

        var export = await sut.ExportAsync(data);

        export.Format.Should().Be(ExportFormat.Xlsx);
        export.DowngradedReason.Should().BeNull();

        using var wb = new XLWorkbook(export.Content);
        var ws = wb.Worksheets.First();

        ws.Cell(1, 1).GetValue<string>().Should().Be("姓名");
        ws.Cell(1, 2).GetValue<string>().Should().Be("薪资");
        ws.Cell(1, 3).GetValue<string>().Should().Be("入职日期");

        ws.Cell(2, 1).GetValue<string>().Should().Be("张三");
        ws.Cell(2, 2).GetValue<decimal>().Should().Be(1234.5m);
        ws.Cell(3, 1).GetValue<string>().Should().Be("李四");
    }

    [Fact]
    public async Task ExportAsync_ExportIgnoreProperty_OmitsColumn()
    {
        var sut = CreateExporter();
        var data = new List<ExportSample> { new() { Name = "张三", Salary = 1m, HireDate = DateTime.Today } };

        var export = await sut.ExportAsync(data);

        using var wb = new XLWorkbook(export.Content);
        var ws = wb.Worksheets.First();

        // 三列：姓名 / 薪资 / 入职日期 —— Password 被 [ExportIgnore] 排除
        ws.LastColumnUsed()!.ColumnNumber().Should().Be(3);
        ws.Cell(1, 1).GetValue<string>().Should().NotBe("Password");
    }

    [Fact]
    public async Task ExportAsync_MaskSensitiveTrue_AppliesFieldFilter()
    {
        var filter = new Mock<IExportFieldFilter>();
        filter.Setup(f => f.ShouldMask("Name")).Returns(true);
        filter.Setup(f => f.Mask("Name", It.IsAny<object?>())).Returns("***");
        var sut = CreateExporter(fieldFilter: filter.Object);

        var data = new List<ExportSample> { new() { Name = "敏感值", Salary = 1m, HireDate = DateTime.Today } };

        var export = await sut.ExportAsync(data);

        using var wb = new XLWorkbook(export.Content);
        var ws = wb.Worksheets.First();

        ws.Cell(2, 1).GetValue<string>().Should().Be("***");
    }

    [Fact]
    public async Task ExportAsync_MaskSensitiveFalse_PassesRawValue()
    {
        var filter = new Mock<IExportFieldFilter>();
        filter.Setup(f => f.ShouldMask(It.IsAny<string>())).Returns(true);
        var sut = CreateExporter(fieldFilter: filter.Object);

        var data = new List<ExportSample> { new() { Name = "原值", Salary = 1m, HireDate = DateTime.Today } };

        var export = await sut.ExportAsync(data, new ExportOptions { MaskSensitive = false });

        using var wb = new XLWorkbook(export.Content);
        var ws = wb.Worksheets.First();

        ws.Cell(2, 1).GetValue<string>().Should().Be("原值");
    }

    [Fact]
    public async Task ExportAsync_Queryable_ExceedsThreshold_DowngradesToCsv()
    {
        // 用 EF InMemory 构造 IQueryable（AsQueryable 不支持 CountAsync）
        var ctxOptions = new DbContextOptionsBuilder<ThresholdDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var ctx = new ThresholdDbContext(ctxOptions);
        ctx.Samples.AddRange(
            new ExportSample { Name = "a", Salary = 1m, HireDate = DateTime.Today },
            new ExportSample { Name = "b", Salary = 1m, HireDate = DateTime.Today },
            new ExportSample { Name = "c", Salary = 1m, HireDate = DateTime.Today });
        await ctx.SaveChangesAsync();

        // 阈值压到 2，数据 3 条 → 触发降级
        var options = new ImportExportOptions
        {
            LargeExportThreshold = 2,
            ExportBatchSize = 100,
            MaxExportRows = 1000,
        };
        var fakeCsv = new Mock<ICsvExporter>();
        var csvStream = new ExportStream(new MemoryStream([1, 2, 3]), ExportFormat.Csv);
        fakeCsv.Setup(c => c.ExportAsync(It.IsAny<IQueryable<ExportSample>>(), It.IsAny<ExportOptions?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(csvStream);

        var sut = CreateExporter(options: options, csvExporter: fakeCsv.Object);
        var export = await sut.ExportAsync(ctx.Samples.AsQueryable());

        export.Format.Should().Be(ExportFormat.Csv);
        export.DowngradedReason.Should().NotBeNullOrEmpty();
        fakeCsv.Verify(c => c.ExportAsync(It.IsAny<IQueryable<ExportSample>>(), It.IsAny<ExportOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // 降级测试专用 DbContext —— 提供 AsQueryable 所需的 IAsyncQueryProvider
    private sealed class ThresholdDbContext(DbContextOptions<ThresholdDbContext> options) : DbContext(options)
    {
        public DbSet<ExportSample> Samples => Set<ExportSample>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<ExportSample>().HasKey(e => e.Name);
    }

    [Fact]
    public async Task ExportAsync_Queryable_ExceedsMaxRows_Throws()
    {
        var options = new ImportExportOptions { MaxExportRows = 1 };
        var sut = CreateExporter(options: options);
        var query = new List<ExportSample>
        {
            new() { Name = "a", Salary = 1m, HireDate = DateTime.Today },
            new() { Name = "b", Salary = 1m, HireDate = DateTime.Today },
        }.AsQueryable();

        var act = async () => await sut.ExportAsync(query);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExportAsync_DateFormat_AppliedToCell()
    {
        var sut = CreateExporter();
        var data = new List<ExportSample>
        {
            new() { Name = "x", Salary = 1m, HireDate = new DateTime(2024, 1, 5) },
        };

        var export = await sut.ExportAsync(data);

        using var wb = new XLWorkbook(export.Content);
        var ws = wb.Worksheets.First();
        var dateCell = ws.Cell(2, 3);
        dateCell.GetDateTime().Should().Be(new DateTime(2024, 1, 5));
        dateCell.Style.DateFormat.Format.Should().Be("yyyy-MM-dd");
    }
}
