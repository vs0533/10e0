using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Sequences;

namespace TenE0.Core.DependencyInjection;

public static class SequencesExtensions
{
    /// <summary>
    /// 启用流水号生成器。
    /// TContext 仅需是 DbContext —— TenE0Sequence 表由 TenE0SystemDbContext 自动注册。
    /// </summary>
    public static IServiceCollection AddTenE0Sequences<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<ISequenceGenerator, EfSequenceGenerator<TContext>>();
        return services;
    }
}
