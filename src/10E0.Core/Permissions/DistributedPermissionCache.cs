using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Caching;

namespace TenE0.Core.Permissions;

/// <summary>
/// IPermissionCache 基于 IDistributedCache 的默认实现。
/// 一期的 AddTenE0Core 已注册 AddDistributedMemoryCache；生产可换 Redis。
///
/// "InvalidateAll" 通过 <see cref="IAtomicCounter"/> 原子自增版本号实现，
/// 缓存 key 拼上版本，旧 key 自动失效（靠 TTL 兜底）。
/// 替代原本"GetString → Parse → +1 → SetString"的非原子三步操作，
/// 避免并发丢增。
///
/// #37: 所有 cache key 都从 <see cref="ICacheKeyNamespace"/> 走，业务方可注入
/// 多租户 namespace 顶层前缀（如 "acme"），多租户共享 Redis 不串数据。
/// </summary>
internal sealed class DistributedPermissionCache(
    IDistributedCache cache,
    IAtomicCounter counter,
    ICacheKeyNamespace keyNamespace,
    IOptions<PermissionsOptions> options) : IPermissionCache
{
    private readonly PermissionsOptions _options = options.Value;

    public async Task<IReadOnlySet<string>?> GetRolePermissionsAsync(string roleCode, CancellationToken cancellationToken = default)
    {
        var key = await BuildKeyAsync(roleCode, cancellationToken);
        var json = await cache.GetStringAsync(key, cancellationToken);
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<HashSet<string>>(json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // #114: 缓存里的旧版/损坏 JSON（升级部署、半截写入、并发 race）不能让反序列化异常
            // 一直冒泡，否则 PermissionEvaluator 的每次 HasAsync 都会吃掉一次异常，权限更新最多延迟
            // 到 TTL 过期。把坏值当 cache miss 处理：删掉坏缓存 → 返回 null → evaluator 走 store 重读。
            // NotSupportedException/InvalidOperationException 是 STJ source-generator 在 AOT/trimming
            // 路径下对 type mismatch 抛的；OperationCanceledException 不在名单里，必须向上传。
            // 清缓存用 CancellationToken.None：caller 取消时不应阻止 fallback，否则权限判断会炸出去。
            try
            {
                await cache.RemoveAsync(key, CancellationToken.None);
            }
            catch
            {
                // 清缓存失败不影响回退路径——下次读还会再试。
            }
            return null;
        }
    }

    public async Task SetRolePermissionsAsync(string roleCode, IReadOnlySet<string> permissions, CancellationToken cancellationToken = default)
    {
        var key = await BuildKeyAsync(roleCode, cancellationToken);
        await cache.SetStringAsync(
            key,
            JsonSerializer.Serialize(permissions),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _options.CacheDuration },
            cancellationToken);
    }

    public async Task InvalidateRoleAsync(string roleCode, CancellationToken cancellationToken = default)
    {
        var key = await BuildKeyAsync(roleCode, cancellationToken);
        await cache.RemoveAsync(key, cancellationToken);
    }

    public async Task InvalidateAllAsync(CancellationToken cancellationToken = default)
    {
        // 原子自增：所有并发调用方拿到的都是单调递增的版本号，无丢增风险。
        // #37: key 走 ICacheKeyNamespace —— 多租户下 InvalidateAll 只清本租户的版本号。
        await counter.IncrementAsync(keyNamespace.PermissionVersionKey(), cancellationToken);
    }

    private async Task<string> BuildKeyAsync(string roleCode, CancellationToken cancellationToken)
    {
        var version = await counter.GetAsync(keyNamespace.PermissionVersionKey(), cancellationToken);
        return keyNamespace.PermissionRoleKey(version, roleCode);
    }
}
