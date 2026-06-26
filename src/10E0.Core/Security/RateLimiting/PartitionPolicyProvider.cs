using System.Threading.RateLimiting;

namespace TenE0.Core.Security.RateLimiting;

/// <summary>
/// 限流分区策略解析（issue #162）。
///
/// <para>
/// 把 <see cref="RateLimitOptions"/> 中的声明式规则，翻译成 ASP.NET Core 内置
/// <c>RateLimiter</c> 能识别的 <see cref="RateLimitPartition{TKey}"/>。
/// 通过最长前缀匹配决定应用哪组规则；同一路径可叠加多条规则（如同时按 IP + User 限流）。
/// </para>
///
/// <para>
/// <b>为什么用自定义分区而非 <c>global limiter</c></b>：全局 limiter 只能一种策略，
/// 无法对 <c>/auth/login</c> 按 IP、<c>/auth/refresh</c> 按用户分别配额。
/// 自定义 <see cref="RateLimitPartition{TKey}"/> 让每条路径独立配额，互不干扰。
/// </para>
///
/// <para><b>单路径单规则</b>：ASP.NET Core <c>AddPolicy</c> 回调一次只能返回一个
/// <see cref="RateLimitPartition{TKey}"/>，故 <c>ResolveRules</c> 选中的规则列表里只有
/// <b>首条</b>生效。业务方若需"同一路径多维度叠加限流"，请改用
/// <c>PartitionedRateLimiter.Create</c> 自行串联（任一 limiter 拒绝即拒），
/// 或在 <see cref="RateLimitOptions.EndpointRules"/> 把多维度需求拆到更细的路径前缀。</para>
///
/// <para><b>分区 key 字符串拼接约定</b>：</para>
/// <list type="bullet">
/// <item><see cref="PartitionKind.Ip"/> → <c>"ip:{ip}"</c></item>
/// <item><see cref="PartitionKind.User"/> → <c>"user:{user}|anon:{ip}"</c>（未登录回退到匿名 IP 桶）</item>
/// <item><see cref="PartitionKind.IpAndEndpoint"/> → <c>"ip-ep:{ip}|{path}"</c></item>
/// <item><see cref="PartitionKind.UserAndEndpoint"/> → <c>"user-ep:{user}|anon:{ip}|{path}"</c></item>
/// </list>
/// </para>
/// </summary>
public static class PartitionPolicyProvider
{
    /// <summary>
    /// 把一组匹配到的规则翻译为 <see cref="RateLimitPartition{TKey}"/> 序列。
    /// 调用方（<c>AddPolicy</c> 回调）拿到序列后逐条加入 limiter pipeline。
    /// </summary>
    /// <param name="ip">客户端 IP（<c>"unknown"</c> 兜底）。</param>
    /// <param name="user">已认证用户名（未认证传 <c>null</c>）。</param>
    /// <param name="path">请求路径（不带 query）。</param>
    /// <param name="rules">本路径命中的规则（已由 <see cref="ResolveRules"/> 选定）。</param>
    public static IReadOnlyList<RateLimitPartition<string>> BuildPartitions(
        string ip,
        string? user,
        string path,
        IReadOnlyList<RateLimitRule> rules)
    {
        var partitions = new List<RateLimitPartition<string>>(rules.Count);
        foreach (var rule in rules)
        {
            var partitionKey = BuildPartitionKey(rule.Partition, ip, user, path);
            var permitLimit = rule.PermitLimit;
            var window = rule.Window;
            var queueLimit = rule.QueueLimit;

            partitions.Add(queueLimit > 0
                ? RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    SegmentsPerWindow = 2,
                })
                : RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    // 不排队：超出立即拒绝（OnRejected 写 429）
                    QueueLimit = 0,
                }));
        }
        return partitions;
    }

    /// <summary>
    /// 按 <paramref name="path"/> 最长前缀匹配选择规则。
    /// 先匹配 <see cref="RateLimitOptions.EndpointRules"/>（最长前缀），命中则用之；
    /// 否则退回 <see cref="RateLimitOptions.GlobalRules"/>。
    /// </summary>
    public static IReadOnlyList<RateLimitRule> ResolveRules(string path, RateLimitOptions options)
    {
        // 最长前缀匹配：在所有 endpoint rules 中找 path 以之开头的最长那个。
        // 例如 /auth/login?q=1 命中 /auth/login 而非 /auth。
        string? bestMatch = null;
        var bestLen = -1;
        foreach (var prefix in options.EndpointRules.Keys)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > bestLen)
            {
                bestMatch = prefix;
                bestLen = prefix.Length;
            }
        }

        if (bestMatch is not null)
            return options.EndpointRules[bestMatch];

        return options.GlobalRules;
    }

    /// <summary>
    /// 构造单条规则对应的分区 key。匿名用户（user 为 null）按 IP 桶防刷。
    /// </summary>
    public static string BuildPartitionKey(PartitionKind kind, string ip, string? user, string path) => kind switch
    {
        PartitionKind.Ip => $"ip:{ip}",
        // 未登录用户不能共用 user 桶（否则共享一个匿名桶，互相挤压）→ 回退到匿名 IP 桶。
        PartitionKind.User when user is null => $"user:anon|{ip}",
        PartitionKind.User => $"user:{user}",
        PartitionKind.IpAndEndpoint => $"ip-ep:{ip}|{path}",
        PartitionKind.UserAndEndpoint when user is null => $"user-ep:anon|{ip}|{path}",
        PartitionKind.UserAndEndpoint => $"user-ep:{user}|{path}",
        _ => $"ip:{ip}",
    };
}
