using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Scheduling;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Tests.Scheduling;

[Trait("Category", "Unit")]
public sealed class RowJobLockTests
{
    private static async Task<RowJobLock<SchedulingTestInfrastructure.TestDbContext>> CreateLockAsync(
        string dbName, FakeTimeProvider? tp = null)
    {
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var now = new DateTimeOffset(2024, 6, 15, 8, 0, 0, TimeSpan.Zero);
        var timeProvider = tp ?? new FakeTimeProvider(now);
        // 种一个任务行供锁操作。
        await using var ctx = factory.CreateDbContext();
        ctx.ScheduledJobs.Add(new TenE0ScheduledJob
        {
            Code = "test-job",
            Name = "Test",
            CronExpression = "0 0 9 * * ?",
            JobType = "Some.Type",
            IsEnabled = true,
        });
        await ctx.SaveChangesAsync();
        return new RowJobLock<SchedulingTestInfrastructure.TestDbContext>(factory, timeProvider);
    }

    [Fact]
    public async Task TryAcquire_NoExistingLock_ReturnsTrueAndSetsOwner()
    {
        var tp = new FakeTimeProvider();
        var factory = SchedulingTestInfrastructure.CreateFactory(Guid.NewGuid().ToString("N"));
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob { Code = "j", Name = "n", CronExpression = "0 0 9 * * ?", JobType = "t" });
            await ctx.SaveChangesAsync();
        }
        var lockSvc = new RowJobLock<SchedulingTestInfrastructure.TestDbContext>(factory, tp);

        var acquired = await lockSvc.TryAcquireAsync("j", "inst-A", TimeSpan.FromMinutes(5), default);

        acquired.Should().BeTrue();
        await using var verify = factory.CreateDbContext();
        var job = verify.ScheduledJobs.Single();
        job.LockedByInstance.Should().Be("inst-A");
        job.LockedUntil.Should().Be(tp.GetUtcNow().AddMinutes(5));
    }

    [Fact]
    public async Task TryAcquire_OtherInstanceHoldingValidLease_ReturnsFalse()
    {
        var factory = SchedulingTestInfrastructure.CreateFactory(Guid.NewGuid().ToString("N"));
        var tp = new FakeTimeProvider();
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "j",
                Name = "n",
                CronExpression = "0 0 9 * * ?",
                JobType = "t",
                LockedByInstance = "inst-A",
                LockedUntil = tp.GetUtcNow().AddMinutes(5),
            });
            await ctx.SaveChangesAsync();
        }
        var lockSvc = new RowJobLock<SchedulingTestInfrastructure.TestDbContext>(factory, tp);

        var acquired = await lockSvc.TryAcquireAsync("j", "inst-B", TimeSpan.FromMinutes(5), default);

        acquired.Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquire_LeaseExpired_AnotherInstanceCanReacquire()
    {
        var tp = new FakeTimeProvider();
        var factory = SchedulingTestInfrastructure.CreateFactory(Guid.NewGuid().ToString("N"));
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "j",
                Name = "n",
                CronExpression = "0 0 9 * * ?",
                JobType = "t",
                LockedByInstance = "inst-A",
                LockedUntil = tp.GetUtcNow().AddMinutes(5),
            });
            await ctx.SaveChangesAsync();
        }
        var lockSvc = new RowJobLock<SchedulingTestInfrastructure.TestDbContext>(factory, tp);

        // 推进时间使租约过期（+10 分钟 > 5 分钟租约）。
        tp.Advance(TimeSpan.FromMinutes(10));
        var acquired = await lockSvc.TryAcquireAsync("j", "inst-B", TimeSpan.FromMinutes(5), default);

        acquired.Should().BeTrue();
        await using var verify = factory.CreateDbContext();
        verify.ScheduledJobs.Single().LockedByInstance.Should().Be("inst-B");
    }

    [Fact]
    public async Task TryAcquire_SameInstanceReacquire_Succeeds()
    {
        var tp = new FakeTimeProvider();
        var factory = SchedulingTestInfrastructure.CreateFactory(Guid.NewGuid().ToString("N"));
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "j",
                Name = "n",
                CronExpression = "0 0 9 * * ?",
                JobType = "t",
                LockedByInstance = "inst-A",
                LockedUntil = tp.GetUtcNow().AddMinutes(5),
            });
            await ctx.SaveChangesAsync();
        }
        var lockSvc = new RowJobLock<SchedulingTestInfrastructure.TestDbContext>(factory, tp);

        // 同实例再次抢锁（续约）应成功 —— 否则重试会被自己卡住。
        var acquired = await lockSvc.TryAcquireAsync("j", "inst-A", TimeSpan.FromMinutes(5), default);
        acquired.Should().BeTrue();
    }

    [Fact]
    public async Task Release_OwnerClearsLock_NonOwnerDoesNot()
    {
        var tp = new FakeTimeProvider();
        var factory = SchedulingTestInfrastructure.CreateFactory(Guid.NewGuid().ToString("N"));
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "j",
                Name = "n",
                CronExpression = "0 0 9 * * ?",
                JobType = "t",
                LockedByInstance = "inst-A",
                LockedUntil = tp.GetUtcNow().AddMinutes(5),
            });
            await ctx.SaveChangesAsync();
        }
        var lockSvc = new RowJobLock<SchedulingTestInfrastructure.TestDbContext>(factory, tp);

        // 非 owner release 不应清锁。
        await lockSvc.ReleaseAsync("j", "inst-B", default);
        await using (var v1 = factory.CreateDbContext())
        {
            v1.ScheduledJobs.Single().LockedByInstance.Should().Be("inst-A");
        }

        // owner release 清锁。
        await lockSvc.ReleaseAsync("j", "inst-A", default);
        await using var v2 = factory.CreateDbContext();
        var job = v2.ScheduledJobs.Single();
        job.LockedByInstance.Should().BeNull();
        job.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task IsRunning_HeldAndValid_ReturnsTrue_AfterLease_ReturnsFalse()
    {
        var tp = new FakeTimeProvider();
        var factory = SchedulingTestInfrastructure.CreateFactory(Guid.NewGuid().ToString("N"));
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "j",
                Name = "n",
                CronExpression = "0 0 9 * * ?",
                JobType = "t",
                LockedByInstance = "inst-A",
                LockedUntil = tp.GetUtcNow().AddMinutes(5),
            });
            await ctx.SaveChangesAsync();
        }
        var lockSvc = new RowJobLock<SchedulingTestInfrastructure.TestDbContext>(factory, tp);

        (await lockSvc.IsRunningAsync("j", default)).Should().BeTrue();

        // 租约过期后不再算「运行中」。
        tp.Advance(TimeSpan.FromMinutes(10));
        (await lockSvc.IsRunningAsync("j", default)).Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquire_NonExistentJob_ReturnsFalse()
    {
        var lockSvc = await CreateLockAsync(Guid.NewGuid().ToString("N"));
        var acquired = await lockSvc.TryAcquireAsync("does-not-exist", "inst", TimeSpan.FromMinutes(1), default);
        acquired.Should().BeFalse();
    }
}
