using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Realtime;

namespace TenE0.Core.Tests.Realtime;

[Trait("Category", "Unit")]
public sealed class ClaimBasedGroupProviderTests
{
    private static IRealtimeGroupProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddOptions<RealtimeOptions>();
        var sp = services.BuildServiceProvider();
        return new ClaimBasedGroupProvider(sp.GetRequiredService<IOptions<RealtimeOptions>>());
    }

    private static ClaimsPrincipal UserWith(params (string type, string value)[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.type, c.value)),
            authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void GetGroups_ProducesAllFourGroupKinds_FromClaims()
    {
        var provider = CreateProvider();
        var user = UserWith(
            (JwtClaims.Subject, "alice"),
            (JwtClaims.Role, "viewer"),
            (JwtClaims.Role, "editor"),
            (JwtClaims.TenantId, "tenant-1"),
            (JwtClaims.Org, "org-node-hq"));

        var groups = provider.GetGroups(user);

        groups.Should().BeEquivalentTo(new[]
        {
            "user:alice",
            "role:viewer",
            "role:editor",
            "tenant:tenant-1",
            "org:org-node-hq"
        });
    }

    [Fact]
    public void GetGroups_OrgGroup_UsesOrgClaimValue_NotTenantId()
    {
        // org 与 tenant 正交：org 组值来自 org claim，不混 tenant_id
        var provider = CreateProvider();
        var user = UserWith(
            (JwtClaims.Subject, "bob"),
            (JwtClaims.TenantId, "T1"),
            (JwtClaims.Org, "HQ-NODE-ID"));

        var groups = provider.GetGroups(user);

        groups.Should().Contain("org:HQ-NODE-ID");
        groups.Should().Contain("tenant:T1");
        // 两个独立组，互不影响
        groups.Count(g => g.StartsWith("org:")).Should().Be(1);
        groups.Count(g => g.StartsWith("tenant:")).Should().Be(1);
    }

    [Fact]
    public void GetGroups_OmitsEmptyGroups_WhenClaimsMissing()
    {
        // 缺 org / tenant claim 的用户（如系统账号 / 未绑定组织）不产出对应组 —— 安全降级
        var provider = CreateProvider();
        var user = UserWith((JwtClaims.Subject, "admin"), (JwtClaims.Role, "super_admin"));

        var groups = provider.GetGroups(user);

        groups.Should().BeEquivalentTo(new[] { "user:admin", "role:super_admin" });
        groups.Should().NotContain(g => g.StartsWith("org:"));
        groups.Should().NotContain(g => g.StartsWith("tenant:"));
    }

    [Fact]
    public void GetGroups_NoSubClaim_StillProducesRoleGroups()
    {
        var provider = CreateProvider();
        var user = UserWith((JwtClaims.Role, "viewer"));

        var groups = provider.GetGroups(user);

        groups.Should().BeEquivalentTo(new[] { "role:viewer" });
    }

    [Fact]
    public void GetGroups_Throws_OnNullUser()
    {
        var provider = CreateProvider();
        var act = () => provider.GetGroups(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
