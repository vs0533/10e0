using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Scheduling;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Tests.Scheduling;

[Trait("Category", "Unit")]
public sealed class JobExecutorTests
{
    private static TenE0ScheduledJob MakeJob(string jobType, int maxRetries = 3, TimeSpan? retryInterval = null)
        => new()
        {
            Code = "exec-job",
            Name = "Exec",
            CronExpression = "0 * * * * ?",
            JobType = jobType,
            IsEnabled = true,
            MaxRetries = maxRetries,
            RetryInterval = retryInterval ?? TimeSpan.FromMilliseconds(10),
        };

    [Fact]
    public async Task ExecuteAsync_Success_RecordsSuccessHistoryAndUpdatesNextRun()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        // 先把任务行存入数据库（executor 用 job.Id 回写 LastRunAt/NextRunAt）。
        var job = MakeJob(typeof(SuccessJob).AssemblyQualifiedName!);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        var successJob = new SuccessJob();
        var sp = SchedulingTestInfrastructure.BuildServiceProvider(factory, successJob);
        var executor = sp.GetRequiredService<JobExecutor<SchedulingTestInfrastructure.TestDbContext>>();

        var status = await executor.ExecuteAsync(job, "inst-1", default);

        status.Should().Be(JobExecutionStatus.Success);
        successJob.Calls.Should().Be(1);

        await using var verify = factory.CreateDbContext();
        // 历史：1 行 Success，attempt 1。
        var execs = verify.JobExecutions.Where(e => e.JobId == job.Id).ToList();
        execs.Should().HaveCount(1);
        execs[0].Status.Should().Be(nameof(JobExecutionStatus.Success));
        execs[0].Attempt.Should().Be(1);
        execs[0].ErrorMessage.Should().BeNull();
        execs[0].FinishedAt.Should().NotBeNull();

        // 任务定义：LastRunStatus=Success，NextRunAt 被重算（不再是 null）。
        var updated = verify.ScheduledJobs.Single();
        updated.LastRunStatus.Should().Be(nameof(JobExecutionStatus.Success));
        updated.LastRunAt.Should().NotBeNull();
        updated.NextRunAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_AlwaysFail_RetriesMaxTimesThenFailed()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var job = MakeJob(typeof(AlwaysFailJob).AssemblyQualifiedName!, maxRetries: 3);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        var failJob = new AlwaysFailJob();
        var sp = SchedulingTestInfrastructure.BuildServiceProvider(factory, failJob);
        var executor = sp.GetRequiredService<JobExecutor<SchedulingTestInfrastructure.TestDbContext>>();

        var status = await executor.ExecuteAsync(job, "inst-1", default);

        status.Should().Be(JobExecutionStatus.Failed);
        failJob.Calls.Should().Be(3); // 重试到 MaxRetries=3
        await using var verify = factory.CreateDbContext();
        var exec = verify.JobExecutions.Single(e => e.JobId == job.Id);
        exec.Status.Should().Be(nameof(JobExecutionStatus.Failed));
        exec.Attempt.Should().Be(3);
        exec.ErrorMessage.Should().Contain("always boom");
        verify.ScheduledJobs.Single().LastRunStatus.Should().Be(nameof(JobExecutionStatus.Failed));
    }

    [Fact]
    public async Task ExecuteAsync_FailsThenSucceeds_ReturnsSuccess()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var job = MakeJob(typeof(FlakeyJob).AssemblyQualifiedName!, maxRetries: 5);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        var flakey = new FlakeyJob(failUntilAttempt: 2);
        var sp = SchedulingTestInfrastructure.BuildServiceProvider(factory, flakey);
        var executor = sp.GetRequiredService<JobExecutor<SchedulingTestInfrastructure.TestDbContext>>();

        var status = await executor.ExecuteAsync(job, "inst-1", default);

        status.Should().Be(JobExecutionStatus.Success);
        flakey.Calls.Should().Be(3); // 失败 2 次，第 3 次成功
        await using var verify = factory.CreateDbContext();
        var exec = verify.JobExecutions.Single(e => e.JobId == job.Id);
        exec.Status.Should().Be(nameof(JobExecutionStatus.Success));
        exec.Attempt.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_SlowJobOverTimeout_MarksTimeoutAndStopsRetrying()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var job = MakeJob(typeof(SlowJob).AssemblyQualifiedName!, maxRetries: 3);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        var slow = new SlowJob(TimeSpan.FromSeconds(30));
        var options = SchedulingTestInfrastructure.DefaultOptions(o => o.JobTimeout = TimeSpan.FromMilliseconds(100));
        var sp = SchedulingTestInfrastructure.BuildServiceProvider(factory, slow, options: options);
        var executor = sp.GetRequiredService<JobExecutor<SchedulingTestInfrastructure.TestDbContext>>();

        var status = await executor.ExecuteAsync(job, "inst-1", default);

        status.Should().Be(JobExecutionStatus.Timeout);
        await using var verify = factory.CreateDbContext();
        var exec = verify.JobExecutions.Single(e => e.JobId == job.Id);
        exec.Status.Should().Be(nameof(JobExecutionStatus.Timeout));
        exec.ErrorMessage.Should().Contain("超时");
    }

    [Fact]
    public async Task ExecuteAsync_UnresolvableJobType_MarksFailedWithoutRetrying()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var job = MakeJob("Nonexistent.Type, NonexistentAssembly");
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        var success = new SuccessJob(); // 实际不会被调
        var sp = SchedulingTestInfrastructure.BuildServiceProvider(factory, success);
        var executor = sp.GetRequiredService<JobExecutor<SchedulingTestInfrastructure.TestDbContext>>();

        var status = await executor.ExecuteAsync(job, "inst-1", default);

        status.Should().Be(JobExecutionStatus.Failed);
        success.Calls.Should().Be(0); // handler 解析失败 → 未执行
        await using var verify = factory.CreateDbContext();
        verify.ScheduledJobs.Single().LastRunStatus.Should().Be(nameof(JobExecutionStatus.Failed));
    }
}
