using System.Security.Claims;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.Abstractions;
using TenE0.Core.Realtime;

namespace TenE0.Core.Tests.Realtime;

[Trait("Category", "Unit")]
public sealed class ClaimBasedUserIdProviderTests
{
    // HubConnectionContext 无无参 ctor 且构造期访问 Features（NRE），无法用 Mock<T> 直接构造。
    // 但 User 是 virtual —— 用 Mock<T> 传 ctor 参数 + SetupAllProperties 再覆盖 User。
    // 为绕过构造期 NRE，提供一个 Features 已初始化的 ConnectionContext 替身。
    private static HubConnectionContext BuildContext(ClaimsPrincipal user)
    {
        var connection = new Mock<ConnectionContext>();
        var features = new FeatureCollection();
        connection.SetupGet(c => c.Features).Returns(features);

        var mock = new Mock<HubConnectionContext>(
            connection.Object, new HubConnectionContextOptions(), NullLoggerFactory.Instance)
        { CallBase = true };
        mock.SetupGet(c => c.User).Returns(user);
        return mock.Object;
    }

    [Fact]
    public void GetUserId_ReturnsSubClaimValue()
    {
        var provider = new ClaimBasedUserIdProvider();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(JwtClaims.Subject, "alice") }, "Test"));

        provider.GetUserId(BuildContext(user)).Should().Be("alice");
    }

    [Fact]
    public void GetUserId_ReturnsNull_WhenNoSubClaim()
    {
        var provider = new ClaimBasedUserIdProvider();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(JwtClaims.Role, "viewer") }, "Test"));

        provider.GetUserId(BuildContext(user)).Should().BeNull();
    }
}
