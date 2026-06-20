using TenE0.Core.DynamicFilters.Storage;
using TenE0.Core.Files.Storage;
using TenE0.Core.Menus.Storage;
using TenE0.Core.Organizations;
using TenE0.Core.Permissions.Storage;
using TenE0.Core.Sequences.Storage;

namespace TenE0.Core.Tests;

[Trait("Category", "Unit")]
public sealed class ModelBuilderExtensionsTests
{
    [Fact]
    public void ConfigureMenusTables_BuildsModel()
    {
        var mb = new ModelBuilder();
        mb.ConfigureTenE0MenuTables();
        mb.FinalizeModel();
    }

    [Fact]
    public void ConfigureFileAttachmentTables_BuildsModel()
    {
        var mb = new ModelBuilder();
        mb.ConfigureTenE0FileAttachmentTables();
        mb.FinalizeModel();
    }

    [Fact]
    public void ConfigurePermissionTables_BuildsModel()
    {
        var mb = new ModelBuilder();
        mb.ConfigureTenE0PermissionTables<TenE0Role>();
        mb.FinalizeModel();
    }

    [Fact]
    public void ConfigureOrgTables_BuildsModel()
    {
        var mb = new ModelBuilder();
        mb.ConfigureTenE0OrgTables();
        mb.FinalizeModel();
    }

    [Fact]
    public void ConfigureSequenceTables_BuildsModel()
    {
        var mb = new ModelBuilder();
        mb.ConfigureTenE0SequenceTables();
        mb.FinalizeModel();
    }

    [Fact]
    public void ConfigureDataFilterTables_BuildsModel()
    {
        var mb = new ModelBuilder();
        mb.ConfigureTenE0DataFilterTables();
        mb.FinalizeModel();
    }
}
