using TenE0.Core.Abstractions;

namespace TenE0.Core.Tests.Abstractions;

/// <summary>
/// BDD acceptance tests for #11 — Part 1 (Abstractions).
///
/// 验证业务契约：
/// - IMultiTenantEntity 必须存在
/// - 必须继承 IBaseEntity（与 SoftDelete/Timer 同族）
/// - 必须有可读可写的 TenantId 属性
///
/// 失败模式（红 → 绿）：未实现前 IMultiTenantEntity 符号不存在，
/// 整个测试文件无法编译 = 完美的 RED 信号。
/// </summary>
[Trait("Category", "BDD")]
public sealed class MultiTenantEntityAcceptanceTests
{
    [Fact]
    public void GivenMultiTenantInterfaceExists_WhenContractIsInspected_ThenItExtendsIBaseEntity()
    {
        // Arrange + Act — 反射取接口
        var iface = typeof(IMultiTenantEntity);

        // Assert
        iface.Should().NotBeNull(
            "issue #11 requires IMultiTenantEntity in TenE0.Core.Abstractions");
        iface!.GetInterfaces().Should().Contain(typeof(IBaseEntity),
            "multi-tenant entities must extend IBaseEntity like SoftDelete / Timer / Tree");
    }

    [Fact]
    public void GivenMultiTenantInterfaceExists_WhenContractIsInspected_ThenTenantIdIsReadableAndWritableString()
    {
        // Arrange
        var iface = typeof(IMultiTenantEntity);
        var prop = iface!.GetProperty(nameof(IMultiTenantEntity.TenantId));

        // Assert
        prop.Should().NotBeNull(
            "IMultiTenantEntity must declare a TenantId property");
        prop!.PropertyType.Should().Be(typeof(string),
            "TenantId is a string identifier (GUID / business code)");
        prop.CanRead.Should().BeTrue("TenantId must be readable for query filters");
        prop.CanWrite.Should().BeTrue("TenantId must be writable so business code can assign it on insert");
    }

    [Fact]
    public void GivenConcreteEntity_WhenImplementsIMultiTenantEntity_ThenItIsAlsoIBaseEntity()
    {
        // Arrange — 一个实现 IMultiTenantEntity 的最小业务实体
        var entityType = typeof(SampleTenantEntity);

        // Assert
        typeof(IMultiTenantEntity).IsAssignableFrom(entityType)
            .Should().BeTrue("business entity must implement IMultiTenantEntity");
        typeof(IBaseEntity).IsAssignableFrom(entityType)
            .Should().BeTrue("multi-tenant entity must remain a base entity (id, etc.)");
    }

    [Fact]
    public void GivenConcreteTenantEntity_WhenTenantIdIsAssigned_ThenValueRoundTrips()
    {
        // Arrange
        var entity = new SampleTenantEntity { Id = "e1", TenantId = "t-42", Name = "x" };

        // Act
        var tenantId = entity.TenantId;

        // Assert
        tenantId.Should().Be("t-42",
            "TenantId must round-trip — it's the value the Tenant query filter compares against");
    }

    // ── Local fixture entity used only to verify the contract ────────
    private sealed class SampleTenantEntity : IMultiTenantEntity
    {
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string? Name { get; set; }
    }
}
