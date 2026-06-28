using System.Text.Json;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Scheduling;

/// <summary>
/// 任务执行时的上下文（issue #164）。
///
/// <para>
/// 传递给 <see cref="IScheduledJob.ExecuteAsync"/>，让任务能拿到自身定义、参数、当前重试次数等。
/// </para>
/// </summary>
public sealed class JobContext
{
    /// <summary>构造执行上下文。</summary>
    /// <param name="job">触发的任务定义。</param>
    /// <param name="attempt">本次是第几次尝试（1 起）。</param>
    /// <param name="parameters">从 <see cref="Entities.TenE0ScheduledJob.ParametersJson"/> 解析出的 JSON 视图；为空则 <c>null</c>。</param>
    public JobContext(TenE0ScheduledJob job, int attempt, JsonElement? parameters)
    {
        Job = job ?? throw new ArgumentNullException(nameof(job));
        Attempt = attempt;
        Parameters = parameters;
    }

    /// <summary>触发的任务定义。</summary>
    public TenE0ScheduledJob Job { get; }

    /// <summary>本次是第几次尝试（1 起）。重试每次递增。</summary>
    public int Attempt { get; }

    /// <summary>
    /// 从 <see cref="Entities.TenE0ScheduledJob.ParametersJson"/> 解析出的通用 JSON 视图；
    /// 无参数任务为 <c>null</c>。任务用 <see cref="GetParameters{T}"/> 反序列化为强类型。
    /// </summary>
    public JsonElement? Parameters { get; }

    /// <summary>
    /// 把 <see cref="Parameters"/> 反序列化为强类型参数。<see cref="Parameters"/> 为空时返回 <c>default</c>。
    /// </summary>
    /// <typeparam name="T">参数类型（任务自定义，通常用 record）。</typeparam>
    /// <returns>反序列化后的参数；无参数时为 <c>default</c>。</returns>
    public T? GetParameters<T>() => Parameters is { } el
        ? el.Deserialize<T>(SchedulingJsonDefaults.Options)
        : default;
}

/// <summary>任务参数反序列化用的共享 JsonSerializerOptions（驼峰、不区分大小写）。</summary>
internal static class SchedulingJsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// 定时任务契约（issue #164）。
///
/// <para>
/// 实现本接口 + 标 <c>[Scheduled]</c> attribute 即成为静态任务；
/// 动态任务只需实现本接口（<c>JobType</c> 反射加载时校验本接口）。
/// </para>
///
/// <para>
/// 实现注册到 DI（<c>Scoped</c>），执行时由 <see cref="JobExecutor{TContext}"/>
/// 从 DI 解析实例，可注入任何 Scoped 服务（如 <c>IDbContextFactory</c>、命令分发器）。
/// </para>
/// </summary>
public interface IScheduledJob
{
    /// <summary>
    /// 执行任务。
    /// </summary>
    /// <param name="context">执行上下文（含任务定义、参数、重试次数）。</param>
    /// <param name="cancellationToken">取消令牌；超时 / 应用关闭时会取消。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 抛异常即视为本次执行失败（按 <c>MaxRetries</c> 重试）；正常返回视为成功。
    /// 实现应响应 <paramref name="cancellationToken"/> 以支持超时取消。
    /// </remarks>
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}
