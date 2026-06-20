using Microsoft.Extensions.Options;

namespace TenE0.Core.DynamicFilters;

/// <summary>
/// 抽象 EF Core 实体的物理表名解析。
/// 业务项目通过 <see cref="TenE0.Core.DependencyInjection.DynamicFiltersExtensions.ConfigureTenE0TableNames"/>
/// 配置 <see cref="TableNameOptions"/>（前缀 / Schema），无需重写 <c>ConfigureTenE0XxxTables</c>。
/// </summary>
public interface ITableNameProvider
{
    /// <summary>
    /// 返回实体 CLR 类型对应的物理表名。
    /// 实现负责拼接前缀、Schema、转 snake_case 等规则。
    /// </summary>
    /// <param name="entityType">实体 CLR 类型（来自 <c>IMutableEntityType.ClrType</c>）。</param>
    /// <returns>不含 schema 的纯表名。Schema 通过 <see cref="TableNameOptions.Schema"/> 单独注入到 <c>ModelBuilder</c>。</returns>
    string GetTableName(Type entityType);
}

/// <summary>
/// 表名命名配置。可选 <see cref="Prefix"/> 与 <see cref="Schema"/>。
/// </summary>
public sealed class TableNameOptions
{
    /// <summary>表名前缀（如 <c>"MyApp_"</c>）。默认空。</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>默认 schema（如 <c>"crm"</c>）。仅当非空时由 EF Core Convention 注入 <c>ToTable(name, schema)</c>。</summary>
    public string? Schema { get; set; }

    /// <summary>是否对表名做 snake_case 转换。默认 <c>true</c>。</summary>
    public bool UseSnakeCase { get; set; } = true;
}

/// <summary>
/// <see cref="ITableNameProvider"/> 的默认实现：
/// <c>Prefix + (snake_case(Name) | 原名)</c>。
/// 单例、无状态，线程安全。
/// </summary>
public sealed class DefaultTableNameProvider(IOptions<TableNameOptions> options) : ITableNameProvider
{
    private readonly TableNameOptions _options = options.Value;

    /// <inheritdoc />
    public string GetTableName(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        var rawName = _options.UseSnakeCase
            ? ToSnakeCase(entityType.Name)
            : entityType.Name;

        return string.IsNullOrEmpty(_options.Prefix)
            ? rawName
            : _options.Prefix + rawName;
    }

    /// <summary>
    /// PascalCase → snake_case。连续大写按"整体单词"切分（如 <c>TenE0User</c> → <c>ten_e0_user</c>）。
    /// </summary>
    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var buffer = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                // 在大写前插下划线，除非：
                // - 是首字符
                // - 前一个字符已经是下划线
                // - 前一个字符是小写（此时首字母大写无需切分，如 "MyApp" 的 "M" 紧跟 "y" 时）
                // - 下一个字符是小写（连续大写的尾字母，如 "XMLParser" 的 "L" 与 "P" 之间）
                var needUnderscore =
                    i > 0 &&
                    name[i - 1] != '_' &&
                    (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (needUnderscore) buffer.Append('_');
                buffer.Append(char.ToLowerInvariant(c));
            }
            else
            {
                buffer.Append(c);
            }
        }
        return buffer.ToString();
    }
}
