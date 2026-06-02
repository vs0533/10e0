using Microsoft.EntityFrameworkCore;
using TenE0.Core.Auth.Jwt.Storage;

namespace TenE0.Core.Tests.Auth.Jwt.Storage;

[Trait("Category", "Unit")]
public sealed class AuthModelBuilderExtensionsTests
{
    private sealed class TestUser : TenE0User { }

    [Fact]
    public void ConfigureTenE0AuthTables_BuildsModelWithoutError()
    {
        var mb = new ModelBuilder();

        mb.ConfigureTenE0AuthTables<TestUser>();

        // Building the model covers all configuration lines
        mb.FinalizeModel();
    }

    [Fact]
    public void ConfigureTenE0AuthTables_SetsUniqueIndexOnUserCode()
    {
        var mb = new ModelBuilder();
        mb.ConfigureTenE0AuthTables<TestUser>();

        var entity = mb.Model.FindEntityType(typeof(TestUser));
        entity.Should().NotBeNull();
        var index = entity!.GetIndexes().FirstOrDefault(i => i.Properties.Any(p => p.Name == "UserCode") && i.IsUnique);
        index.Should().NotBeNull();
    }
}
