namespace TenE0.Core.Scheduling;

/// <summary>
/// 定时任务的注册方式（issue #164）。
///
/// <para>
/// 取值约定：枚举 int 值仅追加，禁止重排已有值（向后兼容老配置）。
/// </para>
/// <list type="bullet">
/// <item><see cref="Static"/>：编译期固定，由 <c>[Scheduled]</c> attribute 标记，
/// 启动期 <see cref="StaticJobRegistrar"/> 扫描注册并幂等 upsert 到
/// <c>TenE0ScheduledJob</c> 表。Code 不可改、不可删（删除后下次启动会重新插入）。</item>
/// <item><see cref="Dynamic"/>：运行时通过 Admin API（<c>POST /admin/scheduler/jobs</c>）增删改，
/// 持久化在 <c>TenE0ScheduledJob</c> 表，不需重启即可生效。</item>
/// </list>
/// </summary>
public enum JobExecutionMode
{
    /// <summary>静态任务：[Scheduled] attribute 标记，启动期注册。</summary>
    Static = 0,

    /// <summary>动态任务：Admin API 运行时增删改。</summary>
    Dynamic = 1,
}
