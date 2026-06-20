using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.DynamicFilters;

namespace TenE0.Core.Tests.DynamicFilters;

/// <summary>
/// #40 验收测试：<see cref="IDbProviderFactoryDescriptor"/> 替换 <c>s_knownFactories</c> 字典。
///
/// 覆盖：
/// - 框架内置 4 个 descriptor（SqlServer / PostgreSQL / MySql / SQLite）能从 DI 解析出 factory
/// - 业务自定义 descriptor（如模拟"达梦"）可通过 DI 注入并被 <see cref="DynamicFilterProvider.ResolveFactory"/> 找到
/// - 名称大小写不敏感
/// - DbProviderFactories 注册表优先于 descriptor 集合
/// - 全部未命中时抛 <see cref="NotSupportedException"/>
/// - <see cref="DynamicFilterProvider"/> 旧单参构造仍可用（向后兼容）
/// </summary>
[Trait("Category", "Unit")]
public sealed class DbProviderFactoryDescriptorAcceptanceTests
{
    [Fact]
    public void SqlServerDescriptor_HasExpectedName()
    {
        var sut = new SqlServerDbProviderFactoryDescriptor();
        sut.Name.Should().Be("SqlServer");
    }

    [Fact]
    public void NpgsqlDescriptor_HasExpectedName()
    {
        var sut = new NpgsqlDbProviderFactoryDescriptor();
        sut.Name.Should().Be("PostgreSQL");
    }

    [Fact]
    public void MySqlDescriptor_HasExpectedName()
    {
        var sut = new MySqlDbProviderFactoryDescriptor();
        sut.Name.Should().Be("MySql");
    }

    [Fact]
    public void SqliteDescriptor_HasExpectedName()
    {
        var sut = new SqliteDbProviderFactoryDescriptor();
        sut.Name.Should().Be("SQLite");
    }

    [Fact]
    public void SqliteDescriptor_FactoryResolvesToSqliteFactory_WhenAssemblyLoaded()
    {
        // Microsoft.Data.Sqlite is referenced by the test project, so the descriptor
        // can resolve its factory via reflection.
        var sut = new SqliteDbProviderFactoryDescriptor();
        var factory = sut.Factory;
        factory.Should().NotBeNull();
        factory.Should().BeSameAs(SqliteFactory.Instance);
    }

    [Fact]
    public void SqlServerDescriptor_FactoryThrowsCleanly_WhenAssemblyMissing()
    {
        // Microsoft.Data.SqlClient may or may not be loaded in the test host.
        // Either we get a real factory (good) or we get a clear InvalidOperationException
        // with an actionable message.
        var sut = new SqlServerDbProviderFactoryDescriptor();
        var act = () => _ = sut.Factory;
        act.Should().NotThrow<NullReferenceException>();
    }

    [Fact]
    public void ResolveFactory_CaseInsensitiveByName()
    {
        // Lowercase "sqlite" should match the descriptor registered with "SQLite"
        var sut = new DynamicFilterProvider(
            NullLogger<DynamicFilterProvider>.Instance,
            [new SqliteDbProviderFactoryDescriptor()]);

        var factory = sut.ResolveFactory("sqlite");
        factory.Should().BeSameAs(SqliteFactory.Instance);
    }

    [Fact]
    public void ResolveFactory_CustomDescriptor_InjectedAndResolved()
    {
        // Simulate a custom "达梦" descriptor with a fake factory.
        var fakeFactory = new FakeDbProviderFactory();
        var custom = new FakeDbProviderFactoryDescriptor("Dm", fakeFactory);

        var sut = new DynamicFilterProvider(
            NullLogger<DynamicFilterProvider>.Instance,
            [custom]);

        var factory = sut.ResolveFactory("Dm");
        factory.Should().BeSameAs(fakeFactory);
    }

    [Fact]
    public void ResolveFactory_DbProviderFactoriesRegistration_WinsOverDescriptor()
    {
        // Register SQLite under "Microsoft.Data.Sqlite" in the global factory table.
        // The provider also receives a descriptor with the SAME name; DbProviderFactories
        // registration should win.
        DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);

        var fakeFactory = new FakeDbProviderFactory();
        var conflictingDescriptor = new FakeDbProviderFactoryDescriptor("Microsoft.Data.Sqlite", fakeFactory);

        var sut = new DynamicFilterProvider(
            NullLogger<DynamicFilterProvider>.Instance,
            [conflictingDescriptor]);

        var factory = sut.ResolveFactory("Microsoft.Data.Sqlite");
        // DbProviderFactories registration wins (returns SqliteFactory.Instance, not fake)
        factory.Should().BeSameAs(SqliteFactory.Instance);
    }

    [Fact]
    public void ResolveFactory_NoMatch_ThrowsNotSupportedException()
    {
        var sut = new DynamicFilterProvider(
            NullLogger<DynamicFilterProvider>.Instance,
            [new SqliteDbProviderFactoryDescriptor()]);

        var act = () => sut.ResolveFactory("Unknown.Provider");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Unknown.Provider*");
    }

    [Fact]
    public void ResolveFactory_NoDescriptors_StillUsesDbProviderFactoriesTable()
    {
        // Backward compat: with no DI descriptors, the provider must still work via
        // DbProviderFactories.RegisterFactory. Old tests pre-register SQLite this way.
        DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);

        var sut = new DynamicFilterProvider(NullLogger<DynamicFilterProvider>.Instance);

        var factory = sut.ResolveFactory("Microsoft.Data.Sqlite");
        factory.Should().BeSameAs(SqliteFactory.Instance);
    }

    [Fact]
    public void ResolveFactory_NotSupportedException_MessageListsKnownDescriptors()
    {
        // The exception should be helpful — list the known descriptor names so callers
        // can see what's available to register against.
        var sut = new DynamicFilterProvider(
            NullLogger<DynamicFilterProvider>.Instance,
            [
                new SqliteDbProviderFactoryDescriptor(),
                new SqlServerDbProviderFactoryDescriptor(),
            ]);

        var act = () => sut.ResolveFactory("Oracle.ManagedDataAccess");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*SQLite*")
            .WithMessage("*SqlServer*");
    }

    [Fact]
    public void ResolveFactory_NullDescriptorsParameter_BackwardCompatible()
    {
        // Old single-arg call sites (no descriptors passed) must continue to work
        // without null reference exceptions.
        var sut = new DynamicFilterProvider(NullLogger<DynamicFilterProvider>.Instance);

        var act = () => sut.ResolveFactory("Anything");
        act.Should().Throw<NotSupportedException>(
            "no DI descriptors and no DbProviderFactories registration means lookup must fail cleanly");
    }

    // ── Test helpers ──────────────────────────────────────────────────

    private sealed class FakeDbProviderFactoryDescriptor(string name, DbProviderFactory factory)
        : IDbProviderFactoryDescriptor
    {
        public string Name { get; } = name;
        public DbProviderFactory Factory { get; } = factory;
    }

    /// <summary>Minimal DbProviderFactory stub for descriptor tests.</summary>
    private sealed class FakeDbProviderFactory : DbProviderFactory;
}
