using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.DependencyInjection;

namespace TenE0.Core.Tests.DependencyInjection;

[Trait("Category", "Unit")]
public sealed class ConnectionStringProbeTests
{
    [Theory]
    [InlineData("Server=localhost;Database=App;User Id=sa;Password=Pw;", DatabaseProvider.SqlServer)]
    [InlineData("Data Source=localhost;Initial Catalog=App;User Id=sa;", DatabaseProvider.SqlServer)]
    [InlineData("Host=localhost;Port=5432;Database=App;Username=app;", DatabaseProvider.PostgreSQL)]
    [InlineData("Host=pg;Port=5432;Database=App;UserID=app;", DatabaseProvider.PostgreSQL)]
    [InlineData("Data Source=app.db", DatabaseProvider.SQLite)]
    [InlineData("Data Source=:memory:", DatabaseProvider.SQLite)]
    [InlineData("Data Source=app.sqlite", DatabaseProvider.SQLite)]
    public void TryDetect_RecognizesKnownProviders(string connectionString, DatabaseProvider expected)
    {
        var ok = ConnectionStringProbe.TryDetect(connectionString, out var provider);

        ok.Should().BeTrue();
        provider.Should().Be(expected);
    }

    [Fact]
    public void TryDetect_ReturnsFalse_ForEmptyOrUnrecognized()
    {
        ConnectionStringProbe.TryDetect("", out var p1).Should().BeFalse();
        p1.Should().BeNull();

        ConnectionStringProbe.TryDetect("  ", out var p2).Should().BeFalse();
        p2.Should().BeNull();

        // 没有任何已知关键词 —— 探测失败
        ConnectionStringProbe.TryDetect("gibberish-no-equals-or-known-keys", out var p3).Should().BeFalse();
        p3.Should().BeNull();
    }

    [Fact]
    public void Detect_Throws_WhenNotRecognized()
    {
        var act = () => ConnectionStringProbe.Detect("totally-unknown-format");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*无法从连接串自动探测 database provider*");
    }

    [Fact]
    public void Detect_DoesNotLeakFullConnectionStringInException()
    {
        // 连接串故意写成不可识别形式以触发异常 —— 异常消息只暴露首个 key + 掩码，
        // 绝不含 value 字符（value 可能是密码 / 凭证）。
        var act = () => ConnectionStringProbe.Detect("weird=SUPERSECRET123;");

        var ex = act.Should().Throw<InvalidOperationException>().Subject.First();
        // 只允许 "weird=***"（key + = + 掩码），不允许任何 value 字符
        ex.Message.Should().Contain("weird=***");
        ex.Message.Should().NotContain("SUPERSECRET123");
        ex.Message.Should().NotContain("SUPER");
    }
}

[Trait("Category", "Unit")]
public sealed class AddTenE0DataContextOverloadsTests
{
    private sealed class ProbeDbContext : DbContext
    {
        public ProbeDbContext(DbContextOptions<ProbeDbContext> options) : base(options) { }
    }

    /// <summary>测试用 InMemory 装配器 —— Core 不引用 InMemory 包，测试项目引用了。</summary>
    private sealed class TestInMemoryConfigurator : IDbProviderConfigurator
    {
        public DatabaseProvider Provider => DatabaseProvider.InMemory;
        public void Configure(IServiceProvider services, DbContextOptionsBuilder options, string connectionString)
            => options.UseInMemoryDatabase(connectionString);
    }

    // AddTenE0DataContext 的 optionsAction 解析 AuditInterceptor（由 AddTenE0Core 注册），
    // 故测试需先 AddTenE0Core —— 与 AddTenE0All 的真实调用顺序一致。
    private static ServiceCollection NewServicesWithCore()
    {
        var services = new ServiceCollection();
        services.AddTenE0Core();
        services.AddTenE0DbProviderConfigurator(new TestInMemoryConfigurator());
        return services;
    }

    [Fact]
    public void ConnectionStringOverload_RegistersDbContextFactory_WhenProviderConfigured()
    {
        var services = NewServicesWithCore();

        services.AddTenE0DataContext<ProbeDbContext>("probe-db", DatabaseProvider.InMemory);

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetService<IDbContextFactory<ProbeDbContext>>();
        factory.Should().NotBeNull();
    }

    [Fact]
    public void ConnectionStringOverload_Throws_WhenNoConfiguratorForProvider()
    {
        var services = new ServiceCollection();
        services.AddTenE0Core();
        // 未注册 SqlServer 装配器 —— DbContextOptions 装配时（解析 factory 即触发）失败

        services.AddTenE0DataContext<ProbeDbContext>("probe-db", DatabaseProvider.SqlServer);

        using var sp = services.BuildServiceProvider();
        // DbContextFactory 是 Singleton，首次解析时构建 optionsAction → 触发 provider 装配失败
        var act = () => sp.GetRequiredService<IDbContextFactory<ProbeDbContext>>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*未注册 SqlServer 的 IDbProviderConfigurator*");
    }

    [Fact]
    public void ConnectionStringOverload_AutoDetectsProvider_WhenExplicitNull()
    {
        // 连接串形式不可识别 + provider=null → 探测失败立即抛（注册阶段就抛）
        var services = new ServiceCollection();

        var act = () => services.AddTenE0DataContext<ProbeDbContext>("weird-format", provider: null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ConnectionStringOverload_AppliesExtraConfigure_AfterProvider()
    {
        var services = NewServicesWithCore();
        var extraCalled = false;

        services.AddTenE0DataContext<ProbeDbContext>(
            "probe-db",
            DatabaseProvider.InMemory,
            options => { extraCalled = true; });

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IDbContextFactory<ProbeDbContext>>().CreateDbContext();

        extraCalled.Should().BeTrue();
    }

    [Fact]
    public void AddTenE0DbProviderConfigurator_RegistersAdditively_MultipleProviders()
    {
        // Code Review 🔴 Critical 回归测试：AddTenE0DbProviderConfigurator 必须累加（Add）而非替换（Replace），
        // 否则多 provider 场景（迁移工具 / 分库）只有最后一个装配器存活，与 TryConfigure 的 GetServices 遍历语义冲突。
        var services = new ServiceCollection();

        services.AddTenE0DbProviderConfigurator(new ProviderSpecificConfigurator(DatabaseProvider.SqlServer));
        services.AddTenE0DbProviderConfigurator(new ProviderSpecificConfigurator(DatabaseProvider.InMemory));

        using var sp = services.BuildServiceProvider();
        sp.GetServices<IDbProviderConfigurator>()
            .Should().HaveCount(2)
            .And.Contain(c => ((ProviderSpecificConfigurator)c!).Provider == DatabaseProvider.SqlServer)
            .And.Contain(c => ((ProviderSpecificConfigurator)c!).Provider == DatabaseProvider.InMemory);
    }

    /// <summary>测试用：按构造参数决定 Provider 的装配器，便于在单测里注册多个不同 Provider。</summary>
    private sealed class ProviderSpecificConfigurator : IDbProviderConfigurator
    {
        private readonly DatabaseProvider _provider;
        public ProviderSpecificConfigurator(DatabaseProvider provider) => _provider = provider;
        public DatabaseProvider Provider => _provider;
        public void Configure(IServiceProvider services, DbContextOptionsBuilder options, string connectionString) { }
    }

    [Fact]
    public void ConfigurationOverload_ReadsConnectionString_ByName()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "probe-db",
            })
            .Build();

        var services = NewServicesWithCore();

        services.AddTenE0DataContext<ProbeDbContext>(config, "Default", DatabaseProvider.InMemory);

        using var sp = services.BuildServiceProvider();
        sp.GetService<IDbContextFactory<ProbeDbContext>>().Should().NotBeNull();
    }

    [Fact]
    public void ConfigurationOverload_Throws_WhenConnectionStringMissing()
    {
        var config = new ConfigurationBuilder().Build(); // 空 —— 无连接串
        var services = new ServiceCollection();

        var act = () => services.AddTenE0DataContext<ProbeDbContext>(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*未找到 ConnectionStrings:Default*");
    }
}

[Trait("Category", "Unit")]
public sealed class AddTenE0AllTests
{
    private sealed class AllDbContext : DbContext
    {
        public AllDbContext(DbContextOptions<AllDbContext> options) : base(options) { }
    }

    private static Action<TenE0IdentityOptions> ValidIdentity => identity =>
    {
        identity.Jwt.SigningKey = new string('x', 48); // ≥32 字节
        identity.Jwt.Issuer = "Test";
        identity.Jwt.Audience = "Test";
    };

    private static IConfiguration ConfigWithDefault(string cs = "all-db") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = cs,
            })
            .Build();

    private static void AddInMemoryConfigurator(IServiceCollection services) =>
        services.AddTenE0DbProviderConfigurator(new InMemoryConfigurator());

    private sealed class InMemoryConfigurator : IDbProviderConfigurator
    {
        public DatabaseProvider Provider => DatabaseProvider.InMemory;
        public void Configure(IServiceProvider services, DbContextOptionsBuilder options, string connectionString)
            => options.UseInMemoryDatabase(connectionString);
    }

    [Fact]
    public void AddTenE0All_Throws_WhenIdentityNotConfigured()
    {
        var services = new ServiceCollection();
        AddInMemoryConfigurator(services);

        var act = () => services.AddTenE0All<AllDbContext>(ConfigWithDefault(), opt =>
        {
            opt.Provider = DatabaseProvider.InMemory; // 跳过连接串探测，直达 Identity 校验
            opt.Identity = null;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Identity 必须配置*");
    }

    [Fact]
    public void AddTenE0All_RegistersDefaultEnabledModules()
    {
        var services = new ServiceCollection();
        AddInMemoryConfigurator(services);

        services.AddTenE0All<AllDbContext>(ConfigWithDefault(), opt =>
        {
            opt.Provider = DatabaseProvider.InMemory;
            opt.Identity = ValidIdentity;
        });

        // 基础套件默认启用：菜单 / 流水号 / 领域事件 / 动态过滤 / 配置
        services.Should().Contain(s => s.ServiceType == typeof(TenE0.Core.Menus.IMenuService));
        services.Should().Contain(s => s.ServiceType == typeof(TenE0.Core.Sequences.ISequenceGenerator));
        services.Should().Contain(s => s.ServiceType == typeof(TenE0.Core.Events.IDomainEventDispatcher));
        services.Should().Contain(s => s.ServiceType == typeof(TenE0.Core.DynamicFilters.IDynamicFilterProvider));
        services.Should().Contain(s => s.ServiceType == typeof(TenE0.Core.Configuration.IDataDictionaryService));

        // DataContext factory 已注册
        services.Should().Contain(s => s.ServiceType == typeof(IDbContextFactory<AllDbContext>));
    }

    [Fact]
    public void AddTenE0All_DoesNotRegisterOptInModules_ByDefault()
    {
        var services = new ServiceCollection();
        AddInMemoryConfigurator(services);

        services.AddTenE0All<AllDbContext>(ConfigWithDefault(), opt =>
        {
            opt.Provider = DatabaseProvider.InMemory;
            opt.Identity = ValidIdentity;
            // Files / Auditing / ImportExport / Realtime / Workflow 均默认 false
        });

        services.Should().NotContain(s => s.ServiceType == typeof(TenE0.Core.Files.IFileService));
        services.Should().NotContain(s => s.ServiceType == typeof(TenE0.Core.Auditing.IAuditLogStore));
        services.Should().NotContain(s => s.ServiceType == typeof(TenE0.Core.ImportExport.IExcelExporter));
    }

    [Fact]
    public void AddTenE0All_RegistersOptInModules_WhenEnabled()
    {
        var services = new ServiceCollection();
        AddInMemoryConfigurator(services);

        services.AddTenE0All<AllDbContext>(ConfigWithDefault(), opt =>
        {
            opt.Provider = DatabaseProvider.InMemory;
            opt.Identity = ValidIdentity;
            opt.Files = true;
            opt.Auditing = true;
            opt.ImportExport = true;
            opt.Realtime = true;
        });

        services.Should().Contain(s => s.ServiceType == typeof(TenE0.Core.Files.IFileService));
        services.Should().Contain(s => s.ServiceType == typeof(TenE0.Core.Auditing.IAuditLogStore));
        services.Should().Contain(s => s.ServiceType == typeof(TenE0.Core.ImportExport.IExcelExporter));
        // SignalR：AddTenE0Realtime 注册 IRealtimeNotifier
        services.Should().Contain(s => s.ServiceType == typeof(TenE0.Core.Realtime.IRealtimeNotifier));
    }

    [Fact]
    public void AddTenE0All_DisablingDefaults_RemovesModuleRegistrations()
    {
        var services = new ServiceCollection();
        AddInMemoryConfigurator(services);

        services.AddTenE0All<AllDbContext>(ConfigWithDefault(), opt =>
        {
            opt.Provider = DatabaseProvider.InMemory;
            opt.Identity = ValidIdentity;
            opt.Menus = false;
            opt.Sequences = false;
            opt.DomainEvents = false;
            opt.DynamicFilters = false;
            opt.Configuration = false;
        });

        services.Should().NotContain(s => s.ServiceType == typeof(TenE0.Core.Menus.IMenuService));
        services.Should().NotContain(s => s.ServiceType == typeof(TenE0.Core.Sequences.ISequenceGenerator));
        services.Should().NotContain(s => s.ServiceType == typeof(TenE0.Core.DynamicFilters.IDynamicFilterProvider));
    }

    [Fact]
    public void AddTenE0All_Throws_WhenNoConnectionString()
    {
        var services = new ServiceCollection();
        AddInMemoryConfigurator(services);
        var emptyConfig = new ConfigurationBuilder().Build(); // 无 ConnectionStrings:Default

        var act = () => services.AddTenE0All<AllDbContext>(emptyConfig, opt =>
        {
            opt.Provider = DatabaseProvider.InMemory;
            opt.Identity = ValidIdentity;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionString*");
    }

    [Fact]
    public void AddTenE0All_UsesExplicitConnectionString_WhenProvided()
    {
        var services = new ServiceCollection();
        AddInMemoryConfigurator(services);

        services.AddTenE0All<AllDbContext>(ConfigWithDefault("should-be-overridden"), opt =>
        {
            opt.Provider = DatabaseProvider.InMemory;
            opt.ConnectionString = "explicit-db";
            opt.Identity = ValidIdentity;
        });

        // 注册成功即可（无异常） —— 显式 connectionString 优先
        services.Should().Contain(s => s.ServiceType == typeof(IDbContextFactory<AllDbContext>));
    }

    [Fact]
    public void AddTenE0All_WithCustomUserType_RegistersIdentityForUser()
    {
        var services = new ServiceCollection();
        AddInMemoryConfigurator(services);

        services.AddTenE0All<CustomUser, AllDbContext>(ConfigWithDefault(), opt =>
        {
            opt.Provider = DatabaseProvider.InMemory;
            opt.Identity = ValidIdentity;
        });

        // IJwtTokenService 由 AddTenE0JwtAuth<TUser,TContext> 注册
        services.Should().Contain(s => s.ServiceType == typeof(TenE0.Core.Auth.Jwt.Services.IJwtTokenService));
    }

    /// <summary>用于验证 TUser 泛型重载的自定义用户类型。</summary>
    private sealed class CustomUser : TenE0User { }
}
