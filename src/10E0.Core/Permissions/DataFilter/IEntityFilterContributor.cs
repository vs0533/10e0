using System.Linq.Expressions;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Permissions.DataFilter;

/// <summary>
/// 实体数据行过滤器贡献者。
///
/// 替代旧 ConditionPrivilege&lt;TEntity&gt;（旧版未完成，最后一行还是 throw "end"）。
/// 新方案利用 EF Core Named Query Filter，调用方实现此接口，BaseDataContext 自动注册到 OnModelCreating。
///
/// 重要：返回的表达式可以引用 BaseDataContext 实例属性（如 CurrentUserCode、CurrentRoleIds），
/// EF 会在每次查询时把它们当作 SQL 参数动态传入，无需重建模型。
/// 这是 EF 文档推荐的 multi-tenancy / row-level security 实现方式。
///
/// 组合策略（重要约定）：
/// - 同一实体多个 contributor 注册时，框架为每个 contributor 注册独立的命名过滤器，
///   EF 自动 AND 组合。这是安全的默认（必须满足所有策略才能看到行）。
/// - 需要 OR 语义请在单个 contributor 表达式内写 ||，不要拆成多个 contributor。
/// - 需要"超管 bypass"请在表达式最前面 OR 上 context.BypassFilters。
/// </summary>
public interface IEntityFilterContributor
{
    /// <summary>本贡献者作用的实体类型（必须实现 IBaseEntity）。</summary>
    Type EntityType { get; }

    /// <summary>
    /// 构造过滤表达式。返回 null 表示不附加过滤（视场景而定，例如未登录时）。
    /// </summary>
    /// <param name="context">承载当前请求上下文的 DbContext，可读取 CurrentUserCode 等属性。</param>
    LambdaExpression? BuildFilter(DataContext.BaseDataContext context);
}

/// <summary>
/// 强类型版本，便于实现方书写 Expression。框架内部桥接到 <see cref="IEntityFilterContributor"/>。
/// </summary>
public abstract class EntityFilterContributor<TEntity> : IEntityFilterContributor
    where TEntity : class, IBaseEntity
{
    public Type EntityType => typeof(TEntity);

    public LambdaExpression? BuildFilter(DataContext.BaseDataContext context) => Build(context);

    /// <summary>实现方覆盖此方法返回类型化的过滤表达式。返回 null 表示跳过。</summary>
    protected abstract Expression<Func<TEntity, bool>>? Build(DataContext.BaseDataContext context);
}
