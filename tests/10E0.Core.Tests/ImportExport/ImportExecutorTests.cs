using TenE0.Core.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.Errors;
using TenE0.Core.ImportExport;
using TenE0.Core.ImportExport.ClosedXml;
using TenE0.Core.ImportExport.Csv;
using TenE0.Core.ImportExport.Mapping;
using TenE0.Core.Permissions;
using EntitySvc = TenE0.Core.EntityService.EntityService;

namespace TenE0.Core.Tests.ImportExport;

[Trait("Category", "Unit")]
public sealed class ImportExecutorTests
{
    // 仅实现 IBaseEntity 即可（ImportExecutor 不要求 AggregateRoot）
    private sealed class ImportProduct : IBaseEntity
    {
        // null! 让 EF ValueGeneratedOnAdd 在 InMemory 下自动生成主键（模拟 AuditInterceptor 行为）
        public string Id { get; set; } = null!;
        [ImportColumn("编码", Required = true)]
        public string Code { get; set; } = "";
        [ImportColumn("名称")]
        public string Name { get; set; } = "";
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<ImportProduct> Products => Set<ImportProduct>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ImportProduct>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.Code).IsRequired(false);
            });
        }
    }

    private sealed class TestFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static (ImportExecutor executor, IDbContextFactory<TestDbContext> factory) CreateInMemory()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory 不支持事务 —— 抑制 TransactionIgnoredWarning（与 FileServiceTests 一致）。
            // 事务行为本身在 SQLite/SqlServer 上才真正生效；此处验证 ImportExecutor 的编排逻辑。
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        var entityService = new EntitySvc(errs, permissionMock.Object);
        var factory = new TestFactory(options);
        var executor = new ImportExecutor(
            new ClosedXmlExcelImporter(),
            new CsvImporter(),
            entityService,
            errs);
        return (executor, factory);
    }

    private static Stream BuildCsv(params string[] lines)
        => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n"));

    [Fact]
    public async Task ExecuteAsync_NonTransactional_AllValidRowsPersist()
    {
        var (executor, factory) = CreateInMemory();
        var csv = BuildCsv("编码,名称", "P1,甲", "P2,乙");

        var result = await executor.ExecuteAsync<TestDbContext, ImportProduct>(
            factory, csv, ExportFormat.Csv);

        result.Total.Should().Be(2);
        result.Success.Should().Be(2);
        result.Failed.Should().Be(0);
        result.TransactionRolledBack.Should().BeFalse();

        await using var verify = factory.CreateDbContext();
        verify.Products.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_NonTransactional_PartialFailurePersistsValidRows()
    {
        var (executor, factory) = CreateInMemory();
        // 第二行缺必填"编码"
        var csv = BuildCsv("编码,名称", "P1,甲", ",乙", "P3,丙");

        var result = await executor.ExecuteAsync<TestDbContext, ImportProduct>(
            factory, csv, ExportFormat.Csv);

        result.Total.Should().Be(3);
        result.Success.Should().Be(2, "P1 和 P3 有效");
        result.Failed.Should().Be(1, "第 2 行必填缺失");
        result.Errors.Should().HaveCount(1);

        await using var verify = factory.CreateDbContext();
        verify.Products.Should().HaveCount(2, "有效行已落库，不受失败行影响");
    }

    [Fact]
    public async Task ExecuteAsync_NonTransactional_ProgressCallbackInvoked()
    {
        var (executor, factory) = CreateInMemory();
        var csv = BuildCsv("编码,名称", "P1,甲", "P2,乙", "P3,丙");
        var reports = new List<ImportProgress>();
        var progress = new Progress<ImportProgress>(p => reports.Add(p));

        await executor.ExecuteAsync<TestDbContext, ImportProduct>(
            factory, csv, ExportFormat.Csv, progress: progress);

        reports.Should().HaveCount(3);
        reports[^1].Processed.Should().Be(3);
        reports[^1].Success.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_Transactional_AllValid_CommitsAll()
    {
        var (executor, factory) = CreateInMemory();
        var csv = BuildCsv("编码,名称", "T1,甲", "T2,乙");
        var options = new ImportOptions { TransactionMode = TransactionMode.Transactional };

        var result = await executor.ExecuteAsync<TestDbContext, ImportProduct>(
            factory, csv, ExportFormat.Csv, options: options);

        // InMemory 不真正支持事务，但 ImportExecutor 走 CreateDbContextAsync + BeginTransactionAsync
        // 不抛异常；逻辑上无失败即提交。
        result.TransactionRolledBack.Should().BeFalse();
        result.Success.Should().Be(2);

        await using var verify = factory.CreateDbContext();
        verify.Products.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_Transactional_AnyFailure_RollsBackAll()
    {
        var (executor, factory) = CreateInMemory();
        // T2 必填缺失
        var csv = BuildCsv("编码,名称", "T1,甲", ",乙", "T3,丙");
        var options = new ImportOptions { TransactionMode = TransactionMode.Transactional };

        var result = await executor.ExecuteAsync<TestDbContext, ImportProduct>(
            factory, csv, ExportFormat.Csv, options: options);

        result.TransactionRolledBack.Should().BeTrue("事务模式下任一失败即回滚");
        result.Success.Should().Be(0);
        result.Total.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_XlsxFormat_RoutesToExcelImporter()
    {
        // 构造一个最小 xlsx：编码/名称 表头 + 1 行
        var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        ws.Cell(1, 1).Value = "编码";
        ws.Cell(1, 2).Value = "名称";
        ws.Cell(2, 1).Value = "X1";
        ws.Cell(2, 2).Value = "xlsx行";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var (executor, factory) = CreateInMemory();

        var result = await executor.ExecuteAsync<TestDbContext, ImportProduct>(
            factory, ms, ExportFormat.Xlsx);

        result.Success.Should().Be(1);
        await using var verify = factory.CreateDbContext();
        verify.Products.Single().Code.Should().Be("X1");
    }
}
