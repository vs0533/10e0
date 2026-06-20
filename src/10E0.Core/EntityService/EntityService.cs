using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;
using TenE0.Core.EntityService.Relations;
using TenE0.Core.Permissions;
using TenE0.Core.Sequences;

namespace TenE0.Core.EntityService;

/// <summary>
/// IEntityService 默认实现。
/// 注入 IErrs（错误袋）、IPermissionEvaluator（字段级权限）、ISequenceGenerator（流水号自动填充）。
/// </summary>
internal sealed class EntityService(
    IErrs errs,
    IPermissionEvaluator permissions,
    ISequenceGenerator? sequenceGenerator = null) : IEntityService
{
    // 缓存类型 → [SequenceAttribute] 字段映射，反射只做一次
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<(PropertyInfo Prop, SequenceAttribute Attr)>> SequenceFieldCache = new();
    // ============================================================
    // Create
    // ============================================================
    public async Task<bool> CreateAsync<TEntity>(
        DbContext context,
        TEntity entity,
        EntityWriteOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        options ??= EntityWriteOptions.Default;

        if (!options.KeepNavigationProperties)
            RelationProcessor.CleanNavigations(context, entity);

        // 字段级权限：Create 场景检查所有"将要写入"的字段
        if (!await CheckFieldPermissionsAsync(context, entity, options, isCreate: true, cancellationToken))
            return false;

        // 自动填充流水号字段（仅 Create 时）
        await FillSequenceFieldsAsync(entity, cancellationToken);

        if (!await RunUniqueValidatorsAsync(context, options, ignoreSelfId: false, cancellationToken))
            return false;

        context.Set<TEntity>().Add(entity);

        if (options.BeforeSaveAsync is not null)
            await options.BeforeSaveAsync(cancellationToken);

        if (!errs.IsValid) return false;

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ============================================================
    // Update — 加载现有实体后再补丁，避免 Attach+Modified 的两个陷阱：
    //   1. 客户端未传字段（如 CreateTime）会被默认值覆盖
    //   2. M:N diff 加载的实体被 EF 身份解析返回为同一引用，导致 diff 失效
    // ============================================================
    public async Task<bool> UpdateAsync<TEntity>(
        DbContext context,
        TEntity entity,
        EntityWriteOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        options ??= EntityWriteOptions.Default;

        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"实体类型未在 DbContext 中注册：{typeof(TEntity).Name}");

        // 1. 加载已存在实体（含所有 M:N 集合）
        var query = context.Set<TEntity>().AsQueryable();
        foreach (var nav in entityType.GetSkipNavigations())
            query = query.Include(nav.Name);

        var dbEntity = await query.FirstOrDefaultAsync(e => e.Id == entity.Id, cancellationToken);
        if (dbEntity is null)
        {
            errs.Add("更新的数据不存在", key: nameof(entity.Id), code: "NOT_FOUND");
            return false;
        }

        // 2. 唯一性验证（更新场景排除自身 ID）
        if (!await RunUniqueValidatorsAsync(context, options, ignoreSelfId: true, cancellationToken))
            return false;

        // 3. 字段级权限：检查"实际要改"的字段（PostedProperties 或全量）
        if (!await CheckFieldPermissionsAsync(context, entity, options, isCreate: false, cancellationToken))
            return false;

        // 4. 把客户端提交的标量字段补丁到加载的实体上
        PatchScalarProperties(context, dbEntity, entity, options);

        // 5. M:N 关系 diff —— 仅处理 options.PostedNavigations 中显式列出的导航
        if (options.PostedNavigations is { Count: > 0 })
            RelationProcessor.DiffSkipNavigations(entityType, dbEntity, entity, options.PostedNavigations);

        // 6. 用户钩子
        if (options.BeforeSaveAsync is not null)
            await options.BeforeSaveAsync(cancellationToken);

        if (!errs.IsValid) return false;

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ============================================================
    // Delete
    // ============================================================
    public async Task<bool> DeleteAsync<TEntity>(
        DbContext context,
        TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity
    {
        var exists = await context.Set<TEntity>().AsNoTracking()
            .AnyAsync(e => e.Id == entity.Id, cancellationToken);
        if (!exists)
        {
            errs.Add("删除的数据不存在", key: nameof(entity.Id), code: "NOT_FOUND");
            return false;
        }

        // 软删除转换在 AuditInterceptor 里统一处理
        context.Set<TEntity>().Remove(entity);

        if (!errs.IsValid) return false;

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ============================================================
    // 私有辅助
    // ============================================================

    private async Task<bool> RunUniqueValidatorsAsync(
        DbContext context,
        EntityWriteOptions options,
        bool ignoreSelfId,
        CancellationToken cancellationToken)
    {
        if (options.UniqueValidators is null) return true;

        foreach (var validator in options.UniqueValidators)
            await validator.ValidateAsync(context, errs, ignoreSelfId, cancellationToken);

        return errs.IsValid;
    }

    /// <summary>
    /// 字段级权限检查。
    ///
    /// Create 场景：检查所有受控字段（FieldPermissions 中列出的且实体上有值/非默认的）。
    /// Update 场景：检查"将要被写入"的字段（PostedProperties 列出 或 null 时全部受控字段）。
    ///
    /// 任一字段权限不足即收集错误并返回 false。
    /// </summary>
    private async Task<bool> CheckFieldPermissionsAsync<TEntity>(
        DbContext context,
        TEntity entity,
        EntityWriteOptions options,
        bool isCreate,
        CancellationToken cancellationToken)
        where TEntity : class, IBaseEntity
    {
        if (options.FieldPermissions is null || options.FieldPermissions.Count == 0)
            return true;

        // Update 场景下，根据 PostedProperties 收窄要检查的字段集
        // Create 场景下，全部受控字段都要检查
        IEnumerable<KeyValuePair<string, string>> toCheck = isCreate
            ? options.FieldPermissions
            : options.PostedProperties is null
                ? options.FieldPermissions                                    // 全量更新：检查全部
                : options.FieldPermissions.Where(kv => options.PostedProperties.Contains(kv.Key));

        var allOk = true;
        foreach (var (fieldName, requiredKey) in toCheck)
        {
            var ok = await permissions.HasAsync(requiredKey, cancellationToken);
            if (!ok)
            {
                errs.Add($"字段 {fieldName} 需要权限：{requiredKey}", key: fieldName, code: "FIELD_PERM");
                allOk = false;
            }
        }

        return allOk;
    }

    /// <summary>
    /// 扫描实体的 [Sequence] 字段，对值为空的字段调用 ISequenceGenerator 自动填充。
    /// 仅 Create 时调用；Update 不重新生成（流水号一旦分配不应变更）。
    /// </summary>
    private async Task FillSequenceFieldsAsync<TEntity>(TEntity entity, CancellationToken cancellationToken)
        where TEntity : class, IBaseEntity
    {
        if (sequenceGenerator is null) return;

        var fields = SequenceFieldCache.GetOrAdd(typeof(TEntity), static t =>
            t.GetProperties()
                .Select(p => (Prop: p, Attr: p.GetCustomAttribute<SequenceAttribute>()))
                .Where(x => x.Attr is not null && x.Prop.PropertyType == typeof(string))
                .Select(x => (x.Prop, Attr: x.Attr!))
                .ToList());

        foreach (var (prop, attr) in fields)
        {
            var current = prop.GetValue(entity) as string;
            if (!string.IsNullOrEmpty(current)) continue;   // 客户端已传值则保留

            var generated = await sequenceGenerator.NextAsync(attr.SequenceKey, attr.Format, cancellationToken);
            prop.SetValue(entity, generated);
        }
    }

    /// <summary>
    /// 审计字段名集合 —— 这些字段专属 AuditInterceptor，EntityService 永不写入。
    /// 即使被列入 PostedProperties 也会被忽略，防止客户端篡改审计信息。
    /// </summary>
    private static readonly HashSet<string> AuditFieldNames = new(StringComparer.Ordinal)
    {
        nameof(ITimerEntity.CreateTime),
        nameof(ITimerEntity.CreateBy),
        nameof(ITimerEntity.UpdateTime),
        nameof(ITimerEntity.UpdateBy),
        nameof(ISoftDeleteEntity.IsSoftDelete),
        nameof(ISoftDeleteEntity.DeleteTime),
        nameof(ISoftDeleteEntity.DeleteBy),
    };

    /// <summary>
    /// 把 <paramref name="source"/>（客户端提交）的标量属性补丁到 <paramref name="target"/>（DB 加载的跟踪实体）。
    ///
    /// - PostedProperties == null：拷贝全部标量属性（除主键和审计字段外）
    /// - PostedProperties != null：仅拷贝指定属性（仍然跳过主键和审计字段）
    /// - 不拷贝导航属性（保留 dbEntity 现有引用；skip navigation 由 DiffSkipNavigations 处理）
    /// </summary>
    private static void PatchScalarProperties<TEntity>(
        DbContext context,
        TEntity target,
        TEntity source,
        EntityWriteOptions options)
        where TEntity : class, IBaseEntity
    {
        var entry = context.Entry(target);

        foreach (var property in entry.Properties)
        {
            var metadata = property.Metadata;

            if (metadata.IsPrimaryKey()) continue;
            if (AuditFieldNames.Contains(metadata.Name)) continue;
            if (options.PostedProperties is not null && !options.PostedProperties.Contains(metadata.Name))
                continue;

            var sourceValue = metadata.PropertyInfo?.GetValue(source);
            property.CurrentValue = sourceValue;
        }
    }
}
