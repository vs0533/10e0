using Microsoft.Extensions.Options;

namespace TenE0.Core.Auditing;

/// <summary>
/// <see cref="IAuditLogSink"/> 默认实现：写入进程级 <see cref="AuditLogChannel"/>，由后台 worker 异步落库。
///
/// <para>
/// <b>生命周期 = Scoped：</b>每请求一个实例，构造时读 <c>IOptionsSnapshot</c> 拿当前配置（含
/// <see cref="AuditOptions.Enabled"/> 总开关）。Channel 本身是 Singleton（见 <see cref="AuditLogChannel"/>），
/// 所以 Sink 是"轻量入队器"，不持有任何跨请求状态。
/// </para>
/// <para>
/// <b>非阻塞保证：</b>所有写入路径都用 <see cref="AuditLogChannel.TryWrite"/>（同步，不 await），
/// Channel 满时按 <see cref="AuditOptions.ChannelFullMode"/> 策略处理（默认 DropOldest），永不抛异常给业务。
/// </para>
/// </summary>
public sealed class AuditLogSink(
    AuditLogChannel channel,
    IOptions<AuditOptions> options,
    TimeProvider timeProvider) : IAuditLogSink
{
    private readonly AuditOptions _options = options.Value;

    public Task EnqueueAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return Task.CompletedTask;

        entry.CreateTime = timeProvider.GetUtcNow();
        // TryWrite 非 await：立即返回，不阻塞业务请求。
        // Channel 已 Complete（停机）时返回 false，条目丢弃（best-effort）。
        channel.TryWrite(new AuditChannelItem.Op(entry));
        return Task.CompletedTask;
    }

    public Task WriteLoginAsync(LoginLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return Task.CompletedTask;

        entry.CreateTime = timeProvider.GetUtcNow();
        channel.TryWrite(new AuditChannelItem.Login(entry));
        return Task.CompletedTask;
    }
}

/// <summary>
/// 审计总开关关闭时的空实现 Sink —— 保留接口便于"未启用审计"场景的零成本注入。
/// 当前 <see cref="AuditLogSink"/> 已内置 Enabled 短路，此类型主要作为文档化占位，
/// 业务方也可显式注册它来完全关闭审计写入（连 Channel 都不建）。
/// </summary>
public sealed class NullAuditLogSink : IAuditLogSink
{
    public Task EnqueueAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task WriteLoginAsync(LoginLogEntry entry, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
