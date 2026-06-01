using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Organizations;

namespace TenE0.Core.DependencyInjection;

public static class OrganizationsExtensions
{
    /// <summary>
    /// 启用组织树服务。
    /// TContext 仅需是 DbContext —— TenE0Org 表由 TenE0SystemDbContext 自动注册。
    /// </summary>
    public static IServiceCollection AddTenE0Organizations<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<IOrgTreeService, OrgTreeService<TContext>>();
        return services;
    }
}
