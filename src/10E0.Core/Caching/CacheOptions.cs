namespace TenE0.Core.Caching;

/// <summary>
/// 多级缓存过期策略。L1 通常设短 TTL（5s-60s）以承受高 QPS；L2 设长 TTL（分钟-小时）
/// 跨进程共享。
///
/// 注意：L1 TTL < L2 TTL 是推荐配置 — L1 短过期让 stale 数据快速回源，
/// L2 长过期避免每次都打 DB / RPC。
/// </summary>
public sealed record CacheOptions
{
    /// <summary>默认策略：L1 = 5s，L2 = 5min。匹配 <see cref="Permissions.PermissionsOptions"/> 的现状。</summary>
    public static CacheOptions Default { get; } = new()
    {
        L1Duration = TimeSpan.FromSeconds(5),
        L2Duration = TimeSpan.FromMinutes(5),
    };

    /// <summary>L1 (进程内) 绝对过期时长。null = 不缓存到 L1（仅用 L2）。</summary>
    public required TimeSpan L1Duration { get; init; }

    /// <summary>L2 (分布式) 绝对过期时长。null = 不缓存到 L2（仅用 L1）。</summary>
    public required TimeSpan L2Duration { get; init; }
}
