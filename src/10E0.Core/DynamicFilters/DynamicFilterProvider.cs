using System.Data.Common;
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
///
/// DbProviderFactory 解析走两层：
/// 1. <see cref="DbProviderFactories"/> 全局注册表（调用方可用 RegisterFactory 注册）
/// 2. DI 注入的 <see cref="IDbProviderFactoryDescriptor"/> 集合（框架默认注册 4 个内置 descriptor，
///    业务可注册自定义 descriptor 接入达梦 / 人大金仓 / OceanBase 等国产 DB）
/// </summary>
public sealed class DynamicFilterProvider : IDynamicFilterProvider
{
    private readonly ILogger<DynamicFilterProvider> _logger;
    private readonly IReadOnlyDictionary<string, IDbProviderFactoryDescriptor> _descriptorsByName;
    private List<TenE0DataFilterRule> _rules = [];

    /// <summary>
    /// 构造默认实例。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="descriptors">
    /// 可选的 <see cref="IDbProviderFactoryDescriptor"/> 集合，由 DI 容器注入。
    /// 默认 <c>null</c>（向后兼容旧测试 / 无 DI 场景）；若为 <c>null</c> 或空集合，
    /// <see cref="ResolveFactory"/> 仅依赖 <see cref="DbProviderFactories"/> 注册表。
    /// </param>
    public DynamicFilterProvider(
        ILogger<DynamicFilterProvider> logger,
        IEnumerable<IDbProviderFactoryDescriptor>? descriptors = null)
    {
        _logger = logger;
        _descriptorsByName = descriptors is null
            ? new Dictionary<string, IDbProviderFactoryDescriptor>(StringComparer.OrdinalIgnoreCase)
            : descriptors.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
    }

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
            _logger.LogInformation("已加载 {Count} 条动态数据过滤规则", rules.Count);
        }
        catch (Exception ex)
        {
            // InMemory 数据库、连接失败、表不存在等情况均 graceful 降级为空规则集
            _logger.LogWarning(ex, "无法从数据库加载动态过滤规则，将以空规则集运行");
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
                _logger.LogWarning(
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

                    _logger.LogDebug(
                        "已注册动态过滤器 '{FilterName}' → 实体 '{EntityType}'",
                        filterName, entityType.ClrType.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
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
    /// 若未注册则回退到 DI 注入的 <see cref="IDbProviderFactoryDescriptor"/> 集合（按 <c>Name</c> 匹配）。
    /// </summary>
    private DbConnection CreateDbConnection(string connectionString, string providerName)
    {
        var factory = ResolveFactory(providerName);
        var conn = factory.CreateConnection()
            ?? throw new InvalidOperationException($"无法为提供程序 '{providerName}' 创建数据库连接");
        conn.ConnectionString = connectionString;
        return conn;
    }

    /// <summary>
    /// 解析 DbProviderFactory：注册表 → DI 注入的 descriptor 集合。
    /// 公开为 <c>internal</c> 便于测试覆盖。
    ///
    /// #124: <b>契约</b> —— 未注册的 provider（含 InMemory / Cosmos 等非关系型后端）
    /// 抛 <see cref="NotSupportedException"/>。生产路径已由 <see cref="LoadRulesAsync"/>
    /// 的 try/catch 兜底降级为空规则集，且 <c>DynamicFilterBootstrap</c> 在 InMemory
    /// 时直接跳过加载；本方法仅供测试或自建引导逻辑直接调用。调用方若不走
    /// <see cref="LoadRulesAsync"/>，应优先用 <see cref="TryResolveFactory"/> 或自行 catch。
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <paramref name="providerName"/> 既不在 <see cref="DbProviderFactories"/> 注册表，
    /// 也不在 DI 注入的 <see cref="IDbProviderFactoryDescriptor"/> 集合中。
    /// </exception>
    internal DbProviderFactory ResolveFactory(string providerName)
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

        // 2. DI 注入的 IDbProviderFactoryDescriptor 集合（框架默认注册 4 个内置 descriptor，
        //    业务可再注册国产 DB descriptor）。Name 比较大小写不敏感。
        if (_descriptorsByName.TryGetValue(providerName, out var descriptor))
        {
            return descriptor.Factory;
        }

        throw new NotSupportedException(
            $"数据库提供程序 '{providerName}' 未注册。" +
            "请在应用启动时调用 DbProviderFactories.RegisterFactory() 注册对应工厂，" +
            $"或注入自定义 IDbProviderFactoryDescriptor（当前已知: {string.Join(", ", _descriptorsByName.Keys)}）。");
    }

    /// <summary>
    /// #124: <see cref="ResolveFactory"/> 的可空变体。未注册时返回 <see langword="null"/>
    /// 而非抛异常，便于自建引导逻辑在调用前探测 provider 是否受支持（如非关系型
    /// InMemory / Cosmos 后端应跳过动态过滤规则加载）。
    /// </summary>
    internal DbProviderFactory? TryResolveFactory(string providerName)
    {
        try
        {
            return ResolveFactory(providerName);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
