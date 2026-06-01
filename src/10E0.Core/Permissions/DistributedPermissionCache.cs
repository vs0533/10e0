using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Permissions;

/// <summary>
/// IPermissionCache 基于 IDistributedCache 的默认实现。
/// 一期的 AddTenE0Core 已注册 AddDistributedMemoryCache；生产可换 Redis。
///
/// "InvalidateAll" 由于 IDistributedCache 缺少枚举能力，采用 version stamp 策略：
/// 每次 InvalidateAll 递增一个版本号，缓存 key 拼上版本，旧 key 自动失效（靠 TTL 兜底）。
/// </summary>
internal sealed class DistributedPermissionCache(
    IDistributedCache cache,
    IOptions<PermissionsOptions> options) : IPermissionCache
{
    private const string VersionKey = "perm-cache:version";
    private readonly PermissionsOptions _options = options.Value;

    public async Task<IReadOnlySet<string>?> GetRolePermissionsAsync(string roleCode, CancellationToken cancellationToken = default)
    {
        var key = await BuildKeyAsync(roleCode, cancellationToken);
        var json = await cache.GetStringAsync(key, cancellationToken);
        return json is null
            ? null
            : JsonSerializer.Deserialize<HashSet<string>>(json);
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
        // 通过自增 version 让所有旧 key 失效；旧条目靠 TTL 自然清理
        var current = await cache.GetStringAsync(VersionKey, cancellationToken);
        var next = (long.TryParse(current, out var v) ? v : 0) + 1;
        await cache.SetStringAsync(VersionKey, next.ToString(), cancellationToken);
    }

    private async Task<string> BuildKeyAsync(string roleCode, CancellationToken cancellationToken)
    {
        var version = await cache.GetStringAsync(VersionKey, cancellationToken) ?? "0";
        return $"perm-role:v{version}:{roleCode}";
    }
}
