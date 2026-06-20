using System.Data.Common;
using System.Reflection;

namespace TenE0.Core.DynamicFilters;

/// <summary>
/// 抽象 ADO.NET <see cref="DbProviderFactory"/> 注册项。
///
/// 框架默认注册 4 个内置实现（SQL Server / PostgreSQL / MySQL / SQLite），
/// 业务项目可注入自定义实现接入国产 DB（达梦 / 人大金仓 / OceanBase 等）。
///
/// <para>
/// 替换历史背景：旧 <c>DynamicFilterProvider</c> 用静态字典硬编码 4 个 provider 的
/// <c>AssemblyQualifiedName</c>，国产 DB 直接 <see cref="NotSupportedException"/>。
/// </para>
/// </summary>
public interface IDbProviderFactoryDescriptor
{
    /// <summary>
    /// 逻辑提供程序名。大小写不敏感比较。
    /// 与 ADO.NET <see cref="DbProviderFactories"/> 注册表的 <c>InvariantName</c> 对齐。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 对应的 <see cref="DbProviderFactory"/> 单例。
    /// 业务自定义实现通常直接 <c>return new DmDbFactory();</c>；
    /// 框架内置实现走反射回退（避免引入 provider 包的硬依赖）。
    /// </summary>
    DbProviderFactory Factory { get; }
}

// =============================================================================
// 框架内置 4 个 provider 实现
// =============================================================================
//
// 设计原则：Core 不显式 PackageReference SQL Server / Npgsql / MySqlConnector / Sqlite
// provider 包（它们都是 EF Core 各自子包传递依赖，运行时不保证已加载）。
// 因此默认 descriptor 仍走反射：Type.GetType(AssemblyQualifiedName) + Instance 单例。
//
// 业务项目若希望 100% 显式（避免反射），可在自己模块中
// `services.Replace(IDbProviderFactoryDescriptor, new SqlServerDbProviderFactoryDescriptor())`
// 注入强类型实现。

/// <summary>SQL Server 内置 descriptor。Provider name: <c>"SqlServer"</c> / <c>"Microsoft.Data.SqlClient"</c>。</summary>
public sealed class SqlServerDbProviderFactoryDescriptor : IDbProviderFactoryDescriptor
{
    private const string FactoryTypeName = "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient";

    /// <inheritdoc />
    public string Name => "SqlServer";

    /// <inheritdoc />
    public DbProviderFactory Factory => ResolveFactory();

    internal static DbProviderFactory ResolveFactory() =>
        ResolveByReflection(FactoryTypeName);

    internal static DbProviderFactory ResolveByReflection(string assemblyQualifiedName)
    {
        var type = Type.GetType(assemblyQualifiedName, throwOnError: false)
            ?? throw new InvalidOperationException(
                $"无法解析 DbProviderFactory 类型 '{assemblyQualifiedName}'。" +
                "请确认对应 provider 程序集已部署，或注入自定义 IDbProviderFactoryDescriptor 替代。");
        var instance = type
            .GetField("Instance", BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null) as DbProviderFactory
            ?? throw new InvalidOperationException(
                $"类型 '{type.FullName}' 缺少 'public static DbProviderFactory Instance' 字段。");
        return instance;
    }
}

/// <summary>PostgreSQL 内置 descriptor。Provider name: <c>"PostgreSQL"</c> / <c>"Npgsql"</c>。</summary>
public sealed class NpgsqlDbProviderFactoryDescriptor : IDbProviderFactoryDescriptor
{
    private const string FactoryTypeName = "Npgsql.NpgsqlFactory, Npgsql";

    /// <inheritdoc />
    public string Name => "PostgreSQL";

    /// <inheritdoc />
    public DbProviderFactory Factory => SqlServerDbProviderFactoryDescriptor.ResolveByReflection(FactoryTypeName);
}

/// <summary>MySQL 内置 descriptor（MySqlConnector）。Provider name: <c>"MySql"</c> / <c>"MySqlConnector"</c>。</summary>
public sealed class MySqlDbProviderFactoryDescriptor : IDbProviderFactoryDescriptor
{
    private const string FactoryTypeName = "MySqlConnector.MySqlConnectorFactory, MySqlConnector";

    /// <inheritdoc />
    public string Name => "MySql";

    /// <inheritdoc />
    public DbProviderFactory Factory => SqlServerDbProviderFactoryDescriptor.ResolveByReflection(FactoryTypeName);
}

/// <summary>SQLite 内置 descriptor。Provider name: <c>"SQLite"</c> / <c>"Microsoft.Data.Sqlite"</c>。</summary>
public sealed class SqliteDbProviderFactoryDescriptor : IDbProviderFactoryDescriptor
{
    private const string FactoryTypeName = "Microsoft.Data.Sqlite.SqliteFactory, Microsoft.Data.Sqlite";

    /// <inheritdoc />
    public string Name => "SQLite";

    /// <inheritdoc />
    public DbProviderFactory Factory => SqlServerDbProviderFactoryDescriptor.ResolveByReflection(FactoryTypeName);
}
