using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.Tests.ImportExport;

[Trait("Category", "Unit")]
public sealed class MappingResolverTests
{
    // 测试实体：覆盖 attribute 各种组合
    private sealed class Sample
    {
        [ImportColumn("编码", Required = true)] [ExportColumn("编码", Order = 1)]
        public string Code { get; set; } = "";

        [ImportColumn("名称")] [ExportColumn("名称", Order = 2)]
        public string Name { get; set; } = "";

        [ExportColumn("创建时间", Order = 3, Format = "yyyy-MM-dd")]
        public DateTimeOffset CreateTime { get; set; }

        [ImportIgnore] [ExportIgnore]
        public string Password { get; set; } = "";

        // 无任何标记 → 不参与映射
        public string Unmapped { get; set; } = "";

        [ImportColumn("仅导入")]
        public string ImportOnlyField { get; set; } = "";

        [ExportColumn("仅导出", Order = 4)]
        public string ExportOnlyField { get; set; } = "";
    }

    [Fact]
    public void Resolve_FromAttributes_ReadsColumnNamesAndRequired()
    {
        var columns = MappingResolver.Resolve<Sample>();

        var code = columns.Single(c => c.Property.Name == nameof(Sample.Code));
        code.ColumnName.Should().Be("编码");
        code.Required.Should().BeTrue();
        code.Importable.Should().BeTrue();
        code.Exportable.Should().BeTrue();
    }

    [Fact]
    public void Resolve_FromAttributes_RespectsFormat()
    {
        var columns = MappingResolver.Resolve<Sample>();

        var createTime = columns.Single(c => c.Property.Name == nameof(Sample.CreateTime));
        createTime.Format.Should().Be("yyyy-MM-dd");
    }

    [Fact]
    public void Resolve_ImportIgnoreExportIgnore_ExcludesFromBoth()
    {
        var columns = MappingResolver.Resolve<Sample>();

        columns.Should().NotContain(c => c.Property.Name == nameof(Sample.Password));
    }

    [Fact]
    public void Resolve_UnmarkedProperty_ExcludedFromMapping()
    {
        var columns = MappingResolver.Resolve<Sample>();

        columns.Should().NotContain(c => c.Property.Name == nameof(Sample.Unmapped));
    }

    [Fact]
    public void Resolve_ImportOnlyAttribute_IsImportableNotExportable()
    {
        var columns = MappingResolver.Resolve<Sample>();

        var field = columns.Single(c => c.Property.Name == nameof(Sample.ImportOnlyField));
        field.Importable.Should().BeTrue();
        field.Exportable.Should().BeFalse();
    }

    [Fact]
    public void Resolve_ExportOnlyAttribute_IsExportableNotImportable()
    {
        var columns = MappingResolver.Resolve<Sample>();

        var field = columns.Single(c => c.Property.Name == nameof(Sample.ExportOnlyField));
        field.Exportable.Should().BeTrue();
        field.Importable.Should().BeFalse();
    }

    [Fact]
    public void ExportColumns_SortsByOrderAscending()
    {
        var exportColumns = MappingResolver.Resolve<Sample>().ExportColumns();

        // Order 1,2,3,4 — 仅导出字段(Order=4)排最后
        exportColumns.Select(c => c.Property.Name)
            .Should().Equal(
                nameof(Sample.Code),
                nameof(Sample.Name),
                nameof(Sample.CreateTime),
                nameof(Sample.ExportOnlyField));
    }

    [Fact]
    public void ImportColumns_ReturnsOnlyImportable()
    {
        var importColumns = MappingResolver.Resolve<Sample>().ImportColumns();

        importColumns.Select(c => c.Property.Name)
            .Should().BeEquivalentTo(new[]
            {
                nameof(Sample.Code),
                nameof(Sample.Name),
                nameof(Sample.ImportOnlyField),
            });
    }

    [Fact]
    public void Resolve_FluentOverridesAttribute()
    {
        var fluent = ImportMapping<Sample>.Create(b => b
            .Map(x => x.Code).ToColumn("Code2").WithOrder(0)
            .Map(x => x.Name));

        var columns = MappingResolver.Resolve<Sample>(fluent);

        var code = columns.Single(c => c.Property.Name == nameof(Sample.Code));
        code.ColumnName.Should().Be("Code2");          // fluent 覆盖
        code.ExportOrder.Should().Be(0);

        // 未在 fluent 声明的 CreateTime 仍来自 attribute
        columns.Should().Contain(c => c.Property.Name == nameof(Sample.CreateTime)
                                      && c.Format == "yyyy-MM-dd");
    }

    [Fact]
    public void Resolve_CachesAttributeReflection()
    {
        var first = MappingResolver.Resolve<Sample>();
        var second = MappingResolver.Resolve<Sample>();

        first.Should().BeSameAs(second, "attribute 映射只反射一次并缓存");
    }
}
