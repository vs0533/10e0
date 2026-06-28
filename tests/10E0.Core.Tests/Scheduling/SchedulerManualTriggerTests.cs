using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Scheduling;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Tests.Scheduling;

[Trait("Category", "Unit")]
public sealed class SchedulerManualTriggerTests
{
    private static Scheduler<SchedulingTestInfrastructure.TestDbContext> CreateScheduler(
        IDbContextFactory<SchedulingTestInfrastructure.TestDbContext> factory,
        FakeTimeProvider? tp = null,
        IOptions<SchedulingOptions>? options = null)
    {
        var timeProvider = tp ?? new FakeTimeProvider();
        var opt = options ?? SchedulingTestInfrastructure.DefaultOptions();
        return new Scheduler<SchedulingTestInfrastructure.TestDbContext>(
            factory, new NoOpJobLock(), opt, timeProvider);
    }

    [Fact]
    public async Task TriggerJob_ExistingJob_SetsNextRunToNow()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var tp = new FakeTimeProvider(new DateTimeOffset(2024, 6, 15, 8, 0, 0, TimeSpan.Zero));
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "manual",
                Name = "Manual",
                CronExpression = "0 0 9 * * ?",
                JobType = typeof(SuccessJob).AssemblyQualifiedName!,
                IsEnabled = true,
                NextRunAt = new DateTimeOffset(2024, 6, 16, 9, 0, 0, TimeSpan.Zero), // 明天
            });
            await ctx.SaveChangesAsync();
        }
        var scheduler = CreateScheduler(factory, tp);

        string jobId;
        await using (var ctx0 = factory.CreateDbContext())
        {
            jobId = ctx0.ScheduledJobs.Single().Id;
        }

        var ok = await scheduler.TriggerJobAsync(jobId, default);

        // NoOpJobLock.IsRunning 恒 false，所以总能触发。
        ok.Should().BeTrue();
        await using var verify = factory.CreateDbContext();
        var job = verify.ScheduledJobs.Single();
        job.NextRunAt.Should().Be(tp.GetUtcNow());
    }

    [Fact]
    public async Task TriggerJob_NonExistent_ReturnsFalse()
    {
        var scheduler = CreateScheduler(SchedulingTestInfrastructure.CreateFactory(Guid.NewGuid().ToString("N")));
        var ok = await scheduler.TriggerJobAsync("no-such-id", default);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task DisableThenEnable_RoundsTrip()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "toggle",
                Name = "Toggle",
                CronExpression = "0 0 9 * * ?",
                JobType = typeof(SuccessJob).AssemblyQualifiedName!,
                IsEnabled = true,
            });
            await ctx.SaveChangesAsync();
        }
        var scheduler = CreateScheduler(factory);

        string id;
        await using (var ctx0 = factory.CreateDbContext())
        {
            id = ctx0.ScheduledJobs.Single().Id;
        }
        (await scheduler.DisableJobAsync(id, default)).Should().BeTrue();
        await using (var v1 = factory.CreateDbContext())
        {
            v1.ScheduledJobs.Single().IsEnabled.Should().BeFalse();
        }
        (await scheduler.EnableJobAsync(id, default)).Should().BeTrue();
        await using var v2 = factory.CreateDbContext();
        v2.ScheduledJobs.Single().IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task CreateJob_InvalidCron_Throws()
    {
        var scheduler = CreateScheduler(SchedulingTestInfrastructure.CreateFactory(Guid.NewGuid().ToString("N")));
        var act = () => scheduler.CreateJobAsync(
            "c", "n", "garbage", typeof(SuccessJob).AssemblyQualifiedName!,
            null, 3, TimeSpan.FromMinutes(1), default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateJob_DuplicateCode_Throws()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "dup",
                Name = "Dup",
                CronExpression = "0 0 9 * * ?",
                JobType = typeof(SuccessJob).AssemblyQualifiedName!,
                IsEnabled = true,
            });
            await ctx.SaveChangesAsync();
        }
        var scheduler = CreateScheduler(factory, options: SchedulingTestInfrastructure.DefaultOptions(o =>
            o.AllowedAssemblies = [typeof(SuccessJob).Assembly]));

        var act = () => scheduler.CreateJobAsync(
            "dup", "Dup2", "0 0 9 * * ?", typeof(SuccessJob).AssemblyQualifiedName!,
            null, 3, TimeSpan.FromMinutes(1), default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已存在*");
    }

    [Fact]
    public async Task CreateJob_JobTypeNotWhitelisted_Throws()
    {
        var dbName = Guid.NewGuid().ToString("N");
        // 白名单只含 System.Private.CoreLib（typeof(object).Assembly），
        // SuccessJob 在测试程序集 → 不在白名单 → 应拒绝。
        var scheduler = CreateScheduler(SchedulingTestInfrastructure.CreateFactory(dbName),
            options: SchedulingTestInfrastructure.DefaultOptions(o =>
                o.AllowedAssemblies = [typeof(object).Assembly]));

        var act = () => scheduler.CreateJobAsync(
            "c", "n", "0 0 9 * * ?", typeof(SuccessJob).AssemblyQualifiedName!,
            null, 3, TimeSpan.FromMinutes(1), default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*白名单*");
    }

    [Fact]
    public async Task UpdateJob_StaticJob_Throws()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var id = Guid.NewGuid().ToString("N");
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Id = id,
                Code = "static",
                Name = "Static",
                CronExpression = "0 0 9 * * ?",
                JobType = typeof(SuccessJob).AssemblyQualifiedName!,
                IsEnabled = true,
                Mode = JobExecutionMode.Static,
            });
            await ctx.SaveChangesAsync();
        }
        var scheduler = CreateScheduler(factory);

        var act = () => scheduler.UpdateJobAsync(
            id, "NewName", null, null, null, null, null, default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*静态任务*");
    }

    [Fact]
    public async Task GetExecutions_ReturnsHistoryInDescOrder()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var jobId = Guid.NewGuid().ToString("N");
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.JobExecutions.AddRange(
                new TenE0JobExecution { JobId = jobId, StartedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), Status = "Success", Attempt = 1 },
                new TenE0JobExecution { JobId = jobId, StartedAt = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), Status = "Failed", Attempt = 2 },
                new TenE0JobExecution { JobId = jobId, StartedAt = new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.Zero), Status = "Success", Attempt = 1 });
            await ctx.SaveChangesAsync();
        }
        var scheduler = CreateScheduler(factory);

        var execs = await scheduler.GetExecutionsAsync(jobId, 10, default);

        execs.Should().HaveCount(3);
        // 倒序（最新在前）。
        execs[0].StartedAt.Should().Be(new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.Zero));
        execs[2].StartedAt.Should().Be(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }
}
