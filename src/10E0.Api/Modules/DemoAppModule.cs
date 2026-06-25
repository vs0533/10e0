using TenE0.Api.Domain;
using TenE0.Api.Hosting;
using TenE0.Api.Seeders;
using TenE0.Core.Configuration;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Hosting;
using TenE0.Core.Workflow.Definitions;

namespace TenE0.Api.Modules;

/// <summary>
/// Demo 项目的业务模块装配（issue #160 示范）。
///
/// <para>
/// <b>职责划分</b>（与 <see cref="ServiceCollectionExtensions.AddTenE0All{TContext}"/> 协同）：
/// <list type="bullet">
/// <item><c>AddTenE0All</c>（框架）：注册 Core / EntityService / DataContext / Cqrs / Permissions /
///   Identity / Menus / Sequences / DomainEvents / DynamicFilters / Configuration（基础套件）+
///   Files / Auditing / ImportExport / Realtime / Workflow（按需）。</item>
/// <item><see cref="DemoAppModule"/>（业务）：注册 demo 专属的 seeder、InMemory provider 装配器、
///   AssigneeDirectory、系统参数定义。</item>
/// </list>
/// </para>
///
/// <para>
/// 这正是 <see cref="IAppModule"/> 的设计意图：框架入口只关心"注册哪些模块"，业务模块自包含。
/// 业务项目复制本文件改成自己的 seeder / DbContext / provider 装配器即可。
/// </para>
///
/// <para>
/// <b>注</b>：demo 的端点扩展（<c>MapDemoEndpoints</c> 等）签名在 <c>WebApplication</c> 上而非
/// <c>IEndpointRouteBuilder</c>，与 <see cref="IAppModule.MapEndpoints"/> 契约不兼容，故 demo 端点
/// 仍在 <c>Program.cs</c> 显式挂载。本模块只承载 DI 部分。
/// </para>
/// </summary>
public sealed class DemoAppModule : IAppModule
{
    public int Order => 100;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // demo 用 EF Core InMemory —— 通过 IDbProviderConfigurator SPI 接入 AddTenE0All 的
        // 连接串重载（Core 不引用 provider 包，装配在 app 层）。
        // 生产项目把它换成 SqlServerDbProviderConfigurator / NpgsqlConfigurator 等。
        services.AddTenE0DbProviderConfigurator(new InMemoryDbProviderConfigurator("10E0-demo-perm"));

        // #153：注册 Demo 声明的系统参数定义（供 SystemParameterStore 校验 + Seeder 落库）
        foreach (var def in SystemParameterDefinitions.All)
            services.AddSingleton<ISystemParameterDefinition>(def);

        // AssigneeDirectory：把"角色/组织 → 用户"查询从 Core 解耦到 Api 层（工作流用）
        services.AddScoped<IAssigneeDirectory, AssigneeDirectory<DemoDbContext>>();

        // Seeder：初始权限授予 + 管理员账号 + 组织树 + 菜单 + 系统参数
        services.AddScoped<IDataSeeder, PermissionSeeder>();
        services.AddScoped<IDataSeeder, AuthSeeder>();
        services.AddScoped<IDataSeeder, MenuSeeder>();
        services.AddScoped<IDataSeeder, ConfigurationSeeder>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // demo 端点扩展签名在 WebApplication 上（见类注释），此处不挂载 —— 由 Program.cs 调用。
    }
}

/// <summary>
/// EF Core InMemory provider 装配器 —— 仅供 demo / 测试场景（issue #160 SPI）。
/// 生产不应使用 InMemory（无事务、无关系语义）。
/// </summary>
internal sealed class InMemoryDbProviderConfigurator : IDbProviderConfigurator
{
    private readonly string _databaseName;

    public InMemoryDbProviderConfigurator(string databaseName)
    {
        _databaseName = databaseName;
    }

    public DatabaseProvider Provider => DatabaseProvider.InMemory;

    public void Configure(IServiceProvider services, Microsoft.EntityFrameworkCore.DbContextOptionsBuilder options, string connectionString)
        => Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions.UseInMemoryDatabase(options, _databaseName);
}
