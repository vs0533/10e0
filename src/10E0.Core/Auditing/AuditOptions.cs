using System.Threading.Channels;

namespace TenE0.Core.Auditing;

/// <summary>
/// 审计模块运行参数（issue #152 决策点 #1：同步 vs 异步落库）。
/// </summary>
public sealed class AuditOptions
{
    /// <summary>
    /// 审计总开关。关闭后 <see cref="AuditLogInterceptor"/> 不再捕获 diff，
    /// auth handler 也不再埋点（ Sink 的 EnqueueAsync/WriteLoginAsync 变成空操作）。
    /// 默认开启。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 审计拦截器捕获的实体操作类型黑名单。
    /// 默认排除框架自身的审计表/登录表（避免自引用：审计自身变更会无限递归），
    /// 以及 Outbox 表（与审计无关，徒增噪音）。
    /// 业务方可追加自己的实体类型名（CLR 类型简单名）。
    /// </summary>
    public HashSet<string> IgnoredEntityTypeNames { get; } =
    [
        nameof(TenE0AuditLog),
        nameof(TenE0LoginLog),
    ];

    /// <summary>
    /// Channel 容量上限。达到后新条目按 <see cref="ChannelFullMode"/> 策略处理。
    /// 默认 10000：足够缓冲突发流量，又不会让后台 worker 积压过深。
    /// </summary>
    public int ChannelCapacity { get; set; } = 10_000;

    /// <summary>
    /// Channel 满时的处理策略。默认 <see cref="BoundedChannelFullMode.DropOldest"/>：
    /// 丢弃最老的一条以腾出位置，保证业务请求永不被审计写入阻塞（best-effort 契约）。
    /// </summary>
    public BoundedChannelFullMode ChannelFullMode { get; set; } = BoundedChannelFullMode.DropOldest;

    /// <summary>
    /// 后台 worker 每轮从 Channel 读取的最大条目数。
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Channel 空时 worker 的轮询间隔。
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 应用停机时 drain 剩余 Channel 的最长等待时间。
    /// 超过后强制退出，未落库的条目丢失（best-effort）。
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
