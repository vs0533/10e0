using System.Linq.Expressions;
using TenE0.Core.Abstractions;

namespace TenE0.Core.EntityService.Validators;

/// <summary>
/// 唯一性验证器静态工厂。
///
/// 用法：
///   options.UniqueValidators = [
///       Unique.Field&lt;Account&gt;(account, x => x.UserCode),
///       Unique.Group&lt;Account&gt;(account, x => x.OrgId, x => x.Code)
///   ]
///
/// 替代旧 new Validation().Unique(...) 的拖沓写法。
/// </summary>
public static class Unique
{
    /// <summary>
    /// 单字段唯一性（每个字段独立判重，任一重复都报错）。
    /// </summary>
    public static IUniqueValidator Field<TEntity>(
        TEntity entity,
        params Expression<Func<TEntity, object?>>[] fields)
        where TEntity : class, IBaseEntity
        => new FieldUniqueValidator<TEntity>(entity, fields);

    /// <summary>
    /// 组合字段唯一性（多个字段联合判重，必须全部匹配才算重复）。
    /// </summary>
    public static IUniqueValidator Group<TEntity>(
        TEntity entity,
        params Expression<Func<TEntity, object?>>[] fields)
        where TEntity : class, IBaseEntity
        => new GroupUniqueValidator<TEntity>(entity, fields);
}
