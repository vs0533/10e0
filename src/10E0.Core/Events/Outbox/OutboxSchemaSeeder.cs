using Microsoft.EntityFrameworkCore;
using TenE0.Core.Hosting;

namespace TenE0.Core.Events.Outbox;

/// <summary>
/// Outbox 表的 Schema 升级 Seeder — 为现有数据库幂等地补齐 #80 引入的新列与索引。
///
/// <para>
/// 背景：本项目当前使用 <c>EnsureCreatedAsync</c>，仅对全新库自动建表；
/// 既有部署升级到带 <c>LockedUntil / LockedByInstance</c> 字段的实体后，
/// EF 模型变更不会反向同步到已存在表结构。需要 Seeder 在启动期 ALTER 补齐。
/// </para>
///
/// <para>
/// 幂等性策略：
/// - ALTER TABLE ADD COLUMN：先用 provider 特定的"元数据探测"判断列是否存在，存在则跳过。
/// - CREATE INDEX：直接使用 IF NOT EXISTS（PG 原生 / SqlServer 通过 sys.indexes 适配）。
/// 这样既兼容 SqlServer 缺 ADD COLUMN IF NOT EXISTS 的限制，也避免每次启动都跑 N 次探测 SQL。
/// </para>
///
/// <para>
/// 双 provider 适配：通过 <see cref="Database.IsRelational"/> + <see cref="Database.ProviderName"/>
/// 判断当前底层（SqlServer / Npgsql / 其它）。非关系型 provider（如 InMemory 测试）跳过执行。
/// </para>
///
/// <para>
/// Order = 0：先于任何业务 Seeder（业务 Seeder 通常 Order=10+），保证后续 seeder 写出的行能落在新列上。
/// </para>
/// </summary>
public sealed class OutboxSchemaSeeder : IDataSeeder
{
    /// <inheritdoc />
    public int Order => 0;

    private const string TableName = "OutboxMessages";
    private const string LockedUntilColumn = "LockedUntil";
    private const string LockedByInstanceColumn = "LockedByInstance";
    private const string LockIndexName = "IX_OutboxMessages_LockedUntil_OccurredOn";

    /// <inheritdoc />
    /// <remarks>
    /// #122 幂等保证：本 Seeder 与 <see cref="DatabaseInitializerService.StartingAsync"/>
    /// 中的 <c>EnsureCreatedAsync</c> 是双路径互补关系：
    /// <list type="bullet">
    /// <item>全新库：<c>EnsureCreatedAsync</c> 按 ModelBuilder（含 LockedUntil/LockedByInstance 列）建表，
    ///   本 Seeder 的 <see cref="ColumnExistsAsync"/> 探测到列已存在 → 跳过 ALTER，零副作用。</item>
    /// <item>既有库（升级部署）：表已存在但缺新列 → 探测返回 false → ALTER 补齐。</item>
    /// </list>
    /// 因此"跑两次"不是 bug —— 第二次探测必然命中"已存在"短路。生产切换 <c>Migrate()</c>
    /// 后，迁移历史会接管建表，本 Seeder 探测仍兼容（列存在即跳过）。
    /// </remarks>
    public async Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        // 关系型 provider 才需要 ALTER；InMemory / Cosmos 等非关系型后端跳过
        if (!context.Database.IsRelational())
        {
            return;
        }

        var provider = context.Database.ProviderName ?? string.Empty;

        // 列存在性探测（不抛错；已存在返回 false → 跳过 ADD COLUMN）
        if (!await ColumnExistsAsync(context, provider, LockedUntilColumn, cancellationToken))
        {
            await context.Database.ExecuteSqlRawAsync(
                BuildAddColumnSql(provider, TableName, LockedUntilColumn, DateTimeOffsetColumnType(provider)),
                cancellationToken);
        }

        if (!await ColumnExistsAsync(context, provider, LockedByInstanceColumn, cancellationToken))
        {
            await context.Database.ExecuteSqlRawAsync(
                BuildAddColumnSql(provider, TableName, LockedByInstanceColumn, StringColumnType(provider)),
                cancellationToken);
        }

        // 索引：复合索引 (LockedUntil, OccurredOn) 用于 Relay 跳过已锁行
        var lockIndexSql = BuildCreateIndexSql(provider, LockIndexName, TableName, LockedUntilColumn, "OccurredOn");
        await context.Database.ExecuteSqlRawAsync(lockIndexSql, cancellationToken);
    }

    /// <summary>
    /// 探测列是否存在 — 通过查询 information schema / sys.columns。
    /// SqlServer 用 sys.columns；Postgres 用 information_schema.columns。
    /// </summary>
    private static async Task<bool> ColumnExistsAsync(
        DbContext context,
        string provider,
        string columnName,
        CancellationToken cancellationToken)
    {
        string probeSql = provider switch
        {
            var p when p.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) =>
                $"SELECT TOP 1 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'{TableName}') AND name = N'{columnName}'",
            var p when p.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("Postgres", StringComparison.OrdinalIgnoreCase) =>
                $"SELECT 1 FROM information_schema.columns WHERE table_name = '{TableName}' AND column_name = '{columnName}' LIMIT 1",
            _ => "SELECT 0", // 未知 provider 保守返回不存在 → 触发 ADD，依赖 RDBMS 报错冒泡
        };

        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = probeSql;
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    private static string BuildAddColumnSql(string provider, string table, string column, string type)
        => provider switch
        {
            // PG：原生支持 IF NOT EXISTS
            var p when p.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("Postgres", StringComparison.OrdinalIgnoreCase) =>
                $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} {type}",
            // SqlServer：无 IF NOT EXISTS，靠探测跳过
            _ => $"ALTER TABLE {table} ADD {column} {type}",
        };

    private static string BuildCreateIndexSql(string provider, string indexName, string table, string col1, string col2)
        => provider switch
        {
            var p when p.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("Postgres", StringComparison.OrdinalIgnoreCase) =>
                $"CREATE INDEX IF NOT EXISTS {indexName} ON {table} ({col1}, {col2})",
            var p when p.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) =>
                // SqlServer IF NOT EXISTS 用 WHERE NOT EXISTS 子查询
                $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexName}' AND object_id = OBJECT_ID(N'{table}')) " +
                $"CREATE INDEX {indexName} ON {table} ({col1}, {col2})",
            _ => $"CREATE INDEX {indexName} ON {table} ({col1}, {col2})",
        };

    private static string DateTimeOffsetColumnType(string provider)
        => provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
           provider.Contains("Postgres", StringComparison.OrdinalIgnoreCase)
            ? "timestamp with time zone"
            : "datetimeoffset";

    private static string StringColumnType(string provider)
        // #127: 列长从 OutboxModelBuilderExtensions 常量取，避免 entity 改 MaxLength 后
        // seeder ALTER 出旧长度导致漂移。
        => (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("Postgres", StringComparison.OrdinalIgnoreCase)
                ? $"varchar({OutboxModelBuilderExtensions.LockedByInstanceMaxLength})"
                : $"nvarchar({OutboxModelBuilderExtensions.LockedByInstanceMaxLength})");
}
