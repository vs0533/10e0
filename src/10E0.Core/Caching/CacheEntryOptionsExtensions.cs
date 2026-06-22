namespace TenE0.Core.Caching;

/// <summary>
/// 缓存条目选项的辅助扩展 — 把 DistributedCacheEntryOptions 里的"绝对过期时间点"
/// 反解成"距离现在的 TimeSpan"。
///
/// 用途：单测里自建 IDistributedCache 时（参考 InMemoryDistributedCache 实现），
/// 需要从传入的 DistributedCacheEntryOptions 算出 TTL。但 AbsoluteExpiration
/// 暴露的是 DateTimeOffset?（绝对时间点），不方便直接做"剩余多久"的算术；
/// AbsoluteExpirationRelativeToNow 又是 TimeSpan?。二者择一：本扩展让测试代码
/// 可以写：
/// <code>
/// var ttl = options.AbsoluteExpiration?.RelativeToNow
///        ?? options.AbsoluteExpirationRelativeToNow
///        ?? TimeSpan.FromMinutes(5);
/// </code>
///
/// 语义上等价于 AbsoluteExpiration - DateTimeOffset.UtcNow，但下界截到 0：
/// 已过期的条目视为"立刻过期"，避免负值 TTL 导致内存 cache 实现误判。
///
/// 实现细节：本扩展用 C# 14 的 extension block 语法，把"接收者"声明为
/// 非可空 DateTimeOffset，而属性返回 TimeSpan?。这样 "?." 链式语法
/// x?.RelativeToNow 在 x 为 DateTimeOffset? 时合法（null 分支返回 null，
/// 非 null 分支在 DateTimeOffset 上查 RelativeToNow 属性）。
/// 如果把接收者声明为 DateTimeOffset?，则 "?." 后的非 null 分支会被
/// 推断为非空接收者（DateTimeOffset），导致 CS1929 "不包含定义"。
/// </summary>
public static class CacheEntryOptionsExtensions
{
    extension(DateTimeOffset absoluteExpiration)
    {
        /// <summary>
        /// 把绝对过期时间点转为相对 DateTimeOffset.UtcNow 的 TimeSpan；
        /// 已过期则返回 TimeSpan.Zero。返回类型为 TimeSpan? 是为了配合
        /// "?." 链式语法（null 接收者 → null 返回）。
        /// </summary>
        public TimeSpan? RelativeToNow
        {
            get
            {
                var remaining = absoluteExpiration - DateTimeOffset.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }
}
