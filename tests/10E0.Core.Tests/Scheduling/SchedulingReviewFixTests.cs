using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Scheduling;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Tests.Scheduling;

/// <summary>
/// 覆盖 PR #180 code review 修复点的测试：
/// - Critical #2 白名单 fail-secure（空数组拒绝、null 放行）
/// - #3 ParametersJson 反序列化传入 JobContext
/// - #7 LockLeaseDuration ≥ JobTimeout 启动期校验
/// </summary>
[Trait("Category", "Unit")]
public sealed class SchedulingReviewFixTests
{
    // ================================================================
    // Critical #2: 白名单 fail-secure
    // ================================================================

    [Fact]
    public async Task CreateJob_AllowedAssembliesEmptyArray_RejectsAllDynamicJobs()
    {
        // 显式配 [] = 「设了防但没放任何」→ 应拒绝所有 JobType（fail-secure）。
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var scheduler = new Scheduler<SchedulingTestInfrastructure.TestDbContext>(
            factory, new NoOpJobLock(),
            SchedulingTestInfrastructure.DefaultOptions(o => o.AllowedAssemblies = []),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

        var act = () => scheduler.CreateJobAsync(
            "c", "n", "0 0 9 * * ?", typeof(SuccessJob).AssemblyQualifiedName!,
            null, 3, TimeSpan.FromMinutes(1), default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*白名单*");
    }

    [Fact]
    public async Task CreateJob_AllowedAssembliesNull_JobAssembliesEmpty_AllowsAll()
    {
        // AllowedAssemblies=null 且 JobAssemblies=[]（opt-in 默认）→ 视为「不限制」→ 放行。
        // 关键：这是 demo 零配置可用的兜底语义。
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var scheduler = new Scheduler<SchedulingTestInfrastructure.TestDbContext>(
            factory, new NoOpJobLock(),
            SchedulingTestInfrastructure.DefaultOptions(o =>
            {
                o.AllowedAssemblies = null;
                o.JobAssemblies = [];
            }),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

        var job = await scheduler.CreateJobAsync(
            "allow-all", "n", "0 0 9 * * ?", typeof(SuccessJob).AssemblyQualifiedName!,
            null, 3, TimeSpan.FromMinutes(1), default);

        job.Code.Should().Be("allow-all");
    }

    [Fact]
    public async Task CreateJob_AllowedAssembliesNull_JobAssembliesSet_UsesJobAssembliesAsWhitelist()
    {
        // AllowedAssemblies=null 但 JobAssemblies 设了 → 用 JobAssemblies 作白名单（DI 默认行为）。
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var scheduler = new Scheduler<SchedulingTestInfrastructure.TestDbContext>(
            factory, new NoOpJobLock(),
            SchedulingTestInfrastructure.DefaultOptions(o =>
            {
                o.AllowedAssemblies = null;
                // JobAssemblies = 测试程序集 → SuccessJob 在内 → 放行。
                o.JobAssemblies = [typeof(SuccessJob).Assembly];
            }),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

        var job = await scheduler.CreateJobAsync(
            "via-jobasm", "n", "0 0 9 * * ?", typeof(SuccessJob).AssemblyQualifiedName!,
            null, 3, TimeSpan.FromMinutes(1), default);

        job.Code.Should().Be("via-jobasm");
    }

    // ================================================================
    // #3: ParametersJson 反序列化传入 JobContext
    // ================================================================

    /// <summary>测试任务：捕获收到的 JobContext 以断言参数。</summary>
    internal sealed class CapturingJob : IScheduledJob
    {
        public JobContext? ReceivedContext;
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            ReceivedContext = context;
            return Task.CompletedTask;
        }
    }

    /// <summary>测试参数 record。</summary>
    public sealed record SampleParameters(string Path, int MaxFiles);

    [Fact]
    public async Task ExecuteAsync_PassesParametersJsonAsJsonElementToContext()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var job = new TenE0ScheduledJob
        {
            Code = "with-params",
            Name = "WithParams",
            CronExpression = "0 * * * * ?",
            JobType = typeof(CapturingJob).AssemblyQualifiedName!,
            ParametersJson = """{"path":"/tmp","maxFiles":5}""",
            IsEnabled = true,
            MaxRetries = 3,
            RetryInterval = TimeSpan.FromMilliseconds(10),
        };
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        var capturing = new CapturingJob();
        var sp = SchedulingTestInfrastructure.BuildServiceProvider(factory, capturing);
        var executor = sp.GetRequiredService<JobExecutor<SchedulingTestInfrastructure.TestDbContext>>();

        await executor.ExecuteAsync(job, "inst-1", default);

        // 验证：JobContext.Parameters 非空，且 GetParameters<T>() 能反序列化为强类型。
        capturing.ReceivedContext.Should().NotBeNull();
        capturing.ReceivedContext!.Parameters.Should().NotBeNull();
        var p = capturing.ReceivedContext.GetParameters<SampleParameters>();
        p.Should().NotBeNull();
        p!.Path.Should().Be("/tmp");
        p.MaxFiles.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteAsync_NullParametersJson_PassesNullToContext()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var job = new TenE0ScheduledJob
        {
            Code = "no-params",
            Name = "NoParams",
            CronExpression = "0 * * * * ?",
            JobType = typeof(CapturingJob).AssemblyQualifiedName!,
            ParametersJson = null, // 无参数
            IsEnabled = true,
            MaxRetries = 3,
            RetryInterval = TimeSpan.FromMilliseconds(10),
        };
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        var capturing = new CapturingJob();
        var sp = SchedulingTestInfrastructure.BuildServiceProvider(factory, capturing);
        var executor = sp.GetRequiredService<JobExecutor<SchedulingTestInfrastructure.TestDbContext>>();

        await executor.ExecuteAsync(job, "inst-1", default);

        capturing.ReceivedContext!.Parameters.Should().BeNull();
        capturing.ReceivedContext.GetParameters<SampleParameters>().Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_InvalidParametersJson_TreatedAsNullDoesNotFailExecution()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = SchedulingTestInfrastructure.CreateFactory(dbName);
        var job = new TenE0ScheduledJob
        {
            Code = "bad-json",
            Name = "BadJson",
            CronExpression = "0 * * * * ?",
            JobType = typeof(CapturingJob).AssemblyQualifiedName!,
            ParametersJson = "{ not valid json", // 非法 JSON
            IsEnabled = true,
            MaxRetries = 3,
            RetryInterval = TimeSpan.FromMilliseconds(10),
        };
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(job);
            await ctx.SaveChangesAsync();
        }

        var capturing = new CapturingJob();
        var sp = SchedulingTestInfrastructure.BuildServiceProvider(factory, capturing);
        var executor = sp.GetRequiredService<JobExecutor<SchedulingTestInfrastructure.TestDbContext>>();

        var status = await executor.ExecuteAsync(job, "inst-1", default);

        // 非法 JSON 不应导致执行失败，按无参数处理。
        status.Should().Be(JobExecutionStatus.Success);
        capturing.ReceivedContext!.Parameters.Should().BeNull();
    }

    // ================================================================
    // #7: LockLeaseDuration ≥ JobTimeout 启动期校验
    // ================================================================

    [Fact]
    public void AddTenE0Scheduling_LeaseLessThanTimeout_RegistersValidation()
    {
        // 仅验证 DI 注册不抛（ValidateOnStart 在 host 启动时才触发，单测不构建 host）；
        // 这里验证 options 校验委托逻辑：构造非法配置后手动 validate。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<SchedulingTestInfrastructure.TestDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddTenE0Scheduling<SchedulingTestInfrastructure.TestDbContext>(configure: o =>
        {
            o.JobTimeout = TimeSpan.FromMinutes(10);
            o.LockLeaseDuration = TimeSpan.FromMinutes(1); // 1 < 10 非法
        });

        using var sp = services.BuildServiceProvider();
        var opt = sp.GetRequiredService<IOptions<SchedulingOptions>>();

        // 触发校验：OptionsValidationException 应抛出。
        var act = () => _ = opt.Value;
        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddTenE0Scheduling_LeaseGreaterThanOrEqualTimeout_Accepts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<SchedulingTestInfrastructure.TestDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddTenE0Scheduling<SchedulingTestInfrastructure.TestDbContext>(configure: o =>
        {
            o.JobTimeout = TimeSpan.FromMinutes(5);
            o.LockLeaseDuration = TimeSpan.FromMinutes(5); // 相等，合法
        });

        using var sp = services.BuildServiceProvider();
        var opt = sp.GetRequiredService<IOptions<SchedulingOptions>>();
        opt.Value.JobTimeout.Should().Be(TimeSpan.FromMinutes(5));
        opt.Value.LockLeaseDuration.Should().Be(TimeSpan.FromMinutes(5));
    }
}
