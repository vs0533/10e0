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

    /// <summary>
    /// #123: RefreshToken.TokenHash 必须有 unique index —— 防止并发 Login 用同一 hash
    /// （理论上的 random collision）时两条记录都插入成功。DB 层 unique 约束是最后防线，
    /// 缺失会导致 refresh 路径 FirstOrDefaultAsync 可能命中多条 → 不可预测行为。
    /// 本测试守住此契约，防止重构时误删 IsUnique()。
    /// </summary>
    [Fact]
    public void ConfigureTenE0AuthTables_SetsUniqueIndexOnTokenHash()
    {
        var mb = new ModelBuilder();
        mb.ConfigureTenE0AuthTables<TestUser>();

        var entity = mb.Model.FindEntityType(typeof(TenE0RefreshToken));
        entity.Should().NotBeNull();
        var index = entity!.GetIndexes().FirstOrDefault(i => i.Properties.Any(p => p.Name == "TokenHash") && i.IsUnique);
        index.Should().NotBeNull(
            "TenE0RefreshToken.TokenHash 必须有 unique index —— refresh token 旋转链的完整性依赖它");
    }
}
