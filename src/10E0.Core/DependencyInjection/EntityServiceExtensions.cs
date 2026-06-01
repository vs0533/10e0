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
    /// 注册 IEntityService。
    /// 必须在 <c>AddTenE0Core()</c> 之后调用（依赖 IErrs）。
    /// </summary>
    public static IServiceCollection AddTenE0EntityService(this IServiceCollection services)
    {
        services.TryAddScoped<IEntityService, EntityService.EntityService>();
        return services;
    }
}
