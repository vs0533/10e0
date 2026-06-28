using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Scheduling;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Tests.Scheduling;

/// <summary>
/// Scheduling 模块测试的共享基础设施（仿 OutboxRelayServiceTests）。
/// </summary>
internal static class SchedulingTestInfrastructure
{
    public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0ScheduledJob> ScheduledJobs => Set<TenE0ScheduledJob>();
        public DbSet<TenE0JobExecution> JobExecutions => Set<TenE0JobExecution>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureTenE0SchedulingTables();
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    public sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options)
        : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    public static IDbContextFactory<TestDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    /// <summary>构造一个最小可用的 IOptions&lt;SchedulingOptions&gt;（测试默认值）。</summary>
    public static IOptions<SchedulingOptions> DefaultOptions(Action<SchedulingOptions>? configure = null)
    {
        var opt = new SchedulingOptions
        {
            ScanInterval = TimeSpan.FromMilliseconds(100),
            JobTimeout = TimeSpan.FromSeconds(5),
        };
        configure?.Invoke(opt);
        return Options.Create(opt);
    }

    /// <summary>构造一个能解析 IScheduledJob 具体类型的 ServiceProvider。</summary>
    public static ServiceProvider BuildServiceProvider(
        IDbContextFactory<TestDbContext> factory,
        IScheduledJob job,
        IJobLock? jobLock = null,
        IOptions<SchedulingOptions>? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(); // JobExecutor / SchedulerWorker 需要 ILogger<>
        services.AddSingleton(factory);
        services.AddSingleton(job.GetType(), job);
        services.AddSingleton<IScheduledJob>(sp => job); // 兜底（部分测试用基类型解析）
        services.AddSingleton(jobLock ?? new NoOpJobLock());
        services.AddSingleton(options ?? DefaultOptions());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<JobExecutor<TestDbContext>>();
        return services.BuildServiceProvider();
    }
}

/// <summary>测试用的成功任务（执行即返回）。</summary>
internal sealed class SuccessJob : IScheduledJob
{
    public int Calls;
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.CompletedTask;
    }
}

/// <summary>测试用的失败任务（前 N 次抛异常）。</summary>
internal sealed class FlakeyJob(int failUntilAttempt) : IScheduledJob
{
    public int Calls;
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref Calls);
        if (n <= failUntilAttempt)
        {
            throw new InvalidOperationException($"boom #{n}");
        }
        return Task.CompletedTask;
    }
}

/// <summary>测试用的常失败任务（永远抛异常）。</summary>
internal sealed class AlwaysFailJob : IScheduledJob
{
    public int Calls;
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        throw new InvalidOperationException("always boom");
    }
}

/// <summary>测试用的慢任务（sleep 指定时长，用于超时测试）。</summary>
internal sealed class SlowJob(TimeSpan delay) : IScheduledJob
{
    public int Calls;
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        await Task.Delay(delay, cancellationToken);
    }
}
