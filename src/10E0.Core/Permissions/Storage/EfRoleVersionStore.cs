using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Permissions.Storage;

/// <summary>
/// <see cref="IRoleVersionStore"/> 的 EF Core + IMemoryCache L1 实现。
///
/// L1 策略：版本号本身是"高频读、低频写、单调递增"的特征数据，5 秒 TTL 完全够用。
/// 5 秒窗内即使管理员 revoke 了一次 token，也最多 5 秒内才生效 — 比原来 30 分钟
/// access token 寿命短一个数量级，仍远低于"安全可接受"的容忍度。
///
/// #37: cache key 走 <see cref="ICacheKeyNamespace"/> —— 多租户场景下按 tenantId 隔离。
/// </summary>
public sealed class EfRoleVersionStore<TContext>(
    IDbContextFactory<TContext> contextFactory,
    ICacheKeyNamespace keyNamespace,
    IMemoryCache cache,
    TimeProvider timeProvider) : IRoleVersionStore
    where TContext : DbContext
{
    private static readonly TimeSpan L1Ttl = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _time = timeProvider;

    public async Task<IReadOnlyDictionary<string, long>> GetCurrentVersionsAsync(
        IReadOnlyCollection<string> roleCodes,
        CancellationToken cancellationToken = default)
    {
        if (roleCodes.Count == 0)
            return new Dictionary<string, long>();

        var result = new Dictionary<string, long>(roleCodes.Count, StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var code in roleCodes)
        {
            if (cache.TryGetValue(CacheKey(code), out long v))
                result[code] = v;
            else
                missing.Add(code);
        }

        if (missing.Count == 0) return result;

        // 一次 DB 拉取所有未命中角色
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await ctx.Set<TenE0Role>()
            .AsNoTracking()
            .Where(r => missing.Contains(r.Code))
            .Select(r => new { r.Code, r.Version })
            .ToListAsync(cancellationToken);

        // L1 写入（即使 version=0 也缓存，避免每个请求都查不存在的角色）。
        // #101: 当 MemoryCacheOptions.SizeLimit 已设时，ICacheEntry.Size 必须显式赋值，
        // 否则 .NET MemoryCache 抛 InvalidOperationException。这里 1 entry = 1 unit（粗算）。
        var now = _time.GetUtcNow();
        foreach (var row in rows)
        {
            using var entry = cache.CreateEntry(CacheKey(row.Code));
            entry.AbsoluteExpiration = now.Add(L1Ttl);
            entry.Size = 1L;
            entry.Value = row.Version;
            result[row.Code] = row.Version;
        }
        // 任何 missing 但 DB 也没有的角色 — version=0
        foreach (var code in missing)
        {
            if (!result.ContainsKey(code))
            {
                using var entry = cache.CreateEntry(CacheKey(code));
                entry.AbsoluteExpiration = now.Add(L1Ttl);
                entry.Size = 1L;
                entry.Value = 0L;
                result[code] = 0L;
            }
        }

        return result;
    }

    private string CacheKey(string roleCode) => keyNamespace.RoleVersionKey(roleCode);
}
