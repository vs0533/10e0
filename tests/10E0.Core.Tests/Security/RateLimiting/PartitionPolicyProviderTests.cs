using TenE0.Core.Security.RateLimiting;

namespace TenE0.Core.Tests.Security.RateLimiting;

/// <summary>
/// 限流分区策略单元测试（issue #162）。
/// 覆盖：最长前缀匹配规则选择 + 各 <see cref="PartitionKind"/> 的分区 key 构造。
/// </summary>
[Trait("Category", "Unit")]
public sealed class PartitionPolicyProviderTests
{
    [Fact]
    public void ResolveRules_ExactEndpointMatch_ReturnsEndpointRules()
    {
        var options = new RateLimitOptions(); // 装载默认规则

        var rules = PartitionPolicyProvider.ResolveRules("/auth/login", options);

        rules.Should().HaveCount(1);
        rules[0].Partition.Should().Be(PartitionKind.Ip);
        rules[0].PermitLimit.Should().Be(10);
    }

    [Fact]
    public void ResolveRules_LongestPrefixWins_OverShorterAndGlobal()
    {
        var options = new RateLimitOptions
        {
            EndpointRules = new(StringComparer.Ordinal)
            {
                ["/auth"] = [new RateLimitRule(PartitionKind.Ip, 50, TimeSpan.FromMinutes(1))],
                ["/auth/login"] = [new RateLimitRule(PartitionKind.Ip, 10, TimeSpan.FromMinutes(1))],
            },
        };

        var rules = PartitionPolicyProvider.ResolveRules("/auth/login", options);

        rules.Should().HaveCount(1);
        rules[0].PermitLimit.Should().Be(10, "最长前缀 /auth/login 应覆盖 /auth");
    }

    [Fact]
    public void ResolveRules_NoEndpointMatch_FallsBackToGlobal()
    {
        var options = new RateLimitOptions();

        var rules = PartitionPolicyProvider.ResolveRules("/some/random/path", options);

        rules.Should().BeSameAs(options.GlobalRules);
    }

    [Theory]
    [InlineData(PartitionKind.Ip, "1.2.3.4", null, "/p", "ip:1.2.3.4")]
    [InlineData(PartitionKind.User, "1.2.3.4", "alice", "/p", "user:alice")]
    [InlineData(PartitionKind.User, "1.2.3.4", null, "/p", "user:anon|1.2.3.4")]
    [InlineData(PartitionKind.IpAndEndpoint, "1.2.3.4", "alice", "/auth/login", "ip-ep:1.2.3.4|/auth/login")]
    [InlineData(PartitionKind.UserAndEndpoint, "1.2.3.4", "alice", "/auth/refresh", "user-ep:alice|/auth/refresh")]
    [InlineData(PartitionKind.UserAndEndpoint, "1.2.3.4", null, "/auth/refresh", "user-ep:anon|1.2.3.4|/auth/refresh")]
    public void BuildPartitionKey_AllKinds_ProduceExpectedKey(
        PartitionKind kind, string ip, string? user, string path, string expected)
    {
        var key = PartitionPolicyProvider.BuildPartitionKey(kind, ip, user, path);
        key.Should().Be(expected);
    }

    [Fact]
    public void BuildPartitions_QueuedRule_ProducesSlidingWindow()
    {
        var rule = new RateLimitRule(PartitionKind.Ip, 10, TimeSpan.FromMinutes(1), QueueLimit: 5);

        var partitions = PartitionPolicyProvider.BuildPartitions("1.2.3.4", null, "/p", [rule]);

        partitions.Should().HaveCount(1);
        partitions[0].PartitionKey.Should().Be("ip:1.2.3.4");
    }

    [Fact]
    public void BuildPartitions_NonQueuedRule_ProducesFixedWindow()
    {
        var rule = new RateLimitRule(PartitionKind.User, 10, TimeSpan.FromMinutes(1));

        var partitions = PartitionPolicyProvider.BuildPartitions("1.2.3.4", "bob", "/p", [rule]);

        partitions.Should().HaveCount(1);
        partitions[0].PartitionKey.Should().Be("user:bob");
    }

    [Fact]
    public void DefaultRules_IncludeCriticalEndpoints()
    {
        RateLimitOptions.DefaultRules.Endpoints.Should().ContainKey("/auth/login");
        RateLimitOptions.DefaultRules.Endpoints.Should().ContainKey("/auth/refresh");
        RateLimitOptions.DefaultRules.Endpoints.Should().ContainKey("/captcha/image");
        RateLimitOptions.DefaultRules.Endpoints.Should().ContainKey("/files/upload");
    }
}
