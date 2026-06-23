namespace TenE0.Core.Caching;

/// <summary>
/// L1 (进程内 <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>) 容量策略。
/// 控制 <see cref="Microsoft.Extensions.Caching.Memory.MemoryCacheOptions.SizeLimit"/> 与
/// <see cref="Microsoft.Extensions.Caching.Memory.MemoryCacheOptions.CompactionPercentage"/>，
/// 给 framework 默认注册一个兜底上限，避免无 SizeLimit 时恶意构造的 key 调用灌满进程内存。
///
/// 注意：本类管"内存容量策略"；<see cref="CacheOptions"/> 管"TTL 过期策略"，是独立维度。
///
/// #101: 16 MB 是 framework default —— 覆盖典型业务热数据集（Permissions 角色权限 +
/// Outbox relay 高频 key + 角色版本号），但对恶意构造的 key 调用快速触发 SizeLimit 驱逐，
/// 让异常暴露而非悄悄 OOM。业务项目可通过 <c>AddTenE0Caching(opts =&gt; opts with { ... })</c>
/// 委托重载或 <c>AddTenE0Caching(configuration)</c> IConfiguration 重载（读 "Caching" 节）覆盖。
///
/// 容量调优指引（#101 review 反馈）：典型 4KB/entry 的 role permission 场景，16 MB ≈ 4000 entries；
/// 命中率敏感业务（如 outbox relay 高 QPS）建议调高 SizeLimit 到 32~64 MB 并把
/// CompactionPercentage 调到 0.10~0.20 减少频繁 compaction 引发 L1 miss 风暴。
/// </summary>
public sealed record CachingOptions
{
    /// <summary>配置节名（用于 <c>services.Configure&lt;CachingOptions&gt;(config.GetSection(...))</c>）。</summary>
    public const string SectionName = "Caching";

    /// <summary>Framework 默认策略：16 MB SizeLimit + 5% 压缩（与 MemoryCache 默认 compaction 对齐）。</summary>
    public static CachingOptions Default { get; } = new()
    {
        SizeLimit = DefaultSizeLimit,
        CompactionPercentage = 0.05,
    };

    /// <summary>Framework 默认 SizeLimit = 16 MB。</summary>
    public const long DefaultSizeLimit = 16L * 1024 * 1024;

    /// <summary>
    /// L1 MemoryCache 的 entry 数上限。null = 不限制（不推荐，仅用于业务项目显式覆盖），
    /// 生产环境写 null 会让 #101 OOM 防护回归。
    /// </summary>
    public required long? SizeLimit { get; init; }

    /// <summary>触发 SizeLimit 时一次驱逐百分比（0.0 ~ 1.0）。</summary>
    public required double CompactionPercentage { get; init; }
}
