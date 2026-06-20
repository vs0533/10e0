namespace TenE0.Core.Caching;

/// <summary>
/// 原子计数器 — 全局单调递增的 long 计数。
///
/// 目的：替代
///   <c>var v = await cache.GetAsync(key); var n = parse(v) + 1; await cache.SetAsync(key, n);</c>
/// 的非原子三步操作，避免并发丢增。
///
/// 典型用例：
/// - <see cref="Permissions.IPermissionCache.InvalidateAllAsync"/> 内部用 <c>IncrementAsync</c>
///   替换"读-改-写"version stamp 竞态。
/// - 业务项目的乐观锁 / 幂等发号器。
///
/// 实现要求：
/// - <see cref="IncrementAsync"/> 必须原子 — Redis 用 <c>INCR</c>，内存用 <c>Interlocked.Increment</c>，
///   EF 用 <c>UPDATE ... OUTPUT INSERTED.Value</c> 单语句。
/// - 不存在的 key 视为 0，<c>IncrementAsync</c> 后必须返回 1（而非 0）。
/// - <see cref="GetAsync"/> 也不存在的 key 必须返回 0。
/// </summary>
public interface IAtomicCounter
{
    /// <summary>
    /// 原子自增并返回自增后的新值。key 不存在则初始化为 1。
    /// </summary>
    /// <param name="key">计数器 key。调用方负责唯一性（如 <c>"perm-cache:version"</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>自增后的新值（≥ 1）。</returns>
    Task<long> IncrementAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取当前值，不修改。key 不存在返回 0。
    /// </summary>
    /// <param name="key">计数器 key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<long> GetAsync(string key, CancellationToken cancellationToken = default);
}
