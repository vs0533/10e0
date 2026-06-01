using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenE0.Core.DataContext;
using TenE0.Core.DynamicFilters.Storage;

namespace TenE0.Core.DynamicFilters;

/// <summary>
/// <see cref="IDynamicFilterProvider"/> 的默认实现。
///
/// 使用原始 ADO.NET 连接读取过滤规则（绕过 DbContext，避免 OnModelCreating 递归），
/// 然后在 OnModelCreating 阶段通过 <see cref="FilterExpressionBuilder"/> 将 JSON 规则
/// 编译为 EF Named Query Filter 注册到模型中。
/// </summary>
public sealed class DynamicFilterProvider(ILogger<DynamicFilterProvider> logger) : IDynamicFilterProvider
{
    private List<TenE0DataFilterRule> _rules = [];

    // ------------------------------------------------------------------
    // 规则加载（ADO.NET 直连，不走 DbContext）
    // ------------------------------------------------------------------

    public async Task LoadRulesAsync(string connectionString, string providerName, CancellationToken ct = default)
    {
        try
        {
            using var connection = CreateDbConnection(connectionString, providerName);
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            // 使用标准 SQL；列名均为普通标识符，无需引号，兼容 SQL Server / PostgreSQL / MySQL / SQLite
            cmd.CommandText =
                "SELECT Id, EntityTypeName, RuleJson, IsEnabled, Description " +
                "FROM DataFilterRules WHERE IsEnabled = 1";

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var rules = new List<TenE0DataFilterRule>();
            while (await reader.ReadAsync(ct))
            {
                rules.Add(new TenE0DataFilterRule
                {
                    Id = reader.GetString(0),
                    EntityTypeName = reader.GetString(1),
                    RuleJson = reader.GetString(2),
                    IsEnabled = true,
                    Description = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }

            _rules = rules;
            logger.LogInformation("已加载 {Count} 条动态数据过滤规则", rules.Count);
        }
        catch (Exception ex)
        {
            // InMemory 数据库、连接失败、表不存在等情况均 graceful 降级为空规则集
            logger.LogWarning(ex, "无法从数据库加载动态过滤规则，将以空规则集运行");
            _rules = [];
        }
    }

    // ------------------------------------------------------------------
    // 在 OnModelCreating 中注册过滤器
    // ------------------------------------------------------------------

    public void ApplyDynamicFilters(ModelBuilder modelBuilder, BaseDataContext context)
    {
        if (_rules.Count == 0) return;

        var contextType = context.GetType();

        // 按实体类型分组，每个实体可能有多个独立规则
        var rulesByEntity = _rules
            .Where(r => r.IsEnabled)
            .GroupBy(r => r.EntityTypeName);

        foreach (var group in rulesByEntity)
        {
            // 在当前模型中查找对应的实体类型
            var entityType = modelBuilder.Model.GetEntityTypes()
                .FirstOrDefault(e => e.ClrType.FullName == group.Key);

            if (entityType is null)
            {
                logger.LogWarning(
                    "实体类型 '{TypeName}' 在当前 DbContext 模型中不存在，跳过其过滤规则",
                    group.Key);
                continue;
            }

            // 为每条规则注册独立的 Named Query Filter
            foreach (var rule in group)
            {
                try
                {
                    var filter = FilterExpressionBuilder.Build(
                        rule.RuleJson,
                        entityType.ClrType,
                        contextType,
                        context);

                    if (filter is null) continue;

                    var filterName = $"DynamicFilter:{rule.Id}";
                    entityType.SetQueryFilter(filterName, filter);

                    logger.LogDebug(
                        "已注册动态过滤器 '{FilterName}' → 实体 '{EntityType}'",
                        filterName, entityType.ClrType.Name);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "构建过滤规则 '{RuleId}'（实体 '{EntityType}'）失败，已跳过",
                        rule.Id, entityType.ClrType.Name);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // ADO.NET 连接工厂
    // ------------------------------------------------------------------

    /// <summary>
    /// 根据 providerName 创建对应的 ADO.NET 连接。
    ///
    /// 优先查询 <see cref="DbProviderFactories"/> 注册表；
    /// 若未注册则回退到已知提供程序的反射发现（Instance 单例字段）。
    /// </summary>
    private static DbConnection CreateDbConnection(string connectionString, string providerName)
    {
        var factory = ResolveFactory(providerName);
        var conn = factory.CreateConnection()
            ?? throw new InvalidOperationException($"无法为提供程序 '{providerName}' 创建数据库连接");
        conn.ConnectionString = connectionString;
        return conn;
    }

    /// <summary>
    /// 解析 DbProviderFactory：注册表 → 已知提供程序反射回退。
    /// </summary>
    private static DbProviderFactory ResolveFactory(string providerName)
    {
        // 1. 尝试 DbProviderFactories 注册表（调用方可在启动时 RegisterFactory）
        try
        {
            return DbProviderFactories.GetFactory(providerName);
        }
        catch
        {
            // 未注册，继续尝试回退
        }

        // 2. 已知 EF Core 提供程序 → 反射获取 Instance 单例
        if (s_knownFactories.TryGetValue(providerName, out var factoryTypeRef))
        {
            var type = Type.GetType(factoryTypeRef, throwOnError: false);
            var instance = type?
                .GetField("Instance", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null) as DbProviderFactory;

            if (instance is not null) return instance;
        }

        throw new NotSupportedException(
            $"数据库提供程序 '{providerName}' 未注册。" +
            "请在应用启动时调用 DbProviderFactories.RegisterFactory() 注册对应工厂，" +
            "或在 Add10E0Core 配置中使用受支持的提供程序名称。");
    }

    /// <summary>
    /// 已知 EF Core 数据库提供程序 → 其 DbProviderFactory 类型的 AssemblyQualifiedName。
    /// 用于在未显式注册 DbProviderFactories 时通过反射回退发现工厂单例。
    /// </summary>
    private static readonly Dictionary<string, string> s_knownFactories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Data.SqlClient"] =
            "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient",
        ["SqlServer"] =
            "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient",
        ["Npgsql"] =
            "Npgsql.NpgsqlFactory, Npgsql",
        ["PostgreSQL"] =
            "Npgsql.NpgsqlFactory, Npgsql",
        ["MySqlConnector"] =
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector",
        ["MySql"] =
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector",
        ["Microsoft.Data.Sqlite"] =
            "Microsoft.Data.Sqlite.SqliteFactory, Microsoft.Data.Sqlite",
        ["SQLite"] =
            "Microsoft.Data.Sqlite.SqliteFactory, Microsoft.Data.Sqlite",
    };
}
