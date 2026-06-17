using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.DynamicFilters;
using TenE0.Core.DynamicFilters.Storage;

namespace TenE0.Api.Hosting;

/// <summary>
/// 启动时引导逻辑：从 DbContext 检测数据库 provider，决定是否加载动态过滤规则。
/// InMemory provider 不支持关系型 SQL，跳过加载。
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
        // 将 EF provider name 映射为 ADO.NET provider invariant name
        // Microsoft.EntityFrameworkCore.SqlServer → Microsoft.Data.SqlClient
        // (System.Data.SqlClient 已 archive，.NET 10 / EF Core 10 默认使用 Microsoft.Data.SqlClient)
        if (string.IsNullOrEmpty(connStr)) return;

        var adoProvider = providerName switch
        {
            "Microsoft.EntityFrameworkCore.SqlServer" => "Microsoft.Data.SqlClient",
            "Npgsql.EntityFrameworkCore.PostgreSQL" => "Npgsql",
            "Pomelo.EntityFrameworkCore.MySql" => "MySqlConnector",
            _ => providerName
        };
        await filterProvider.LoadRulesAsync(connStr, adoProvider, ct);
    }
}
