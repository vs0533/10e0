using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;

namespace TenE0.Core.EntityService.Validators;

/// <summary>
/// 单字段唯一性验证：每个字段单独不能重复。
/// 例：UserCode 唯一，Email 唯一（任意一个重复都报错）。
///
/// 替代旧 SimpleUnique。新版用 EF 异步查询，构造 Expression 树时利用 C# 14 简化语法。
/// </summary>
internal sealed class FieldUniqueValidator<TEntity> : IUniqueValidator
    where TEntity : class, IBaseEntity
{
    private readonly TEntity _entity;
    private readonly IReadOnlyList<PropertyInfo> _properties;

    public FieldUniqueValidator(TEntity entity, params Expression<Func<TEntity, object?>>[] fieldSelectors)
    {
        _entity = entity;
        _properties = fieldSelectors.Select(ExtractProperty).ToList();
    }

    public async Task ValidateAsync(DbContext context, IErrs errs, bool ignoreSelfId, CancellationToken cancellationToken)
    {
        var dbSet = context.Set<TEntity>().AsNoTracking().TagWith("单字段唯一性验证");

        foreach (var property in _properties)
        {
            var value = property.GetValue(_entity);
            var query = BuildEqualityQuery(dbSet, property, value);

            if (ignoreSelfId)
                query = query.Where(e => e.Id != _entity.Id);

            if (await query.AnyAsync(cancellationToken))
                errs.Add($"{GetDisplayName(property)} 不能重复", key: property.Name, code: ErrorCodes.Unique);
        }
    }

    private static IQueryable<TEntity> BuildEqualityQuery(IQueryable<TEntity> source, PropertyInfo property, object? value)
    {
        var param = Expression.Parameter(typeof(TEntity), "e");
        var propAccess = Expression.Property(param, property);
        // 装箱后比较：兼容 Nullable<T> 和值类型
        var constant = Expression.Constant(value, property.PropertyType);
        var equal = Expression.Equal(propAccess, constant);
        var lambda = Expression.Lambda<Func<TEntity, bool>>(equal, param);
        return source.Where(lambda);
    }

    private static PropertyInfo ExtractProperty(Expression<Func<TEntity, object?>> selector)
    {
        var expr = selector.Body is UnaryExpression u ? u.Operand : selector.Body;
        if (expr is MemberExpression { Member: PropertyInfo prop })
            return prop;
        throw new ArgumentException($"无法解析字段选择器：{selector}");
    }

    private static string GetDisplayName(PropertyInfo property)
    {
        var display = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>();
        return display?.Name ?? property.Name;
    }
}

/// <summary>
/// 组合字段唯一性验证：一组字段联合不能重复。
/// 例：(OrgId, Code) 组合唯一（同一机构下编码不能重复，不同机构可重复）。
///
/// 替代旧 GroupUnique。
/// </summary>
internal sealed class GroupUniqueValidator<TEntity> : IUniqueValidator
    where TEntity : class, IBaseEntity
{
    private readonly TEntity _entity;
    private readonly IReadOnlyList<PropertyInfo> _properties;

    public GroupUniqueValidator(TEntity entity, params Expression<Func<TEntity, object?>>[] fieldSelectors)
    {
        if (fieldSelectors.Length == 0)
            throw new ArgumentException("分组唯一性验证至少需要一个字段", nameof(fieldSelectors));

        _entity = entity;
        _properties = fieldSelectors.Select(ExtractProperty).ToList();
    }

    public async Task ValidateAsync(DbContext context, IErrs errs, bool ignoreSelfId, CancellationToken cancellationToken)
    {
        var dbSet = context.Set<TEntity>().AsNoTracking().TagWith("组合字段唯一性验证");

        var param = Expression.Parameter(typeof(TEntity), "e");
        Expression? combined = null;

        foreach (var property in _properties)
        {
            var propAccess = Expression.Property(param, property);
            var value = property.GetValue(_entity);
            var constant = Expression.Constant(value, property.PropertyType);
            var equal = Expression.Equal(propAccess, constant);
            combined = combined is null ? equal : Expression.AndAlso(combined, equal);
        }

        var lambda = Expression.Lambda<Func<TEntity, bool>>(combined!, param);
        var query = dbSet.Where(lambda);

        if (ignoreSelfId)
            query = query.Where(e => e.Id != _entity.Id);

        if (await query.AnyAsync(cancellationToken))
        {
            var names = string.Join(", ", _properties.Select(p => p.Name));
            errs.Add($"组合字段 [{names}] 已存在，不能重复", key: names, code: ErrorCodes.UniqueGroup);
        }
    }

    private static PropertyInfo ExtractProperty(Expression<Func<TEntity, object?>> selector)
    {
        var expr = selector.Body is UnaryExpression u ? u.Operand : selector.Body;
        if (expr is MemberExpression { Member: PropertyInfo prop })
            return prop;
        throw new ArgumentException($"无法解析字段选择器：{selector}");
    }
}
