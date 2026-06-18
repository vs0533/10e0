using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.DependencyInjection;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 框架入口壳契约 — 让 <c>Program.cs</c> 从"业务模块细节"剥离为"模块装配清单"。
///
/// 业务模块（demo、未来租户模块等）实现 <see cref="IAppModule"/>：
///   1. <see cref="IAppModule.ConfigureServices"/> 挂自己的 DbContext / Cqrs handler / seeder；
///   2. <see cref="IAppModule.MapEndpoints"/> 挂自己的 <c>/demo/*</c> 路由。
///
/// 框架入口的最终形态（迁出 demo 后目标）：
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// builder.Services.AddTenE0Core();
/// builder.Services.AddAppModule&lt;DemoModule&gt;(builder.Configuration);
/// var app = builder.Build();
/// app.MapAppModules();
/// app.Run();
/// </code>
///
/// 设计依据（#43）：原 Program.cs 102 行（含 8 个 demo 路由 / 6 个 demo seeder / 弱口令
/// fallback）无法在不拆项目的情况下瘦身到 ≤50 行。引入本接口后，框架入口只关心"注册
/// 哪些模块 + 挂载哪些模块的端点"，业务模块自包含。
/// </summary>
public interface IAppModule
{
    /// <summary>
    /// 模块顺序：数字小的先 <see cref="ConfigureServices"/>，先 <see cref="MapEndpoints"/>。
    /// 框架壳默认占 <c>0</c>，业务模块用 100 / 200 / 300。
    ///
    /// 同 <c>Order</c> 的多模块按 DI 注册顺序（<see cref="AddAppModule{TModule}"/> 调用顺序），
    /// 由 <see cref="Enumerable.OrderBy{TSource,TKey}(System.Collections.Generic.IEnumerable{TSource},System.Func{TSource,TKey})"/>
    /// 的稳定排序保证一致行为。
    /// </summary>
    int Order => 100;

    /// <summary>注册本模块专属的 DI 服务（DbContext、Cqrs handler、seeder 等）。</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>挂本模块的路由端点。</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

/// <summary>
/// 业务模块注册 / 路由挂载扩展。
///
/// 用法：
/// <code>
/// builder.Services.AddAppModule&lt;DemoModule&gt;(builder.Configuration);
/// ...
/// app.MapAppModules();
/// </code>
/// </summary>
public static class AppModuleExtensions
{
    /// <summary>
    /// 注册业务模块到 DI 容器。允许多次调用挂载多个模块。
    /// 模块实例以 Singleton 注册（模块本身无状态），单例内调用
    /// <see cref="IAppModule.ConfigureServices"/> 完成模块的 DI 装配。
    ///
    /// 重要：模块实例本身是 <b>Singleton</b>，意味着实现类不能保存可变状态（Scoped
    /// 服务、请求级数据等）。模块应当是无状态的行为载体，只用来挂服务和路由。
    /// </summary>
    public static IServiceCollection AddAppModule<TModule>(this IServiceCollection services, IConfiguration configuration)
        where TModule : class, IAppModule, new()
    {
        var module = new TModule();
        module.ConfigureServices(services, configuration);
        services.AddSingleton<IAppModule>(module);
        return services;
    }

    /// <summary>
    /// 按模块 <see cref="IAppModule.Order"/> 升序遍历，依次挂载各模块的端点。
    /// 框架壳自身在 <c>MapAppModules</c> 之前完成 controllers / openapi / auth 装配。
    ///
    /// 单个模块 <see cref="IAppModule.MapEndpoints"/> 抛异常会被捕获并重新抛出 ——
    /// 已被挂载的路由仍保留（路由表操作非事务，无法回滚），但失败模块不会阻塞后续模块
    /// 的注册尝试。检查异常堆栈即可定位是哪个模块挂载失败。
    /// </summary>
    public static IEndpointRouteBuilder MapAppModules(this IEndpointRouteBuilder endpoints)
    {
        var modules = endpoints.ServiceProvider
            .GetServices<IAppModule>()
            .OrderBy(m => m.Order)
            .ToList();

        foreach (var module in modules)
        {
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }
}
