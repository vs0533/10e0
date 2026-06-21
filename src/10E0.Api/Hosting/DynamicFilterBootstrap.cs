using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.DynamicFilters;

namespace TenE0.Api.Hosting;

/// <summary>
/// 启动时引导逻辑：从 DbContext 检测数据库 provider，决定是否加载动态过滤规则。
/// InMemory provider 不支持关系型 SQL，跳过加载。
///
/// EF Core provider name 到 ADO.NET provider invariant name 的映射优先使用
/// DI 注入的 <see cref="IDbProviderFactoryDescriptor"/> 集合（按约定名匹配），
/// 未命中时回退到内置别名表（兼容旧 EF provider name）。
/// </summary>
internal static class DynamicFilterBootstrap
{
    public static async Task LoadRulesAsync(WebApplication app, CancellationToken ct = default)
    {
        using var scope = app.Services.CreateScope();
        var filterProvider = scope.ServiceProvider.GetRequiredService<IDynamicFilterProvider>();
        // 从 DbContext 获取连接信息来加载规则
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
        using var ctx = await contextFactory.CreateDbContextAsync(ct);
        var providerName = ctx.Database.ProviderName ?? "";
        // InMemory 数据库不支持关系型方法，跳过动态过滤规则加载
        if (providerName.Contains("InMemory"))
        {
            Console.WriteLine("[DynamicFilters] Skipping rule load: InMemory database");
            return;
        }

        var connStr = ctx.Database.GetConnectionString() ?? "";
        if (string.IsNullOrEmpty(connStr)) return;

        // 将 EF provider name 映射为 ADO.NET provider invariant name
        // 1. 尝试 DI 注入的 descriptor 集合（按 descriptor.Name 匹配 EF provider 别名）
        var descriptors = scope.ServiceProvider
            .GetServices<IDbProviderFactoryDescriptor>()
            .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        // 2. 回退到内置别名表（覆盖 EF Core 包名 → ADO.NET invariant name 的映射，
        //    这是 descriptor 集合无法覆盖的"桥接"层：descriptor.Name 是 ADO.NET 名，
        //    EF Core provider name 是包名，需要显式桥接）
        var adoProvider = providerName switch
        {
            "Microsoft.EntityFrameworkCore.SqlServer" => "SqlServer",
            "Npgsql.EntityFrameworkCore.PostgreSQL" => "PostgreSQL",
            "Pomelo.EntityFrameworkCore.MySql" => "MySql",
            _ => providerName
        };

        // 3. 若 descriptor 集合恰好直接注册了 EF provider name（例如业务自定义注册），
        //    优先使用直接匹配
        if (!descriptors.ContainsKey(adoProvider) && descriptors.ContainsKey(providerName))
        {
            adoProvider = providerName;
        }

        await filterProvider.LoadRulesAsync(connStr, adoProvider, ct);
    }
}
