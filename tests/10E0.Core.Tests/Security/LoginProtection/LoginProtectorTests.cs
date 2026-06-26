using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Security.LoginProtection;

namespace TenE0.Core.Tests.Security.LoginProtection;

/// <summary>
/// 登录失败锁定单元测试（issue #162）。
/// 覆盖：失败计数 + 滑动窗口重置 + 阈值触发锁定 + 锁定期拒绝 + 成功清零 + 主动解锁。
/// </summary>
[Trait("Category", "Unit")]
public sealed class LoginProtectorTests
{
    private static LoginProtector CreateProtector(
        out FakeTimeProvider clock,
        int maxAttempts = 5,
        TimeSpan? window = null,
        TimeSpan? lockout = null)
    {
        clock = new FakeTimeProvider();
        var options = Options.Create(new LoginProtectionOptions
        {
            MaxFailedAttempts = maxAttempts,
            SlidingWindow = window ?? TimeSpan.FromMinutes(10),
            LockoutDuration = lockout ?? TimeSpan.FromMinutes(15),
        });
        return new LoginProtector(new InMemoryLoginAttemptStore(), clock, options);
    }

    [Fact]
    public async Task EnsureNotLockedAsync_NoFailures_DoesNotThrow()
    {
        var protector = CreateProtector(out _);

        var state = await protector.EnsureNotLockedAsync("u001");

        state.FailedCount.Should().Be(0);
        state.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task RecordFailure_BelowThreshold_DoesNotLock()
    {
        var protector = CreateProtector(out _, maxAttempts: 5);

        for (var i = 0; i < 4; i++)
            await protector.RecordFailureAsync("u001");

        var act = async () => await protector.EnsureNotLockedAsync("u001");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordFailure_AtThreshold_TriggersLockout()
    {
        var protector = CreateProtector(out var clock, maxAttempts: 5);

        for (var i = 0; i < 5; i++)
            await protector.RecordFailureAsync("u001");

        var act = async () => await protector.EnsureNotLockedAsync("u001");
        await act.Should().ThrowAsync<AccountLockedException>();
    }

    [Fact]
    public async Task RecordFailure_LockUntilIsNowPlusLockoutDuration()
    {
        var lockout = TimeSpan.FromMinutes(15);
        var protector = CreateProtector(out var clock, maxAttempts: 3, lockout: lockout);
        var start = clock.GetUtcNow();

        for (var i = 0; i < 3; i++)
            await protector.RecordFailureAsync("u001");

        var ex = await Assert.ThrowsAsync<AccountLockedException>(
            () => protector.EnsureNotLockedAsync("u001"));
        ex.LockedUntil.Should().Be(start + lockout);
    }

    [Fact]
    public async Task EnsureNotLocked_AfterLockoutExpires_AllowsLogin()
    {
        var protector = CreateProtector(out var clock, maxAttempts: 3,
            lockout: TimeSpan.FromMinutes(15));

        for (var i = 0; i < 3; i++)
            await protector.RecordFailureAsync("u001");

        await Assert.ThrowsAsync<AccountLockedException>(
            () => protector.EnsureNotLockedAsync("u001"));

        // 推进时间超过锁定期
        clock.Advance(TimeSpan.FromMinutes(16));

        var act = async () => await protector.EnsureNotLockedAsync("u001");
        await act.Should().NotThrowAsync("锁定期过后应自动解锁");
    }

    [Fact]
    public async Task RecordSuccess_ClearsCounter_PreventingFalseLockout()
    {
        var protector = CreateProtector(out _, maxAttempts: 5);

        // 失败 4 次（未锁），成功一次 → 清零
        for (var i = 0; i < 4; i++)
            await protector.RecordFailureAsync("u001");
        await protector.RecordSuccessAsync("u001");

        // 再失败 4 次也不应锁（因为已清零）
        for (var i = 0; i < 4; i++)
            await protector.RecordFailureAsync("u001");

        var act = async () => await protector.EnsureNotLockedAsync("u001");
        await act.Should().NotThrowAsync("成功清零后 4 次失败不应触发 5 次阈值锁定");
    }

    [Fact]
    public async Task SlidingWindow_OldFailures_ExpireAndDoNotAccumulate()
    {
        var window = TimeSpan.FromMinutes(5);
        var protector = CreateProtector(out var clock, maxAttempts: 3, window: window);

        // 失败 2 次
        await protector.RecordFailureAsync("u001");
        await protector.RecordFailureAsync("u001");

        // 推进时间超过窗口 → 旧失败应过期
        clock.Advance(TimeSpan.FromMinutes(6));

        // 再失败 1 次（窗口已重置，应是第 1 次而非第 3 次）→ 不锁
        await protector.RecordFailureAsync("u001");

        var act = async () => await protector.EnsureNotLockedAsync("u001");
        await act.Should().NotThrowAsync("滑动窗口外的失败不应累计");
    }

    [Fact]
    public async Task Reset_ManuallyUnlocksAccount()
    {
        var protector = CreateProtector(out _, maxAttempts: 3);

        for (var i = 0; i < 3; i++)
            await protector.RecordFailureAsync("u001");

        await Assert.ThrowsAsync<AccountLockedException>(
            () => protector.EnsureNotLockedAsync("u001"));

        await protector.ResetAsync("u001");

        var act = async () => await protector.EnsureNotLockedAsync("u001");
        await act.Should().NotThrowAsync("Reset 应立即解锁");
    }

    [Fact]
    public async Task LockoutDisabled_OptionsOff_NeverLocks()
    {
        var clock = new FakeTimeProvider();
        var options = Options.Create(new LoginProtectionOptions
        {
            LockoutEnabled = false,
            MaxFailedAttempts = 2,
        });
        var protector = new LoginProtector(new InMemoryLoginAttemptStore(), clock, options);

        for (var i = 0; i < 10; i++)
            await protector.RecordFailureAsync("u001");

        var act = async () => await protector.EnsureNotLockedAsync("u001");
        await act.Should().NotThrowAsync("LockoutEnabled=false 时永不锁定");
    }

    [Fact]
    public async Task RecordFailure_DifferentUsersCountIndependently()
    {
        var protector = CreateProtector(out _, maxAttempts: 3);

        await protector.RecordFailureAsync("alice");
        await protector.RecordFailureAsync("alice");
        await protector.RecordFailureAsync("alice"); // alice 满 3 次
        await protector.RecordFailureAsync("bob"); // bob 只 1 次

        // alice 3 次应锁
        await Assert.ThrowsAsync<AccountLockedException>(
            () => protector.EnsureNotLockedAsync("alice"));

        // bob 不锁
        var act = async () => await protector.EnsureNotLockedAsync("bob");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void AccountLockedException_CarriesUserCodeAndLockedUntil()
    {
        var until = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var ex = new AccountLockedException("u001", until);

        ex.UserCode.Should().Be("u001");
        ex.LockedUntil.Should().Be(until);
    }
}

/// <summary>
/// LoginProtection DI 扩展单元测试（issue #162）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class LoginProtectionExtensionsTests
{
    [Fact]
    public void AddTenE0LoginProtection_RegistersStoreAndProtector()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddTenE0LoginProtection();

        var sp = services.BuildServiceProvider();
        sp.GetService<ILoginAttemptStore>().Should().BeOfType<InMemoryLoginAttemptStore>();
        sp.GetService<LoginProtector>().Should().NotBeNull();
    }

    [Fact]
    public void AddTenE0LoginProtection_AppliesCustomConfigure()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddTenE0LoginProtection(o => o.MaxFailedAttempts = 99);

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<LoginProtectionOptions>>().Value;
        options.MaxFailedAttempts.Should().Be(99);
    }
}
