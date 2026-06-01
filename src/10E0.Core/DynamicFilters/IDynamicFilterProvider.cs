using Microsoft.EntityFrameworkCore;
using TenE0.Core.DataContext;

namespace TenE0.Core.DynamicFilters;

/// <summary>
/// 动态数据过滤规则提供者。
///
/// 在应用启动时从数据库加载过滤规则（不经过 DbContext，避免 OnModelCreating 递归），
/// 在 OnModelCreating 阶段为每个配置了规则的实体注册 EF Named Query Filter。
///
/// 过滤表达式引用 BaseDataContext 的运行时属性（CurrentUserCode 等），
/// EF Core 在每次查询时自动参数化，无需重建模型。
/// </summary>
public interface IDynamicFilterProvider
{
    /// <summary>
    /// 在 DbContext.OnModelCreating 中调用。
    /// 为 ModelBuilder 中匹配规则的实体注册 Named Query Filter。
    /// </summary>
    void ApplyDynamicFilters(ModelBuilder modelBuilder, BaseDataContext context);

    /// <summary>
    /// 从数据库加载/重新加载规则。启动时由初始化流程自动调用。
    /// </summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    /// <param name="providerName">
    /// EF Core 数据库提供程序标识（如 "Microsoft.Data.SqlClient"、"Npgsql"、"MySqlConnector"）。
    /// 用于通过 <see cref="System.Data.Common.DbProviderFactories"/> 创建 ADO.NET 连接。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    Task LoadRulesAsync(string connectionString, string providerName, CancellationToken ct = default);
}
