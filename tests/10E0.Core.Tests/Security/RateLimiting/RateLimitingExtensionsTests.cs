using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Security.RateLimiting;

namespace TenE0.Core.Tests.Security.RateLimiting;

/// <summary>
/// 限流 DI 扩展单元测试（issue #162）。
/// 覆盖：AddTenE0RateLimiting 注册 options + RateLimiter 中间件；自定义策略名常量稳定。
/// </summary>
[Trait("Category", "Unit")]
public sealed class RateLimitingExtensionsTests
{
    [Fact]
    public void AddTenE0RateLimiting_RegistersOptionsWithDefaults()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTenE0RateLimiting();
        var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        options.Enabled.Should().BeTrue();
        options.GlobalRules.Should().NotBeEmpty();
        options.EndpointRules.Should().NotBeEmpty();
    }

    [Fact]
    public void AddTenE0RateLimiting_AppliesCustomConfigure()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTenE0RateLimiting(o =>
        {
            o.Enabled = false;
            o.PermitAuthenticatedBypass = true;
        });
        var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        options.Enabled.Should().BeFalse();
        options.PermitAuthenticatedBypass.Should().BeTrue();
    }

    [Fact]
    public void PolicyName_IsStable()
    {
        RateLimitingExtensions.PolicyName.Should().Be("tene0-policy");
    }
}
