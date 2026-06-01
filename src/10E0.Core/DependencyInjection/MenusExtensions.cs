using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Menus;

namespace TenE0.Core.DependencyInjection;

public static class MenusExtensions
{
    /// <summary>
    /// 启用菜单服务。
    /// TContext 仅需是 DbContext —— 菜单表由 TenE0SystemDbContext 自动注册。
    /// </summary>
    public static IServiceCollection AddTenE0Menus<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<IMenuService, MenuService<TContext>>();
        return services;
    }
}
