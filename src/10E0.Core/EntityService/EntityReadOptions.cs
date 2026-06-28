namespace TenE0.Core.EntityService;

/// <summary>
/// 读操作选项。与 <see cref="EntityWriteOptions"/> 对称。
/// </summary>
public sealed record EntityReadOptions
{
    /// <summary>
    /// 筛选条件(白名单字段安全模式)。null 时不附加任何 Where(仅靠 EF Named Query Filter)。
    /// 每个 <see cref="ReadFilter.Field"/> 在运行时校验必须是实体的真实属性名,防表达式注入。
    /// </summary>
    public IReadOnlyList<ReadFilter>? Filters { get; init; }

    /// <summary>
    /// 排序:属性名 + 方向。例:<c>[new ReadOrderBy("CreateTime", Descending: true)]</c>。
    /// 多个元素时按顺序组合(主排序 → 次排序)。null 时不附加排序(行序由数据库决定)。
    /// </summary>
    public IReadOnlyList<ReadOrderBy>? OrderBy { get; init; }

    /// <summary>
    /// 显式旁路过滤器(细粒度,取代危险的 <c>IgnoreQueryFilters()</c> 全量旁路)。
    /// <list type="bullet">
    ///   <item>null/空:应用全部过滤器(软删除 + 行级权限 + 租户)—— 默认,最安全</item>
    ///   <item>指定名称:仅旁路这些命名过滤器(如 <c>["Tenant"]</c> 只旁路租户,保留行级权限)</c></item>
    ///   <item><c>["*"]</c>:全量旁路(等价 <c>IgnoreQueryFilters()</c>),会记日志告警避免误用</c></item>
    /// </list>
    /// 已知命名过滤器名称:<c>SoftDelete</c>(软删除)/ <c>Tenant</c>(多租户)/
    /// <c>DataPrivilege:&lt;ContributorType&gt;</c>(行级权限)。
    /// </summary>
    public IReadOnlySet<string>? BypassFilters { get; init; }

    /// <summary>AsNoTracking(默认 true,读场景通常不跟踪)。</summary>
    public bool AsNoTracking { get; init; } = true;
}

/// <summary>
/// 类型安全的筛选条件。<see cref="Field"/> 运行时白名单校验(必须是实体真实属性名),防表达式注入。
/// </summary>
public sealed record ReadFilter(string Field, ReadOperator Operator, object? Value);

/// <summary>支持的筛选运算符。</summary>
public enum ReadOperator
{
    /// <summary>等于 (==)</summary>
    Eq,
    /// <summary>不等于 (!=)</summary>
    Ne,
    /// <summary>大于 (&gt;)</summary>
    Gt,
    /// <summary>大于等于 (&gt;=)</summary>
    Gte,
    /// <summary>小于 (&lt;)</summary>
    Lt,
    /// <summary>小于等于 (&lt;=)</summary>
    Lte,
    /// <summary>包含(字符串 Contains)</summary>
    Contains,
    /// <summary>前缀匹配(字符串 StartsWith)</summary>
    StartsWith,
    /// <summary>后缀匹配(字符串 EndsWith)</summary>
    EndsWith,
    /// <summary>在集合中(值须为 IEnumerable)。翻译为 EF <c>Contains</c>。</summary>
    In
}

/// <summary>排序项:属性名 + 方向。</summary>
public sealed record ReadOrderBy(string Field, bool Descending = false);
