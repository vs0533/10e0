namespace TenE0.Core.Realtime;

/// <summary>
/// 实时推送模块配置（#155）。
/// </summary>
public sealed class RealtimeOptions
{
    /// <summary>
    /// Hub 的路由前缀。<see cref="DependencyInjection.RealtimeExtensions.MapTenE0Hub"/> 据此注册
    /// <c>{HubPath}/notification</c> 端点。JWT WebSocket 认证中间件也按此前缀匹配 query token。
    /// </summary>
    public string HubPath { get; set; } = "/hub";

    /// <summary>
    /// 跨实例 backplane 模式。默认 <see cref="BackplaneMode.None"/>（单体直推）。
    /// 选 <see cref="BackplaneMode.Redis"/> 需提供 <see cref="IRealtimeBackplane"/> 的 Redis 实现（后续 issue）。
    /// </summary>
    public BackplaneMode Backplane { get; set; } = BackplaneMode.None;

    /// <summary>
    /// 组名前缀。<see cref="ClaimBasedGroupProvider"/> 据此产出组名（如 <c>user:alice</c>）。
    /// 业务方可整体替换 <see cref="IRealtimeGroupProvider"/>，本配置仅约束默认实现。
    /// </summary>
    public RealtimeGroupPrefixes GroupPrefixes { get; set; } = new();
}

/// <summary>backplane 模式枚举。</summary>
public enum BackplaneMode
{
    /// <summary>单体 / 开发环境：直推，无跨实例广播（<see cref="NoopRealtimeBackplane"/>）。</summary>
    None,

    /// <summary>多副本：经 Redis pub/sub 广播。实现留后续 issue。</summary>
    Redis,
}

/// <summary>默认组名前缀（<see cref="ClaimBasedGroupProvider"/> 用）。</summary>
public sealed class RealtimeGroupPrefixes
{
    public string User { get; set; } = "user:";
    public string Role { get; set; } = "role:";
    public string Tenant { get; set; } = "tenant:";
    public string Org { get; set; } = "org:";
}
