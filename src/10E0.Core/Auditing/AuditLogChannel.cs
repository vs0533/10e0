using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Auditing;

/// <summary>
/// 进程级审计日志 Channel —— <see cref="AuditLogSink"/>（请求侧入队）与
/// <see cref="AuditLogRelayWorker{TContext}"/>（后台出队）共享的单一有界通道。
///
/// <para>
/// <b>为何是 Singleton：</b>Sink 注册为 Scoped（每请求一个），但 Channel 必须跨请求共享，
/// 否则 worker 无法读到其他请求 scope 里 Sink 入队的条目。用独立 Singleton 类型承载 Channel，
/// 避免把 Sink 本身做成 Singleton（Sink 依赖 Scoped 的 options snapshot 等）。
/// </para>
/// <para>
/// <b>为何区分两类条目：</b>操作审计（<see cref="AuditLogEntry"/>）与登录审计
/// （<see cref="LoginLogEntry"/>）落不同的表，但走同一个 Channel + 同一个 worker，
/// 用 <see cref="AuditChannelItem"/> 联合体区分，减少后台服务数量。
/// </para>
/// </summary>
public sealed class AuditLogChannel : IDisposable
{
    private readonly Channel<AuditChannelItem> _channel;

    public AuditLogChannel(IOptions<AuditOptions> options)
    {
        var opt = options.Value;
        _channel = Channel.CreateBounded<AuditChannelItem>(new BoundedChannelOptions(opt.ChannelCapacity)
        {
            // 满时丢最老的一条：业务请求永不被审计写入阻塞（best-effort 契约）。
            FullMode = opt.ChannelFullMode,
            // 单读单写场景：关掉这些开销。
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>入队（Sink 调用）。返回 false 表示 Channel 已完成（应用停机中），调用方应丢弃。</summary>
    public bool TryWrite(AuditChannelItem item) => _channel.Writer.TryWrite(item);

    /// <summary>读端（Worker 调用）。</summary>
    public ChannelReader<AuditChannelItem> Reader => _channel.Reader;

    /// <summary>标记 Channel 完成（停机时 worker 调用，让 Reader 结束等待）。</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public void Dispose() => _channel.Writer.TryComplete();
}

/// <summary>Channel 条目联合体：操作审计或登录审计二选一。</summary>
public abstract record AuditChannelItem
{
    private AuditChannelItem() { }

    /// <summary>已盖好时间戳的操作审计条目。</summary>
    public sealed record Op(AuditLogEntry Entry) : AuditChannelItem;

    /// <summary>已盖好时间戳的登录审计条目。</summary>
    public sealed record Login(LoginLogEntry Entry) : AuditChannelItem;
}
