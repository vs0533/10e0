using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Auditing;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 审计日志模块 DI 扩展（issue #152）。
///
/// <para>
/// 一次性注册以下组件（调用方一次 <see cref="AddTenE0Auditing{TContext}"/> 即可获得完整审计基础设施）：
/// <list type="bullet">
/// <item><see cref="AuditLogChannel"/>（Singleton）：进程级 Channel，Sink 入队 / Worker 出队共享。</item>
/// <item><see cref="IAuditFieldFilter"/>（Singleton）：默认 <see cref="DefaultAuditFieldFilter"/>，业务方可 Replace。</item>
/// <item><see cref="AuditLogInterceptor"/>（Singleton）：业务 SaveChanges 拦截 → 字段级 diff → Sink。
///   注册为 Singleton 是 #95 captive-dependency 约束的必然结果（IDbContextFactory 是 Singleton，
///   optionsAction 在 root scope 解析拦截器）；拦截器内部通过 IHttpContextAccessor 按需解析 Scoped Sink。</item>
/// <item><see cref="IAuditLogSink"/>（Scoped）：默认 <see cref="AuditLogSink"/>，持有 Singleton Channel。
///   auth command handlers 通过构造函数注入此 Scoped 服务。</item>
/// <item><see cref="IAuditLogStore"/>（Scoped）：默认 <see cref="AuditLogStore{TContext}"/>，Admin 查询用。</item>
/// <item><see cref="AuditLogRelayWorker{TContext}"/>（Hosted）：后台批量落库 + 优雅停机 drain。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>挂载拦截器到 DbContext：</b><see cref="AuditLogInterceptor"/> 不在此处直接挂到 DbContextOptions，
/// 而是由 <c>ServiceCollectionExtensions.AddTenE0DataContext</c> 通过 <c>GetService&lt;AuditLogInterceptor&gt;()</c>
/// 可选注入（对齐 OutboxInterceptor 模式）。因此调用方必须<b>先</b>调 <c>AddTenE0Auditing</c>
/// 再调 <c>AddTenE0DataContext</c>，或至少在 DbContextFactory 构建 options 前 —— 否则拦截器取不到。
/// （Demo 项目的 Program.cs 调用顺序已正确。）
/// </para>
/// </summary>
public static class AuditingExtensions
{
    public static IServiceCollection AddTenE0Auditing<TContext>(
        this IServiceCollection services,
        Action<AuditOptions>? configure = null)
        where TContext : DbContext
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<AuditOptions>();

        // 进程级 Channel：Sink（Scoped，每请求）与 Worker（Hosted，单例后台）共享同一实例。
        services.TryAddSingleton<AuditLogChannel>();

        // 默认脱敏规则：业务方 services.Replace(...) 覆盖
        services.TryAddSingleton<IAuditFieldFilter, DefaultAuditFieldFilter>();

        // 拦截器 Singleton（#95 captive-dependency：optionsAction 在 root scope 解析此实例）
        services.TryAddSingleton<AuditLogInterceptor>();

        // Sink Scoped：auth handlers 按 scope 注入；持有 Singleton Channel。
        // 用 Replace 覆盖 AddTenE0Core 注册的 NullAuditLogSink 默认值（TryAdd 会被 Null 占位挡住）。
        services.Replace(ServiceDescriptor.Scoped<IAuditLogSink, AuditLogSink>());

        // 查询服务 Scoped：Admin 端点按 scope 注入
        services.TryAddScoped<IAuditLogStore, AuditLogStore<TContext>>();

        // 后台落库 Worker
        services.AddHostedService<AuditLogRelayWorker<TContext>>();

        return services;
    }
}
