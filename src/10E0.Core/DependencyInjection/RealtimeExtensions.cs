using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TenE0.Core.Events;
using TenE0.Core.Realtime;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 实时推送模块的 DI / 端点注册扩展（#155）。
/// </summary>
public static class RealtimeExtensions
{
    /// <summary>
    /// 注册实时推送基础设施（SignalR Hub + notifier + 默认组/用户映射 + backplane + 声明式触发桥接器）。
    ///
    /// 一次性注册：
    /// - <see cref="NotificationHub"/> 的 <see cref="IHubContext{THub}"/>（SignalR 框架自注册）
    /// - <see cref="IRealtimeNotifier"/> → <see cref="HubBasedRealtimeNotifier"/>（Scoped）
    /// - <see cref="IRealtimeGroupProvider"/> → <see cref="ClaimBasedGroupProvider"/>（Singleton）
    /// - <see cref="IUserIdProvider"/> → <see cref="ClaimBasedUserIdProvider"/>（Singleton，让 Clients.User(code) 生效）
    /// - <see cref="NotificationDispatcher{TEvent}"/> 作为 <b>开放泛型</b> <see cref="IDomainEventHandler{TEvent}"/>
    ///   —— 任何实现 <see cref="INotifyClient"/> 的事件自动触发推送（声明式，业务方零样板）
    /// - <see cref="IRealtimeBackplane"/>：按 <see cref="RealtimeOptions.Backplane"/> 选实现（默认 Noop）
    /// - <see cref="RealtimeBackplaneSubscriber"/>：IHostedService 订阅远端副本广播
    ///
    /// 调用方还需 <see cref="MapTenE0Hub"/> 注册 Hub 端点，并在 JwtBearer 配置处接入 query-string 认证
    /// （见 <see cref="ConfigureRealtimeHubToken"/>)。
    /// </summary>
    public static IServiceCollection AddTenE0Realtime(
        this IServiceCollection services,
        Action<RealtimeOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<RealtimeOptions>();

        services.AddSignalR();

        services.TryAddScoped<IRealtimeNotifier, HubBasedRealtimeNotifier>();
        services.TryAddSingleton<IRealtimeGroupProvider, ClaimBasedGroupProvider>();
        // AddSignalR() 自带 DefaultUserIdProvider（按 ClaimTypes.NameIdentifier 解析），
        // 但本框架主体标识在 JWT sub claim —— 必须 Replace 成 ClaimBasedUserIdProvider。
        services.Replace(ServiceDescriptor.Singleton<IUserIdProvider, ClaimBasedUserIdProvider>());

        // 声明式触发核心：开放泛型 handler，DI 按 TEvent 自动构建。
        // 仅对实现 INotifyClient 的事件有意义 —— 其 Target 属性来自该接口；非 INotifyClient 事件解析到此
        // handler 会因无法满足类型约束而在运行时报错。业务项目若也手写注册了同事件的 handler，二者并存（fan-out）。
        services.TryAddScoped(typeof(IDomainEventHandler<>), typeof(NotificationDispatcher<>));

        // backplane（单体默认 Noop；多副本时业务方 Replace 为 Redis 实现并把 options.Backplane 设 Redis）
        services.AddRealtimeBackplane();

        services.AddHostedService<RealtimeBackplaneSubscriber>();

        return services;
    }

    private static IServiceCollection AddRealtimeBackplane(this IServiceCollection services)
    {
        services.TryAddSingleton<IRealtimeBackplane>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RealtimeOptions>>().Value;
            return options.Backplane switch
            {
                BackplaneMode.Redis => throw new InvalidOperationException(
                    "RealtimeOptions.Backplane=Redis 需提供 IRealtimeBackplane 的 Redis 实现（后续 issue）。"
                    + "当前先用 services.Replace(...) 注入你的实现，或保持 None。"),
                _ => new NoopRealtimeBackplane(),
            };
        });
        return services;
    }

    /// <summary>
    /// 注册 NotificationHub 端点（<c>{hubPath}/notification</c>，hubPath 默认 <c>/hub</c>）。
    ///
    /// 在 <c>app.UseEndpoints</c> / <c>app.MapXXX</c> 阶段调用：
    /// <code>app.MapTenE0Hub();</code>
    /// </summary>
    /// <param name="endpoints">来自 <see cref="WebApplication"/>（同时实现 <see cref="IEndpointRouteBuilder"/>）。</param>
    /// <param name="hubPath">
    /// Hub 路由前缀（默认 <see cref="RealtimeOptions.HubPath"/>）。端点为 <c>{hubPath}/notification</c>。
    /// 传 null 时读 <see cref="RealtimeOptions.HubPath"/>。
    /// </param>
    public static IEndpointRouteBuilder MapTenE0Hub(this IEndpointRouteBuilder endpoints, string? hubPath = null)
    {
        var prefix = hubPath
            ?? endpoints.ServiceProvider.GetRequiredService<IOptions<RealtimeOptions>>().Value.HubPath;
        endpoints.MapHub<NotificationHub>($"{prefix}/notification");
        return endpoints;
    }

    /// <summary>
    /// 接入 JwtBearer 的 <c>OnMessageReceived</c>，让 WebSocket 握手能从 query string 取 token（SignalR 标准模式）。
    ///
    /// SignalR / 浏览器无法在 WebSocket 握手时设置 Authorization 头，标准做法是把 token 放在
    /// query string <c>?access_token=</c>。本方法用 <see cref="OptionsBuilderPostConfigureOptions{TOptions}"/>
    /// 在首个 <c>JwtBearerOptions</c> 实例化时挂回调（PostConfigure 保证读到的 <see cref="RealtimeOptions.HubPath"/>
    /// 是配置阶段后的最终值，且不触发 root provider 过早构建）：
    /// 当请求路径以 <see cref="RealtimeOptions.HubPath"/> 开头时，从 query 取 token 填入 context。
    ///
    /// 用法（Program.cs，在 AddAuthentication/AddJwtBearer 之后）：
    /// <code>builder.Services.AddRealtimeHubTokenFromQuery();</code>
    /// </summary>
    public static IServiceCollection AddRealtimeHubTokenFromQuery(this IServiceCollection services)
    {
        // 必须指定方案名：JwtBearer 方案名为 "Bearer"（JwtBearerDefaults.AuthenticationScheme）。
        // 无参 PostConfigure<JwtBearerOptions> 注册的是 Options.DefaultName（""），命不中 JwtBearer 方案。
        const string Scheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
        services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(Scheme, options =>
        {
            options.Events ??= new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents();
            var previous = options.Events.OnMessageReceived;
            options.Events.OnMessageReceived = async ctx =>
            {
                await previous(ctx);
                var hubPath = ctx.HttpContext.RequestServices
                    .GetRequiredService<IOptions<RealtimeOptions>>().Value.HubPath;
                var path = ctx.HttpContext.Request.Path;
                if (path.StartsWithSegments(hubPath) &&
                    ctx.HttpContext.Request.Query.TryGetValue("access_token", out var token))
                {
                    ctx.Token = token!;
                }
            };
        });
        return services;
    }
}
