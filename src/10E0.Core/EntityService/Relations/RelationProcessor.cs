using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TenE0.Core.Abstractions;

namespace TenE0.Core.EntityService.Relations;

/// <summary>
/// 关系处理工具：导航属性清理 + M:N 关系 diff。
///
/// 替代旧 RelationProcessor + IgnoreNavigationProp，但有重要简化：
/// - 不再依赖 MultipleEntity/TreeEntity 标记基类，直接读 EF Core IModel 元数据
/// - 不维护自建 MetaContext 反射缓存（EF 已有）
/// - M:N 通过 skip navigation 检测，支持无显式 junction 类的 M:N 关系
/// </summary>
internal static class RelationProcessor
{
    /// <summary>
    /// 把客户端实体上不应该触发联动写入的导航属性清理掉。
    ///
    /// 保留：
    /// - skip navigation（M:N）— 由 <see cref="DiffSkipNavigations"/> 处理
    ///
    /// 清理：
    /// - 普通导航属性（一对一/多对一/一对多）— 防止 EF 把客户端塞的对象当作联动写入
    /// </summary>
    public static void CleanNavigations<TEntity>(DbContext context, TEntity entity)
        where TEntity : class, IBaseEntity
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"实体类型未在 DbContext 中注册：{typeof(TEntity).Name}");

        var skipNavNames = entityType.GetSkipNavigations().Select(n => n.Name).ToHashSet();

        foreach (var nav in entityType.GetNavigations())
        {
            if (skipNavNames.Contains(nav.Name))
                continue;
            nav.PropertyInfo?.SetValue(entity, null);
        }
    }

    /// <summary>
    /// 对实体类型的所有 skip navigation 做 diff，把 <paramref name="dbEntity"/> 的集合对齐到 <paramref name="postedEntity"/> 的集合。
    ///
    /// 调用方约定：
    /// - dbEntity 已被 DbContext 跟踪，且通过 .Include(skipNav) 加载了完整 M:N 集合
    /// - postedEntity 是客户端提交的实体，其 skip nav 集合中只需要 Id 已设置的对象引用
    ///
    /// 处理后：dbEntity 的 M:N 集合被增删改成与 postedEntity 一致，EF 后续 SaveChanges 自动维护连接表。
    /// </summary>
    public static void DiffSkipNavigations<TEntity>(
        IEntityType entityType,
        TEntity dbEntity,
        TEntity postedEntity,
        IReadOnlySet<string>? processOnly = null)
        where TEntity : class, IBaseEntity
    {
        foreach (var nav in entityType.GetSkipNavigations())
        {
            // 调用方显式声明只处理某些导航
            if (processOnly is not null && !processOnly.Contains(nav.Name))
                continue;

            var propInfo = nav.PropertyInfo;
            if (propInfo is null) continue;

            // 客户端没传该导航属性（null）→ 视为"不变更"，跳过
            if (propInfo.GetValue(postedEntity) is not IEnumerable postedCollection)
                continue;

            if (propInfo.GetValue(dbEntity) is not IList dbCollection)
                continue;

            DiffOne(nav, dbCollection, postedCollection);
        }
    }

    private static void DiffOne(ISkipNavigation nav, IList dbCollection, IEnumerable postedCollection)
    {
        var pkPropInfo = nav.TargetEntityType.FindPrimaryKey()!.Properties[0].PropertyInfo!;

        // 收集 posted ID（去重，忽略 null Id）
        var postedIds = new HashSet<object>();
        var postedItemsById = new Dictionary<object, object>();
        foreach (var item in postedCollection)
        {
            var id = pkPropInfo.GetValue(item);
            if (id is null) continue;
            if (postedIds.Add(id)) postedItemsById[id] = item;
        }

        // 1. 删除：DB 集合里有但 posted 没有
        var dbItems = dbCollection.Cast<object>().ToList();
        foreach (var dbItem in dbItems)
        {
            var id = pkPropInfo.GetValue(dbItem)!;
            if (!postedIds.Contains(id))
                dbCollection.Remove(dbItem);
        }

        // 2. 新增：posted 有但 DB 没有
        var remainingDbIds = dbCollection.Cast<object>()
            .Select(o => pkPropInfo.GetValue(o)!)
            .ToHashSet();
        foreach (var (id, postedItem) in postedItemsById)
        {
            if (!remainingDbIds.Contains(id))
                dbCollection.Add(postedItem);
        }
    }
}
