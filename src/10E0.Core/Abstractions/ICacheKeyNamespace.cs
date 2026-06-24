namespace TenE0.Core.Abstractions;

/// <summary>
/// 缓存键命名空间的抽象注入点（#37 Part 3）。
///
/// 解决：Core 把缓存键字面量散落在 3 个文件
/// （"user_info" / "perm-cache:version" / "perm-role:v{version}:{roleCode}" /
/// "role-version:{roleCode}"），多租户共享 Redis 时无 namespace 隔离 → 跨租户串数据。
///
/// 设计：把"前缀（静态字符串）"和"键构造（动态方法）"分两层暴露。
/// <list type="bullet">
///   <item>
///     <term>前缀属性</term>
///     <description>业务方 grep 时能直接看到 wire-format 字面量（<c>perm-cache:version</c>），
///     方便排查缓存污染问题。</description>
///   </item>
///   <item>
///     <term>键构造方法</term>
///     <description>默认无参实现与遗留字面量逐字一致；多租户实现可在此拼入 tenantId 顶级前缀。</description>
///   </item>
/// </list>
///
/// 生命周期：Singleton（纯函数，无状态；不持有任何缓存/连接）。
/// 多租户注册示例：
/// <code>
/// services.Replace(ServiceDescriptor.Singleton&lt;ICacheKeyNamespace,
///     new DefaultCacheKeyNamespace(tenantId: myTenantIdAccessor.Get())&gt;());
/// </code>
/// </summary>
public interface ICacheKeyNamespace
{
    /// <summary>
    /// 权限缓存版本号原子计数器的 key 前缀。
    /// 例：默认 "perm-cache:version"（<c>DistributedPermissionCache</c> 硬编码）。
    /// </summary>
    string PermissionVersionPrefix { get; }

    /// <summary>
    /// 单角色权限集合缓存 key 的角色段前缀。
    /// 例：默认 "perm-role"（<c>DistributedPermissionCache</c> 硬编码 "perm-role:v{version}:{roleCode}"）。
    /// </summary>
    string PermissionRolePrefix { get; }

    /// <summary>
    /// 单角色版本号 L1 缓存 key 前缀。
    /// 例：默认 "role-version"（<c>EfRoleVersionStore</c> 硬编码 "role-version:{roleCode}"）。
    /// </summary>
    string RoleVersionPrefix { get; }

    /// <summary>
    /// 用户信息缓存 key 前缀。
    /// 例：默认 "user_info"（<c>HttpCurrentUserContext</c> 硬编码 "user_info:{UserCode}"）。
    /// </summary>
    string UserInfoPrefix { get; }

    /// <summary>
    /// 数据字典选项列表缓存 key 前缀。
    /// 例：默认 "dict-items"（<c>DataDictionaryService</c> 用 "dict-items:{dictTypeCode}"）。
    /// </summary>
    string DictItemsPrefix { get; }

    /// <summary>
    /// 系统参数值缓存 key 前缀。
    /// 例：默认 "sys-param"（<c>SystemParameterStore</c> 用 "sys-param:{key}"）。
    /// </summary>
    string SystemParameterPrefix { get; }

    /// <summary>
    /// 构造权限版本号原子计数器的完整 cache key。
    /// </summary>
    /// <returns>无 tenantId 时 = <c>PermissionVersionPrefix</c>；
    /// 有 tenantId 时 = <c>"{tenantId}:{PermissionVersionPrefix}"</c>。</returns>
    string PermissionVersionKey();

    /// <summary>
    /// 构造单角色权限集合的完整 cache key。
    /// </summary>
    /// <param name="version">当前权限版本号（来自 <see cref="PermissionVersionKey"/> 解析）。</param>
    /// <param name="roleCode">角色 code。</param>
    /// <returns>无 tenantId 时 = <c>"perm-role:v{version}:{roleCode}"</c>；
    /// 有 tenantId 时 = <c>"{tenantId}:perm-role:v{version}:{roleCode}"</c>。</returns>
    string PermissionRoleKey(long version, string roleCode);

    /// <summary>
    /// 构造单角色版本号的完整 cache key。
    /// </summary>
    /// <param name="roleCode">角色 code。</param>
    /// <returns>无 tenantId 时 = <c>"role-version:{roleCode}"</c>；
    /// 有 tenantId 时 = <c>"{tenantId}:role-version:{roleCode}"</c>。</returns>
    string RoleVersionKey(string roleCode);

    /// <summary>
    /// 构造用户信息的完整 cache key。
    /// </summary>
    /// <param name="userCode">用户 code（<c>ICurrentUserContext.UserCode</c>）。</param>
    /// <returns>无 tenantId 时 = <c>"user_info:{userCode}"</c>；
    /// 有 tenantId 时 = <c>"{tenantId}:user_info:{userCode}"</c>。</returns>
    string UserInfoKey(string userCode);

    /// <summary>
    /// 构造某字典类型选项列表的完整 cache key。
    /// </summary>
    /// <param name="dictTypeCode">字典类型 Code。</param>
    /// <returns>无 tenantId 时 = <c>"dict-items:{dictTypeCode}"</c>；
    /// 有 tenantId 时 = <c>"{tenantId}:dict-items:{dictTypeCode}"</c>。</returns>
    string DictItemsKey(string dictTypeCode);

    /// <summary>
    /// 构造某系统参数值的完整 cache key。
    /// </summary>
    /// <param name="key">系统参数 Key。</param>
    /// <returns>无 tenantId 时 = <c>"sys-param:{key}"</c>；
    /// 有 tenantId 时 = <c>"{tenantId}:sys-param:{key}"</c>。</returns>
    string SystemParameterKey(string key);
}

/// <summary>
/// <see cref="ICacheKeyNamespace"/> 的默认实现。
///
/// <para>前缀属性与遗留硬编码字面量逐字一致：</para>
/// <list type="bullet">
///   <item><c>PermissionVersionPrefix = "perm-cache:version"</c></item>
///   <item><c>PermissionRolePrefix = "perm-role"</c></item>
///   <item><c>RoleVersionPrefix = "role-version"</c></item>
///   <item><c>UserInfoPrefix = "user_info"</c></item>
/// </list>
///
/// <para>键构造方法：</para>
/// <list type="bullet">
///   <item><c>tenantId</c> 为 null / 空 / 空白 → 走遗留单租户格式，向后兼容</item>
///   <item>非空 → 拼到所有 key 顶部，多租户共享 Redis 不串数据</item>
/// </list>
///
/// ctor 接收 <c>tenantId</c>（可为 null/空）—— 单租户部署直接
/// <c>new DefaultCacheKeyNamespace()</c>；多租户实现把租户 ID 解析器包成 delegate 注入。
/// </summary>
public sealed class DefaultCacheKeyNamespace : ICacheKeyNamespace
{
    private readonly string? _tenantId;

    /// <summary>无参 ctor：单租户场景，与遗留 literal 逐字一致。</summary>
    public DefaultCacheKeyNamespace()
        : this(tenantId: null)
    {
    }

    /// <summary>
    /// 带 tenantId ctor：多租户共享 Redis 场景。
    /// <paramref name="tenantId"/> 为 null/空/空白时按单租户处理，避免生成 ":xxx" 残缺 key。
    /// </summary>
    public DefaultCacheKeyNamespace(string? tenantId)
    {
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
    }

    /// <inheritdoc />
    public string PermissionVersionPrefix => "perm-cache:version";

    /// <inheritdoc />
    public string PermissionRolePrefix => "perm-role";

    /// <inheritdoc />
    public string RoleVersionPrefix => "role-version";

    /// <inheritdoc />
    public string UserInfoPrefix => "user_info";

    /// <inheritdoc />
    public string DictItemsPrefix => "dict-items";

    /// <inheritdoc />
    public string SystemParameterPrefix => "sys-param";

    /// <inheritdoc />
    public string PermissionVersionKey() => WithTenant(PermissionVersionPrefix);

    /// <inheritdoc />
    public string PermissionRoleKey(long version, string roleCode) =>
        WithTenant($"{PermissionRolePrefix}:v{version}:{roleCode}");

    /// <inheritdoc />
    public string RoleVersionKey(string roleCode) =>
        WithTenant($"{RoleVersionPrefix}:{roleCode}");

    /// <inheritdoc />
    public string UserInfoKey(string userCode) =>
        WithTenant($"{UserInfoPrefix}:{userCode}");

    /// <inheritdoc />
    public string DictItemsKey(string dictTypeCode) =>
        WithTenant($"{DictItemsPrefix}:{dictTypeCode}");

    /// <inheritdoc />
    public string SystemParameterKey(string key) =>
        WithTenant($"{SystemParameterPrefix}:{key}");

    private string WithTenant(string keyWithoutTenant) =>
        _tenantId is null ? keyWithoutTenant : $"{_tenantId}:{keyWithoutTenant}";
}
