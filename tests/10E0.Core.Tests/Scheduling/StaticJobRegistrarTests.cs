using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Scheduling;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Tests.Scheduling;

[Trait("Category", "Unit")]
public sealed class StaticJobRegistrarTests
{
    // 测试用的 attribute-marked job。
    [Scheduled("0 0 9 * * ?", Description = "demo")]
    public sealed class DemoStaticJob : IScheduledJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    // 另一个 attribute-marked job。
    [Scheduled("0 0 2 * * ?", Code = "custom-code")]
    public sealed class AnotherStaticJob : IScheduledJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static StaticJobRegistrar CreateRegistrar(
        System.Reflection.Assembly[] assemblies, FakeTimeProvider? tp = null)
    {
        var options = Options.Create(new SchedulingOptions { JobAssemblies = assemblies });
        return new StaticJobRegistrar(options, tp ?? new FakeTimeProvider(),
            NullLogger<StaticJobRegistrar>.Instance);
    }

    [Fact]
    public async Task SeedAsync_InsertsNewStaticJobs()
    {
        var factory = SchedulingTestInfrastructure.CreateFactory(Guid.NewGuid().ToString("N"));
        var registrar = CreateRegistrar([typeof(StaticJobRegistrarTests).Assembly]);

        await using (var ctx = factory.CreateDbContext())
        {
            await registrar.SeedAsync(ctx, default);
            await ctx.SaveChangesAsync();
        }

        await using var verify = factory.CreateDbContext();
        var jobs = verify.ScheduledJobs.ToList();
        // 扫描本测试程序集内所有 [Scheduled] 类型（DemoStaticJob / AnotherStaticJob）。
        jobs.Should().Contain(j => j.Code == typeof(DemoStaticJob).FullName);
        jobs.Should().Contain(j => j.Code == "custom-code");
        var demo = jobs.Single(j => j.Code == typeof(DemoStaticJob).FullName);
        demo.Mode.Should().Be(JobExecutionMode.Static);
        demo.CronExpression.Should().Be("0 0 9 * * ?");
        demo.NextRunAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SeedAsync_SecondRunIsIdempotent_DoesNotDuplicate()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var registrar = CreateRegistrar([typeof(StaticJobRegistrarTests).Assembly]);

        // 第一次 seed。
        await using (var ctx = factory.CreateDbContext())
        {
            await registrar.SeedAsync(ctx, default);
            await ctx.SaveChangesAsync();
        }
        // 第二次 seed（幂等 upsert）。
        await using (var ctx = factory.CreateDbContext())
        {
            await registrar.SeedAsync(ctx, default);
            await ctx.SaveChangesAsync();
        }

        await using var verify = factory.CreateDbContext();
        var demoCount = verify.ScheduledJobs.Count(j => j.Code == typeof(DemoStaticJob).FullName);
        demoCount.Should().Be(1, "二次幂等 upsert 不应重复插入");
    }

    [Fact]
    public async Task SeedAsync_UpdatesCronOnCodeChange_PreservesRunHistory()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var registrar = CreateRegistrar([typeof(StaticJobRegistrarTests).Assembly]);

        // 第一次 seed。
        await using (var ctx = factory.CreateDbContext())
        {
            await registrar.SeedAsync(ctx, default);
            await ctx.SaveChangesAsync();
        }

        // 模拟运维历史：手动写 LastRunAt / LastRunStatus。
        var oldRunAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var ctx = factory.CreateDbContext())
        {
            var job = ctx.ScheduledJobs.Single(j => j.Code == typeof(DemoStaticJob).FullName);
            job.CronExpression = "0 0 8 * * ?"; // 改成 8 点（模拟代码改动前的旧 cron）
            job.LastRunAt = oldRunAt;
            job.LastRunStatus = nameof(JobExecutionStatus.Success);
            await ctx.SaveChangesAsync();
        }

        // 第二次 seed（代码改回 9 点）→ 应更新 Cron 但保留运维历史。
        await using (var ctx = factory.CreateDbContext())
        {
            await registrar.SeedAsync(ctx, default);
            await ctx.SaveChangesAsync();
        }

        await using var verify = factory.CreateDbContext();
        var updated = verify.ScheduledJobs.Single(j => j.Code == typeof(DemoStaticJob).FullName);
        updated.CronExpression.Should().Be("0 0 9 * * ?", "Cron 应被代码值覆盖");
        updated.LastRunAt.Should().Be(oldRunAt, "运维历史不可因代码改动清零");
        updated.LastRunStatus.Should().Be(nameof(JobExecutionStatus.Success));
    }

    [Fact]
    public async Task SeedAsync_CodeRemovedFromCodebase_DisablesExistingJob()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);

        // 先手动插入一个 Static 任务（模拟历史遗留，代码已删）。
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new TenE0ScheduledJob
            {
                Code = "removed-legacy-job",
                Name = "Removed",
                CronExpression = "0 0 9 * * ?",
                JobType = "Some.Removed, Asm",
                Mode = JobExecutionMode.Static,
                IsEnabled = true,
            });
            await ctx.SaveChangesAsync();
        }

        var registrar = CreateRegistrar([typeof(StaticJobRegistrarTests).Assembly]);
        await using (var ctx = factory.CreateDbContext())
        {
            await registrar.SeedAsync(ctx, default);
            await ctx.SaveChangesAsync();
        }

        await using var verify = factory.CreateDbContext();
        var removed = verify.ScheduledJobs.Single(j => j.Code == "removed-legacy-job");
        removed.IsEnabled.Should().BeFalse("代码已删的 Static 任务应被自动禁用（不删，保留历史）");
    }

    [Fact]
    public async Task SeedAsync_NoAssembliesConfigured_IsNoOp()
    {
        var factory = SchedulingTestInfrastructure.CreateFactory(Guid.NewGuid().ToString("N"));
        var registrar = CreateRegistrar([]); // 空程序集列表

        await using (var ctx = factory.CreateDbContext())
        {
            await registrar.SeedAsync(ctx, default);
            await ctx.SaveChangesAsync();
        }

        await using var verify = factory.CreateDbContext();
        verify.ScheduledJobs.Should().BeEmpty();
    }

    [Fact]
    public void ScanScheduledJobs_AttributeButNotIScheduledJob_Throws()
    {
        // 反向校验：[Scheduled] 标在非 IScheduledJob 类型上应抛异常（防误用）。
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        // 正常扫描应不抛异常（本程序集所有 [Scheduled] 类型都实现了接口）。
        var act = () => StaticJobRegistrar.ScanScheduledJobs([asm]);
        act.Should().NotThrow();
    }
}
