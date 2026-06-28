using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenE0.Core.Abstractions;
using TenE0.Core.Queries;

namespace TenE0.Core.EntityService;

/// <summary>
/// <see cref="IEntityQueryService"/> 默认实现。
///
/// 读管线:
/// <code>
/// DbSet(EF Named Filter 自动应用)
///   → BypassFilters(IgnoreQueryFilters)   // 按选项,默认全应用
///   → AsNoTracking(默认 true)
///   → ReadFilters(Expression 树 Where,字段白名单校验)
///   → [Count]                              // 分页总数,投影前计数
///   → OrderBy(DynamicOrderBy 字符串,字段已校验)
///   → Page(page, pageSize)                 // 复用 DynamicQueryExtensions.Page,含上限保护
///   → Select(selector)                      // 仅投影重载
///   → ToListAsync
/// </code>
///
/// 字段白名单校验通过 <see cref="context"/>.Model 反射 + 缓存,非法字段抛
/// <see cref="ArgumentException">(编程错误,不入 IErrs)。
/// </summary>
internal sealed class EntityQueryService(ILogger<EntityQueryService>? logger = null) : IEntityQueryService
{
    /// <summary>全量旁路占位符(等价无参 <c>IgnoreQueryFilters()</c>)。</summary>
    private const string BypassAll = "*";

    /// <summary>类型 → 允许的属性名集合(EF 模型属性,已排除 [NotMapped])。反射只做一次。</summary>
    private static readonly ConcurrentDictionary<Type, IReadOnlySet<string>> AllowedFieldsCache = new();

    // ============================================================
    // GetByIdAsync
    // ============================================================
    public async Task<TEntity?> GetByIdAsync<TEntity>(
        DbContext context, string id,
        EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        var q = BuildQuery<TEntity>(context, options);
        return await q.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    public async Task<TView?> GetByIdAsync<TEntity, TView>(
        DbContext context, string id,
        Expression<Func<TEntity, TView>> selector,
        EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        var q = BuildQuery<TEntity>(context, options);
        return await q.Where(e => e.Id == id).Select(selector).FirstOrDefaultAsync(cancellationToken);
    }

    // ============================================================
    // ListAsync
    // ============================================================
    public async Task<List<TEntity>> ListAsync<TEntity>(
        DbContext context, EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        var q = BuildQuery<TEntity>(context, options);
        return await q.ToListAsync(cancellationToken);
    }

    public async Task<List<TView>> ListAsync<TEntity, TView>(
        DbContext context, Expression<Func<TEntity, TView>> selector,
        EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        var q = BuildQuery<TEntity>(context, options);
        return await q.Select(selector).ToListAsync(cancellationToken);
    }

    // ============================================================
    // PagedAsync
    // ============================================================
    public async Task<PagedResult<TEntity>> PagedAsync<TEntity>(
        DbContext context, PagedQuery query,
        EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        var q = BuildQuery<TEntity>(context, options);
        var (page, pageSize) = NormalizePaging(query);
        var total = await q.CountAsync(cancellationToken);
        var items = await q.Page(page, pageSize).ToListAsync(cancellationToken);
        return PagedResult<TEntity>.Create(items, total, page, pageSize);
    }

    public async Task<PagedResult<TView>> PagedAsync<TEntity, TView>(
        DbContext context, PagedQuery query,
        Expression<Func<TEntity, TView>> selector,
        EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        var q = BuildQuery<TEntity>(context, options);
        var (page, pageSize) = NormalizePaging(query);
        var total = await q.CountAsync(cancellationToken);
        var items = await q.Page(page, pageSize).Select(selector).ToListAsync(cancellationToken);
        return PagedResult<TView>.Create(items, total, page, pageSize);
    }

    // ============================================================
    // CountAsync / ExistsAsync
    // ============================================================
    public async Task<int> CountAsync<TEntity>(
        DbContext context, EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        var q = BuildQuery<TEntity>(context, options);
        return await q.CountAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync<TEntity>(
        DbContext context, string id, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        // ExistsAsync 不暴露 BypassFilters —— 主键存在性探测应始终尊重行级过滤
        // (否则会泄露"被权限隔离的行是否存在")。
        var q = context.Set<TEntity>().AsQueryable();
        return await q.AnyAsync(e => e.Id == id, cancellationToken);
    }

    // ============================================================
    // 私有:查询构建
    // ============================================================

    /// <summary>
    /// 构建 TEntity 查询:应用 BypassFilters → AsNoTracking → Filters → OrderBy。
    /// 不含分页/投影(分页在 PagedAsync 单独处理,投影在调用点 Select)。
    /// </summary>
    private IQueryable<TEntity> BuildQuery<TEntity>(DbContext context, EntityReadOptions? options)
        where TEntity : class, IBaseEntity
    {
        options ??= EntityReadOptionsDefault;

        var query = ApplyBypass(context.Set<TEntity>().AsQueryable(), options.BypassFilters, typeof(TEntity));

        if (options.AsNoTracking)
            query = query.AsNoTracking();

        if (options.Filters is { Count: > 0 })
            query = ApplyFilters(query, options.Filters, context, typeof(TEntity));

        if (options.OrderBy is { Count: > 0 })
            query = query.DynamicOrderBy(BuildOrderByString(options.OrderBy, context, typeof(TEntity)));

        return query;
    }

    /// <summary>
    /// 按 <see cref="EntityReadOptions.BypassFilters"/> 应用 <c>IgnoreQueryFilters</c>。
    /// null/空 → 不旁路(默认全应用,最安全);具体名 → 细粒度旁路;<c>"*"</c> → 全量旁路并告警。
    /// </summary>
    private IQueryable<TEntity> ApplyBypass<TEntity>(
        IQueryable<TEntity> source, IReadOnlySet<string>? bypass, Type entityType)
        where TEntity : class, IBaseEntity
    {
        if (bypass is null || bypass.Count == 0)
            return source;

        if (bypass.Contains(BypassAll))
        {
            // 全量旁路 —— 仅管理后台等受控场景使用,记告警
            logger?.LogWarning(
                "EntityQueryService: 全量旁路过滤器(BypassFilters=[\"{Marker}\"]) 作用于实体 {Entity}。" +
                "这会绕过软删除 + 行级权限 + 租户隔离,仅限管理后台等受控场景。",
                BypassAll, entityType.Name);
            return source.IgnoreQueryFilters();
        }

        // 细粒度旁路:逐个名字传入 IgnoreQueryFilters(params string[])
        return source.IgnoreQueryFilters(bypass.ToArray());
    }

    /// <summary>
    /// 应用筛选条件(Expression 树手动构建,避开字符串插值任意 object 值)。
    /// 每个 <see cref="ReadFilter.Field"/> 先做白名单校验。
    /// </summary>
    private IQueryable<TEntity> ApplyFilters<TEntity>(
        IQueryable<TEntity> source, IReadOnlyList<ReadFilter> filters, DbContext context, Type entityType)
        where TEntity : class, IBaseEntity
    {
        var allowed = GetAllowedFields(context, entityType);

        foreach (var f in filters)
        {
            if (!allowed.Contains(f.Field))
                throw new ArgumentException(
                    $"非法筛选字段 '{f.Field}':不是实体 {entityType.Name} 的 EF 模型属性。" +
                    "字段名经运行时白名单校验以防止表达式注入(详见 docs/28-entity-query-service.md)。",
                    nameof(filters));

            var predicate = BuildPredicate<TEntity>(f);
            source = source.Where(predicate);
        }

        return source;
    }

    /// <summary>
    /// 把 <see cref="ReadFilter"/> 编译为 <c>Expression&lt;Func&lt;TEntity, bool&gt;&gt;</c>。
    /// 值按属性类型强制转换(Nullable&lt;T&gt; 取 UnderlyingType),String 操作走 Contains/StartsWith/EndsWith。
    /// </summary>
    private static Expression<Func<TEntity, bool>> BuildPredicate<TEntity>(ReadFilter filter)
        where TEntity : class, IBaseEntity
    {
        var param = Expression.Parameter(typeof(TEntity), "e");
        var property = Expression.Property(param, filter.Field);
        var propType = property.Type;
        var underlying = Nullable.GetUnderlyingType(propType) ?? propType;

        Expression body = filter.Operator switch
        {
            ReadOperator.Eq => Expression.Equal(property, ToConstant(filter.Value, propType)),
            ReadOperator.Ne => Expression.NotEqual(property, ToConstant(filter.Value, propType)),
            ReadOperator.Gt => Expression.GreaterThan(property, ToConstant(filter.Value, propType)),
            ReadOperator.Gte => Expression.GreaterThanOrEqual(property, ToConstant(filter.Value, propType)),
            ReadOperator.Lt => Expression.LessThan(property, ToConstant(filter.Value, propType)),
            ReadOperator.Lte => Expression.LessThanOrEqual(property, ToConstant(filter.Value, propType)),
            ReadOperator.Contains => CallStringMethod(property, nameof(string.Contains), filter.Value),
            ReadOperator.StartsWith => CallStringMethod(property, nameof(string.StartsWith), filter.Value),
            ReadOperator.EndsWith => CallStringMethod(property, nameof(string.EndsWith), filter.Value),
            ReadOperator.In => BuildInExpression(property, filter.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(filter), $"未知的筛选运算符: {filter.Operator}")
        };

        return Expression.Lambda<Func<TEntity, bool>>(body, param);
    }

    /// <summary>构建字符串方法调用(prop.Method(value))。value 强制为 string。</summary>
    private static MethodCallExpression CallStringMethod(MemberExpression property, string methodName, object? value)
    {
        var stringValue = value?.ToString() ?? string.Empty;
        return Expression.Call(property, methodName, Type.EmptyTypes, Expression.Constant(stringValue, typeof(string)));
    }

    /// <summary>
    /// 构建 In 表达式:把 value(IEnumerable)转成类型化 List,调 Queryable.Contains(等价于 EF 翻译的 IN)。
    /// 通过 IEnumerable&lt;T&gt;.Contains 实例方法调用,EF Core 能正确翻译为参数化 SQL IN。
    /// </summary>
    private static MethodCallExpression BuildInExpression(MemberExpression property, object? value)
    {
        if (value is null)
            throw new ArgumentException("ReadOperator.In 的值不能为 null", nameof(value));

        // string 也实现 IEnumerable<char>,但这不是"集合"语义 —— 显式拒绝
        if (value is string or char or not IEnumerable)
            throw new ArgumentException(
                $"ReadOperator.In 的值必须是集合(实现 IEnumerable 且非 string/char),实际类型: {value.GetType().Name}",
                nameof(value));

        var elements = (IEnumerable)value;

        var elementType = property.Type;
        var underlying = Nullable.GetUnderlyingType(elementType) ?? elementType;

        // 把 IEnumerable 装进类型化 List<T>,保证 EF 能翻译为参数化 IN
        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var item in elements)
            list.Add(Convert.ChangeType(item, underlying));

        // 调用 List<T>.Contains(T) —— EF Core 把它翻译为 SQL IN(@p0, @p1, ...)
        var containsMethod = listType.GetMethod(nameof(List<object>.Contains), [elementType])!;
        return Expression.Call(Expression.Constant(list, listType), containsMethod, property);
    }

    /// <summary>把 value 强制转换为属性类型(处理 Nullable&lt;T&gt;)。</summary>
    private static ConstantExpression ToConstant(object? value, Type propType)
    {
        var underlying = Nullable.GetUnderlyingType(propType) ?? propType;
        var converted = value is null ? null : Convert.ChangeType(value, underlying);
        return Expression.Constant(converted, propType);
    }

    // ============================================================
    // 私有:白名单 / 排序字符串 / 分页规范化
    // ============================================================

    /// <summary>
    /// 从 EF 模型取实体的可筛选属性名集合(含 [NotMapped] 自动排除 —— 模型属性才有列)。
    /// 结果按类型缓存。对 value object / owned type 不展开(简单白名单,够用)。
    /// </summary>
    private static IReadOnlySet<string> GetAllowedFields(DbContext context, Type entityType)
    {
        return AllowedFieldsCache.GetOrAdd(entityType, _ =>
        {
            var et = context.Model.FindEntityType(entityType)
                ?? throw new InvalidOperationException($"实体类型未在 DbContext 中注册:{entityType.Name}");
            // GetProperties() 返回所有 mapped scalar properties(排除导航)。
            // shadow property 也算(框架内部用),不影响安全性 —— 仍是真实列。
            return new HashSet<string>(
                et.GetProperties().Select(p => p.Name),
                StringComparer.Ordinal);
        });
    }

    /// <summary>
    /// 把 <see cref="ReadOrderBy"/> 列表拼成 <c>DynamicOrderBy</c> 字符串:"CreateTime desc, Name asc"。
    /// 字段名已在 <see cref="BuildOrderByString"/> 白名单校验(只可能是真实属性名),无注入面。
    /// </summary>
    private static string BuildOrderByString(IReadOnlyList<ReadOrderBy> orderBy, DbContext context, Type entityType)
    {
        var allowed = GetAllowedFields(context, entityType);
        var parts = new List<string>(orderBy.Count);
        foreach (var o in orderBy)
        {
            if (!allowed.Contains(o.Field))
                throw new ArgumentException(
                    $"非法排序字段 '{o.Field}':不是实体 {entityType.Name} 的 EF 模型属性。",
                    nameof(orderBy));
            parts.Add(o.Descending ? $"{o.Field} desc" : $"{o.Field} asc");
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// 分页参数规范化(page&lt;1 → 1,pageSize&lt;1 → 10,&gt;1000 → 1000)。
    /// 与 <see cref="DynamicQueryExtensions.Page{T}"/> 一致,这里前置规范化是因为
    /// <see cref="PagedResult{T}.Create"/> 用规范化后的值算 TotalPages。
    /// </summary>
    private static (int Page, int PageSize) NormalizePaging(PagedQuery query)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize switch
        {
            < 1 => 10,
            > 1000 => 1000,
            _ => query.PageSize
        };
        return (page, pageSize);
    }

    /// <summary>默认读选项(AsNoTracking=true,其余 null)。</summary>
    private static readonly EntityReadOptions EntityReadOptionsDefault = new();
}
