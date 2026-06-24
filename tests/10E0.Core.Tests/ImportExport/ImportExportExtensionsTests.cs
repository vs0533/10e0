using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.DependencyInjection;
using TenE0.Core.ImportExport;
using TenE0.Core.ImportExport.ClosedXml;
using TenE0.Core.ImportExport.Csv;

namespace TenE0.Core.Tests.ImportExport;

[Trait("Category", "Unit")]
public sealed class ImportExportExtensionsTests
{
    [Fact]
    public void AddTenE0ImportExport_RegistersAllExpectedServices()
    {
        var services = new ServiceCollection();

        services.AddTenE0ImportExport();
        var sp = services.BuildServiceProvider();

        sp.GetService<IExcelExporter>().Should().BeOfType<ClosedXmlExcelExporter>();
        sp.GetService<IExcelImporter>().Should().BeOfType<ClosedXmlExcelImporter>();
        sp.GetService<ICsvExporter>().Should().BeOfType<CsvExporter>();
        sp.GetService<ICsvImporter>().Should().BeOfType<CsvImporter>();
        sp.GetService<IImportTemplateGenerator>().Should().BeOfType<ClosedXmlTemplateGenerator>();
        sp.GetService<IExportFieldFilter>().Should().NotBeNull();

        // ImportExecutor 依赖 IEntityService（由 AddTenE0EntityService 注册），此处仅断言描述符存在。
        services.Should().Contain(sd => sd.ServiceType == typeof(ImportExecutor));
    }

    [Fact]
    public void AddTenE0ImportExport_NoGenericType_DoesNotRequireDbContext()
    {
        // 关键约束：AddTenE0ImportExport 无 <TContext>，纯流处理
        var services = new ServiceCollection();

        var act = () => services.AddTenE0ImportExport();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddTenE0ImportExport_WithoutAuditModule_ExportFieldFilterPassthrough()
    {
        // 审计模块未注册 IAuditFieldFilter → ExportFieldFilter 直通（不脱敏）
        var services = new ServiceCollection();
        services.AddTenE0ImportExport();
        var sp = services.BuildServiceProvider();

        var filter = sp.GetRequiredService<IExportFieldFilter>();
        filter.ShouldMask("Password").Should().BeFalse("未启用审计模块，默认直通");
        filter.Mask("Password", "secret").Should().Be("secret");
    }

    [Fact]
    public void AddTenE0ImportExport_WithAuditModule_ExportFieldFilterDelegatesToAudit()
    {
        var services = new ServiceCollection();
        // 注册一个命中 "password" 的 IAuditFieldFilter
        var auditFilter = new Mock<TenE0.Core.Auditing.IAuditFieldFilter>();
        auditFilter.Setup(f => f.IsSensitive("Password")).Returns(true);
        auditFilter.Setup(f => f.Mask("Password", It.IsAny<object?>())).Returns("***");
        services.AddSingleton(auditFilter.Object);
        services.AddTenE0ImportExport();
        var sp = services.BuildServiceProvider();

        var filter = sp.GetRequiredService<IExportFieldFilter>();
        filter.ShouldMask("Password").Should().BeTrue();
        filter.Mask("Password", "secret").Should().Be("***");
    }

    [Fact]
    public void AddTenE0ImportExport_AppliesOptions()
    {
        var services = new ServiceCollection();
        services.AddTenE0ImportExport(o =>
        {
            o.ExportBatchSize = 100;
            o.LargeExportThreshold = 50;
        });
        var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ImportExportOptions>>().Value;
        options.ExportBatchSize.Should().Be(100);
        options.LargeExportThreshold.Should().Be(50);
    }
}
