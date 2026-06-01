using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TenE0.Core.Hosting;

/// <summary>
/// 数据库初始化的种子数据填充接口。
/// 实现方在 Add10E0Core 后注册具体实现，DatabaseInitializerService 启动时统一调用。
/// </summary>
public interface IDataSeeder
{
    /// <summary>种子数据填充。可幂等（多次调用安全）。</summary>
    Task SeedAsync(DbContext context, CancellationToken cancellationToken);

    /// <summary>执行顺序，数字小的先执行。</summary>
    int Order => 0;
}

/// <summary>
/// 数据库初始化服务。
///
/// 替代旧 UseE0Context() 里的 `dc.DataInit()` 调用（旧实现没有 await，Task 被丢弃，
/// 导致请求可能在数据库未就绪时进入）。
///
/// 使用 IHostedLifecycleService.StartingAsync 保证在 Kestrel 开始监听之前完成初始化。
///
/// 注意：本服务自身是 Singleton（IHostedService 约定），通过 IServiceScopeFactory
/// 创建作用域来解析 Scoped 的 IDbContextFactory 和 IDataSeeder，避免生命周期冲突。
/// </summary>
public sealed class DatabaseInitializerService<TContext>(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializerService<TContext>> logger)
    : IHostedLifecycleService where TContext : DbContext
{
    /// <summary>
    /// 在应用开始接收请求之前执行。
    /// </summary>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("数据库初始化开始：{ContextType}", typeof(TContext).Name);

        // 自建作用域以解析 Scoped 服务（IDbContextFactory、IDataSeeder 等）
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var contextFactory = sp.GetRequiredService<IDbContextFactory<TContext>>();
        var seeders = sp.GetServices<IDataSeeder>();

        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);

        // 确保架构已创建（开发环境）。生产建议改 Migrate。
        await ctx.Database.EnsureCreatedAsync(cancellationToken);

        foreach (var seeder in seeders.OrderBy(s => s.Order))
        {
            logger.LogInformation("执行 Seeder：{SeederType} (Order={Order})", seeder.GetType().Name, seeder.Order);
            await seeder.SeedAsync(ctx, cancellationToken);
        }

        await ctx.SaveChangesAsync(cancellationToken);

        logger.LogInformation("数据库初始化完成：{ContextType}", typeof(TContext).Name);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
