using TenE0.Core.EntityService.Validators;

namespace TenE0.Core.EntityService;

/// <summary>
/// 写操作选项。
///
/// 替代旧 BaseCMD 上散落的 PostedProp / OpenInner / UpdateAll / AuthCUD 等字段，
/// 统一收敛到一个不可变选项对象，命令本身不再被这些"控制位"污染。
/// </summary>
public sealed record EntityWriteOptions
{
    /// <summary>
    /// Update 场景：客户端实际提交的标量字段名集合。
    /// - null：写入全部标量字段（除审计字段和主键外）
    /// - 非空集合：仅写入这些字段（部分更新）
    ///
    /// 注意：审计字段（CreateTime/UpdateBy/IsSoftDelete 等）永远不被写入，
    /// 即使被列入此集合也会被忽略 —— 这些字段专属 AuditInterceptor。
    /// </summary>
    public IReadOnlySet<string>? PostedProperties { get; init; }

    /// <summary>
    /// Update 场景：显式 opt-in 哪些 skip navigation（M:N）需要被 diff 处理。
    /// - null 或空：不处理任何 M:N（保持 DB 现状）
    /// - 非空：只处理列出的 M:N 导航
    ///
    /// 为什么要 opt-in 而不是默认处理：实体类常把集合属性默认初始化为空列表，
    /// 若默认全处理则会把"未传"误判为"清空"，丢失关联数据。这是旧 E0 的一个隐患。
    /// </summary>
    public IReadOnlySet<string>? PostedNavigations { get; init; }

    /// <summary>
    /// Create 场景：true 时保留实体上的导航属性（用于带 M:N 关联的创建）。
    /// false（默认）清理普通导航属性，防止级联写入。
    /// </summary>
    public bool KeepNavigationProperties { get; init; }

    /// <summary>
    /// 唯一性验证器集合。任意一个失败即收集错误到 IErrs 并阻止保存。
    /// </summary>
    public IReadOnlyList<IUniqueValidator>? UniqueValidators { get; init; }

    /// <summary>保存前钩子，在 SaveChanges 之前、所有处理完成之后调用。</summary>
    public Func<CancellationToken, Task>? BeforeSaveAsync { get; init; }

    /// <summary>
    /// 字段级权限映射：字段名 → 所需权限 key。
    /// 写操作时检查：若某字段在 PostedProperties（或全量更新场景下被实际修改），且当前用户不具备对应权限，则报错。
    ///
    /// 替代旧 BaseEntityServer.Authorization 内联字段级权限检查 — 但更声明式、与命令分离。
    /// 例：FieldPermissions = { ["Salary"] = "user.update.salary" }
    /// </summary>
    public IReadOnlyDictionary<string, string>? FieldPermissions { get; init; }

    /// <summary>空选项（最常用，全字段写入、清理导航属性、无唯一性验证）。</summary>
    public static EntityWriteOptions Default { get; } = new();
}
