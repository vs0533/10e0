using System.Diagnostics.Metrics;

namespace TenE0.Core.Observability;

/// <summary>
/// 框架自定义指标集合（#161）。基于 .NET <c>System.Diagnostics.Metrics</c>，
/// 以 DI <b>Singleton</b> 持有 <see cref="Meter"/> 与各仪器。
///
/// <para>
/// <b>设计决策</b>（偏离 issue 草案的 <c>static</c> 设计）：
/// 用 DI Singleton 而非静态字段，契合本仓库"每个服务独立注入、可测试、不 ServiceLocator"原则；
/// 未注册任何读取者（MeterListener / OTel SDK / Prometheus exporter）时仪器近零开销，
/// 因此即使可观测性未完整启用也可安全埋点 —— <see cref="TenE0.Core.Cqrs.CommandDispatcher"/>
/// 与 <see cref="TenE0.Core.Events.Outbox.OutboxRelayService{TContext}"/> 通过
/// <c>serviceProvider.GetService&lt;TenE0Metrics&gt;()</c> 解析，未注册时为 <c>null</c> → no-op。
/// </para>
///
/// <para>
/// <b>指标清单</b>（Prometheus 命名 <c>tene0_*</c>）：
/// <list type="bullet">
/// <item><c>tene0.command.total</c>（Counter，tags: <c>command</c>, <c>result=success|failure</c>）—— CQRS 命令计数</item>
/// <item><c>tene0.command.duration</c>（Histogram，ms，tag: <c>command</c>）—— CQRS 命令耗时</item>
/// <item><c>tene0.outbox.delivered</c>（Counter，tag: <c>result=success|failure</c>）—— Outbox 投递结果</item>
/// <item><c>tene0.outbox.backlog</c>（ObservableGauge，count）—— Outbox 待投递积压</item>
/// </list>
/// </para>
///
/// <para>
/// 埋点位置：<see cref="TenE0.Core.Cqrs.CommandDispatcher.SendAsync{TResult}"/>（命令计数/耗时）；
/// <see cref="TenE0.Core.Events.Outbox.OutboxRelayService{TContext}.ProcessBatchAsync"/>（投递计数 + 每轮刷新积压）。
/// </para>
/// </summary>
public sealed class TenE0Metrics : IDisposable
{
    /// <summary>Meter 名，应用层 OTel SDK 用 <c>AddMeter(TenE0Metrics.MeterName)</c> 订阅。</summary>
    public const string MeterName = "TenE0";

    private const string MeterVersion = "1.0";

    /// <summary>Tag key 常量，避免散落字符串。</summary>
    public static class Tags
    {
        public const string Command = "command";
        public const string Result = "result";
        public const string Success = "success";
        public const string Failure = "failure";
    }

    private long _outboxBacklog;

    /// <summary>构造。创建 Meter 与全部仪器。</summary>
    public TenE0Metrics()
    {
        Meter = new Meter(MeterName, MeterVersion);

        CommandTotal = Meter.CreateCounter<long>(
            "tene0.command.total",
            unit: "count",
            description: "CQRS 命令总数（tag: command 类型名 / result=success|failure）");

        CommandDuration = Meter.CreateHistogram<double>(
            "tene0.command.duration",
            unit: "ms",
            description: "CQRS 命令耗时");

        OutboxDelivered = Meter.CreateCounter<long>(
            "tene0.outbox.delivered",
            unit: "count",
            description: "Outbox 投递结果（tag: result=success|failure）");

        // ObservableGauge：读取方（Prometheus / MeterListener）拉取时回调本 lambda 返回当前积压值。
        OutboxBacklog = Meter.CreateObservableGauge(
            "tene0.outbox.backlog",
            observeValue: () => new Measurement<long>(Interlocked.Read(ref _outboxBacklog)),
            unit: "count",
            description: "Outbox 待投递积压数");
    }

    /// <summary>底层 Meter（应用层 OTel 用 <c>AddSource</c> / <c>AddMeter</c> 订阅）。</summary>
    internal Meter Meter { get; }

    /// <summary>CQRS 命令计数器。tag <c>command</c> + <c>result</c>。</summary>
    public Counter<long> CommandTotal { get; }

    /// <summary>CQRS 命令耗时直方图（毫秒）。tag <c>command</c>。</summary>
    public Histogram<double> CommandDuration { get; }

    /// <summary>Outbox 投递结果计数器。tag <c>result</c>。</summary>
    public Counter<long> OutboxDelivered { get; }

    /// <summary>Outbox 积压可观测仪表。</summary>
    public ObservableGauge<long> OutboxBacklog { get; }

    /// <summary>
    /// 更新当前 Outbox 积压值。由 <c>OutboxRelayService</c> 每轮投递后写入。
    /// </summary>
    public void SetBacklog(long count) => Interlocked.Exchange(ref _outboxBacklog, count);

    /// <summary>释放底层 Meter。</summary>
    public void Dispose() => Meter.Dispose();
}
