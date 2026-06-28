using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.EntityService;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// EntityService 的 DI 注册扩展。
/// </summary>
public static class EntityServiceExtensions
{
    /// <summary>
    /// 注册 IEntityService（写侧）+ IEntityQueryService（读侧）。
    /// 必须在 <c>AddTenE0Core()</c> 之后调用（依赖 IErrs）。
    ///
    /// IEntityQueryService 是 IEntityService 的读侧对称 —— 随基础套件启用（无 opt-in 开关），
    /// 让 CQRS 读路径有官方推荐入口，自动复用 EF Named Query Filter（软删除/行级权限/租户）。
    /// </summary>
    public static IServiceCollection AddTenE0EntityService(this IServiceCollection services)
    {
        services.TryAddScoped<IEntityService, EntityService.EntityService>();
        services.TryAddScoped<IEntityQueryService, EntityQueryService>();
        return services;
    }
}
