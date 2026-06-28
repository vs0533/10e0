using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;
using TenE0.Core.Queries;

namespace TenE0.Core.EntityService;

/// <summary>
/// 实体读操作服务 —— <see cref="IEntityService"/> 的读侧对称。
///
/// 设计原则(与 <see cref="IEntityService"/> 一致):
/// <list type="bullet">
///   <item>DbContext 由调用方传入,服务本身无状态</item>
///   <item>自动复用 EF Named Query Filter(软删除 / 行级权限 / 租户),无需手写 <c>Where(IsSoftDelete==false)</c></item>
///   <item>提供显式旁路开关(<see cref="EntityReadOptions.BypassFilters"/>),取代危险的 <c>IgnoreQueryFilters()</c> 全量旁路</item>
///   <item>筛选字段经运行时白名单校验(<see cref="ReadFilter.Field"/> 必须是实体真实属性),防表达式注入</item>
/// </list>
///
/// 与 <c>IQueryHandler&lt;T&gt;</c> 的关系:本服务是 Handler 内部可用的工具,
/// <b>不强制</b>使用,也不替代业务 Handler(复杂 join / 投影仍由业务方自己写)。
/// </summary>
public interface IEntityQueryService
{
    /// <summary>
    /// 按主键查询单个实体(自动应用行级过滤;被过滤掉的行返回 null,与"不存在"语义一致)。
    /// </summary>
    Task<TEntity?> GetByIdAsync<TEntity>(
        DbContext context, string id,
        EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;

    /// <summary>
    /// 按主键查询并投影到 DTO(最常用:详情页直接出 View)。
    /// </summary>
    Task<TView?> GetByIdAsync<TEntity, TView>(
        DbContext context, string id,
        Expression<Func<TEntity, TView>> selector,
        EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;

    /// <summary>
    /// 列表查询(不分页,慎用;通常用 <see cref="PagedAsync{TEntity}"/>)。
    /// </summary>
    Task<List<TEntity>> ListAsync<TEntity>(
        DbContext context, EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;

    /// <summary>列表查询 + 投影到 DTO。</summary>
    Task<List<TView>> ListAsync<TEntity, TView>(
        DbContext context, Expression<Func<TEntity, TView>> selector,
        EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;

    /// <summary>分页查询(核心方法)。返回 <see cref="PagedResult{T}"/>。</summary>
    Task<PagedResult<TEntity>> PagedAsync<TEntity>(
        DbContext context, PagedQuery query,
        EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;

    /// <summary>分页查询 + 投影到 DTO(列表页标准用法)。</summary>
    Task<PagedResult<TView>> PagedAsync<TEntity, TView>(
        DbContext context, PagedQuery query,
        Expression<Func<TEntity, TView>> selector,
        EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;

    /// <summary>计数(自动应用行级过滤)。</summary>
    Task<int> CountAsync<TEntity>(
        DbContext context, EntityReadOptions? options = null, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;

    /// <summary>是否存在(自动应用行级过滤)。</summary>
    Task<bool> ExistsAsync<TEntity>(
        DbContext context, string id, CancellationToken cancellationToken = default)
        where TEntity : class, IBaseEntity;
}
