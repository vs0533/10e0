using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Abstractions;
using TenE0.Core.Cqrs;
using TenE0.Core.Observability;

namespace TenE0.Core.Tests.Observability;

/// <summary>
/// #161 CQRS 埋点验证：CommandDispatcher.SendAsync 成功/失败后递增 tene0.command.total
/// 并记录 tene0.command.duration。用 MeterListener 捕获指标值断言。
/// </summary>
public sealed class TenE0MetricsTests
{
    internal sealed record ProbeCmd(string Value) : ICommand<string>;

    internal sealed class OkHandler : ICommandHandler<ProbeCmd, string>
    {
        public Task<string> HandleAsync(ProbeCmd command, CancellationToken cancellationToken) =>
            Task.FromResult(command.Value);
    }

    internal sealed class FailHandler : ICommandHandler<ProbeCmd, string>
    {
        public Task<string> HandleAsync(ProbeCmd command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task SendAsync_Success_IncrementsCommandTotalWithSuccessTag()
    {
        var metrics = new TenE0Metrics();
        var (successCount, failureCount, durationSamples) = (0L, 0L, 0);
        using var listener = CreateListener(metrics, (name, tags, value) =>
        {
            if (name == "tene0.command.total")
            {
                var result = TagValue(tags, TenE0Metrics.Tags.Result);
                if (result == TenE0Metrics.Tags.Success) successCount += (long)value;
                if (result == TenE0Metrics.Tags.Failure) failureCount += (long)value;
            }
            if (name == "tene0.command.duration") durationSamples++;
        });

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<ProbeCmd, string>>(new OkHandler());
        services.AddSingleton(metrics);
        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        var result = await dispatcher.SendAsync(new ProbeCmd("hi"));

        result.Should().Be("hi");
        successCount.Should().Be(1);
        failureCount.Should().Be(0);
        durationSamples.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task SendAsync_Failure_IncrementsCommandTotalWithFailureTag()
    {
        var metrics = new TenE0Metrics();
        var (successCount, failureCount) = (0L, 0L);
        using var listener = CreateListener(metrics, (name, tags, value) =>
        {
            if (name != "tene0.command.total") return;
            var result = TagValue(tags, TenE0Metrics.Tags.Result);
            if (result == TenE0Metrics.Tags.Success) successCount += (long)value;
            if (result == TenE0Metrics.Tags.Failure) failureCount += (long)value;
        });

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<ProbeCmd, string>>(new FailHandler());
        services.AddSingleton(metrics);
        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        Func<Task> act = () => dispatcher.SendAsync(new ProbeCmd("x"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        successCount.Should().Be(0);
        failureCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_MetricsNotRegistered_NoOpAndNoThrow()
    {
        // 未注册 TenE0Metrics（observability 关闭）→ GetService 返回 null → no-op。
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<ProbeCmd, string>>(new OkHandler());
        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        var result = await dispatcher.SendAsync(new ProbeCmd("noop"));

        result.Should().Be("noop");
    }

    [Fact]
    public void SetBacklog_FeedsObservableGauge()
    {
        // ObservableGauge 由读取方（Prometheus / MeterListener）拉取时回调；
        // 用 MeterListener 触发 RecordObservableInstruments 后断言 SetBacklog 写入的值被读出。
        var metrics = new TenE0Metrics();
        long observed = -1;
        using var listener = CreateListener(metrics, (name, _, value) =>
        {
            if (name == "tene0.outbox.backlog") observed = (long)value;
        });

        metrics.SetBacklog(123);
        listener.RecordObservableInstruments(); // 拉取当前所有 ObservableInstrument 快照

        observed.Should().Be(123);
    }

    [Fact]
    public void OutboxDelivered_Add_IncrementsCounterByResult()
    {
        // OutboxRelayService 投递成功/失败时调用此计数器。直接断言两种 result tag 互不串扰。
        var metrics = new TenE0Metrics();
        long success = 0, failure = 0;
        using var listener = CreateListener(metrics, (name, tags, value) =>
        {
            if (name != "tene0.outbox.delivered") return;
            var result = TagValue(tags, TenE0Metrics.Tags.Result);
            if (result == TenE0Metrics.Tags.Success) success += (long)value;
            if (result == TenE0Metrics.Tags.Failure) failure += (long)value;
        });

        metrics.OutboxDelivered.Add(1, [new(TenE0Metrics.Tags.Result, TenE0Metrics.Tags.Success)]);
        metrics.OutboxDelivered.Add(2, [new(TenE0Metrics.Tags.Result, TenE0Metrics.Tags.Success)]);
        metrics.OutboxDelivered.Add(1, [new(TenE0Metrics.Tags.Result, TenE0Metrics.Tags.Failure)]);

        success.Should().Be(3);
        failure.Should().Be(1);
    }

    /// <summary>从 ReadOnlySpan&lt;KeyValuePair&gt; 中按 key 取 value（span 无 LINQ 支持）。</summary>
    private static string? TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var t in tags)
            if (t.Key == key) return t.Value?.ToString();
        return null;
    }

    // 创建一个监听 TenE0 Meter 全部仪器的 MeterListener。
    private static MeterListener CreateListener(
        TenE0Metrics metrics,
        Action<string, ReadOnlySpan<KeyValuePair<string, object?>>, double> onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == TenE0Metrics.MeterName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
            onMeasurement(inst.Name, tags, value));
        listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
            onMeasurement(inst.Name, tags, value));
        listener.Start();
        return listener;
    }
}
