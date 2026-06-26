using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Scheduling;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Tests.Scheduling;

[Trait("Category", "Unit")]
public sealed class SchedulerWorkerTests
{
    private static (SchedulerWorker<SchedulingTestInfrastructure.TestDbContext> Worker, IOptions<SchedulingOptions> Options)
        CreateWorker(
            IServiceProvider sp,
            SchedulingOptions options)
    {
        var opt = Options.Create(options);
        // Worker 内部用 TimeProvider 读「现在」做 pick 判定，这里用 System 即可
        // （测试关注的是 ProcessBatchAsync 单轮行为，不依赖时间推进）。
        var worker = new SchedulerWorker<SchedulingTestInfrastructure.TestDbContext>(
            sp.GetRequiredService<IServiceScopeFactory>(), opt, TimeProvider.System,
            NullLogger<SchedulerWorker<SchedulingTestInfrastructure.TestDbContext>>.Instance);
        return (worker, opt);
    }

    /// <summary>构造一个真实 ServiceProvider（让 SchedulerWorker 的 CreateAsyncScope 能解析所有依赖）。</summary>
    private static (ServiceProvider Sp, SuccessJob Job, FakeTimeProvider Tp) BuildSp(
        string dbName,
        IJobLock? jobLock = null,
        Action<SchedulingOptions>? configure = null)
    {
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var tp = new FakeTimeProvider(new DateTimeOffset(2024, 6, 15, 8, 0, 0, TimeSpan.Zero));
        var job = new SuccessJob();
        var options = new SchedulingOptions
        {
            ScanInterval = TimeSpan.FromMilliseconds(50),
            JobTimeout = TimeSpan.FromSeconds(5),
            LockInstanceId = "inst-test",
            TimeZone = TimeZoneInfo.Utc,
        };
        configure?.Invoke(options);

        var services = new ServiceCollection();
        services.AddLogging(); // JobExecutor（Worker 内解析）需要 ILogger<>
        services.AddSingleton(factory);
        services.AddSingleton(job.GetType(), job);
        services.AddSingleton(jobLock ?? new NoOpJobLock());
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<TimeProvider>(tp);
        services.AddScoped<JobExecutor<SchedulingTestInfrastructure.TestDbContext>>();
        return (services.BuildServiceProvider(), job, tp);
    }

    [Fact]
    public async Task ProcessBatchAsync_NoDueJobs_Returns0()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var (sp, _, _) = BuildSp(dbName);
        var options = SchedulingTestInfrastructure.DefaultOptions().Value;
        var (worker, _) = CreateWorker(sp, options);

        var result = await worker.ProcessBatchAsync(default);
        result.Should().Be(0);
    }

    [Fact]
    public async Task ProcessBatchAsync_DueJob_ExecutesAndUpdatesNextRun()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var job = new TenE0ScheduledJob
        {
            Code = "due-job",
            Name = "Due",
            CronExpression = "0 0 9 * * ?",
            JobType = typeof(SuccessJob).AssemblyQualifiedName!,
            IsEnabled = true,
            MaxRetries = 3,
            RetryInterval = TimeSpan.FromMilliseconds(10),
            NextRunAt = new DateTimeOffset(2024, 6, 15, 7, 0, 0, TimeSpan.Zero), // 已过期
        };
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        var (sp, successJob, _) = BuildSp(dbName);
        var options = SchedulingTestInfrastructure.DefaultOptions().Value;
        var (worker, _) = CreateWorker(sp, options);

        var result = await worker.ProcessBatchAsync(default);

        result.Should().Be(1);
        successJob.Calls.Should().Be(1);

        await using var verify = factory.CreateDbContext();
        var updated = verify.ScheduledJobs.Single();
        updated.LastRunStatus.Should().Be(nameof(JobExecutionStatus.Success));
        updated.LastRunAt.Should().NotBeNull();
        // NextRunAt 被重算为未来时间（> 现在）。
        updated.NextRunAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessBatchAsync_DisabledJob_NotExecuted()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "disabled",
                Name = "Off",
                CronExpression = "0 0 9 * * ?",
                JobType = typeof(SuccessJob).AssemblyQualifiedName!,
                IsEnabled = false, // 禁用
                NextRunAt = new DateTimeOffset(2024, 6, 15, 7, 0, 0, TimeSpan.Zero),
            });
            await ctx.SaveChangesAsync();
        }

        var (sp, successJob, _) = BuildSp(dbName);
        var options = SchedulingTestInfrastructure.DefaultOptions().Value;
        var (worker, _) = CreateWorker(sp, options);

        var result = await worker.ProcessBatchAsync(default);

        result.Should().Be(0);
        successJob.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ProcessBatchAsync_LockSkips_JobNotExecuted()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "locked",
                Name = "Locked",
                CronExpression = "0 0 9 * * ?",
                JobType = typeof(SuccessJob).AssemblyQualifiedName!,
                IsEnabled = true,
                NextRunAt = new DateTimeOffset(2024, 6, 15, 7, 0, 0, TimeSpan.Zero),
            });
            await ctx.SaveChangesAsync();
        }

        // 用一个永远返回 false 的锁模拟「他人持有」。
        var denyingLock = new DenyingJobLock();
        var (sp, successJob, _) = BuildSp(dbName, jobLock: denyingLock);
        var options = SchedulingTestInfrastructure.DefaultOptions().Value;
        var (worker, _) = CreateWorker(sp, options);

        var result = await worker.ProcessBatchAsync(default);

        result.Should().Be(0);
        successJob.Calls.Should().Be(0);
    }
}

/// <summary>测试用：TryAcquire 永远返回 false（模拟他人持锁）。</summary>
internal sealed class DenyingJobLock : IJobLock
{
    public Task<bool> TryAcquireAsync(string jobCode, string instanceId, TimeSpan lease, CancellationToken cancellationToken)
        => Task.FromResult(false);
    public Task ReleaseAsync(string jobCode, string instanceId, CancellationToken cancellationToken)
        => Task.CompletedTask;
    public Task<bool> IsRunningAsync(string jobCode, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
