using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth;

namespace TenE0.Core.Tests.Auth;

public sealed class HttpCurrentUserContextTests
{
    private sealed class UserInfoStub : ICurrentUserInfo
    {
        public string UserCode { get; }
        public string DisplayName { get; }
        public UserType UserType { get; }

        public UserInfoStub(string userCode, string displayName, UserType userType)
        {
            UserCode = userCode;
            DisplayName = displayName;
            UserType = userType;
        }
    }

    private (Mock<IHttpContextAccessor> HttpContextAccessor, Mock<IDistributedCache> Cache, Mock<IUserInfoLoader> Loader, HttpCurrentUserContext Ctx) CreateWithClaims(string userCode, string[] roles, string userType = "Person")
    {
        var claims = new List<Claim>
        {
            new(JwtClaims.Subject, userCode),
            new(JwtClaims.UserType, userType),
        };
        foreach (var role in roles)
            claims.Add(new Claim(JwtClaims.Role, role));

        var identity = new ClaimsIdentity(claims, "test");

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.SetupGet(c => c.User).Returns(new ClaimsPrincipal(identity));

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContextMock.Object);

        var cache = new Mock<IDistributedCache>();
        var loader = new Mock<IUserInfoLoader>();

        var ctx = new HttpCurrentUserContext(accessor.Object, cache.Object, loader.Object);
        return (accessor, cache, loader, ctx);
    }

    [Fact]
    public void NoHttpContext_ShouldNotBeAuthenticated()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns((HttpContext?)null);

        var cache = new Mock<IDistributedCache>();
        var loader = new Mock<IUserInfoLoader>();

        var ctx = new HttpCurrentUserContext(accessor.Object, cache.Object, loader.Object);

        ctx.IsAuthenticated.Should().BeFalse();
        ctx.UserCode.Should().BeNull();
        ctx.RoleIds.Should().BeEmpty();
    }

    [Fact]
    public void WithClaims_ShouldReadUserCode()
    {
        var (_, _, _, ctx) = CreateWithClaims("emp_001", ["admin"]);
        ctx.UserCode.Should().Be("emp_001");
    }

    [Fact]
    public void WithClaims_ShouldReadRoles()
    {
        var (_, _, _, ctx) = CreateWithClaims("u1", ["role_a", "role_b"]);
        ctx.RoleIds.Should().BeEquivalentTo("role_a", "role_b");
    }

    [Fact]
    public void WithClaims_ShouldReadUserType()
    {
        var (_, _, _, ctx) = CreateWithClaims("u1", [], "Unit");
        ctx.UserType.Should().Be(UserType.Unit);
    }

    [Fact]
    public void WithoutUserTypeClaim_ShouldDefaultToPerson()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtClaims.Subject, "u1"),
            new Claim(JwtClaims.Role, "viewer"),
        }, "test");

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.SetupGet(c => c.User).Returns(new ClaimsPrincipal(identity));

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContextMock.Object);

        var cache = new Mock<IDistributedCache>();
        var loader = new Mock<IUserInfoLoader>();

        var ctx = new HttpCurrentUserContext(accessor.Object, cache.Object, loader.Object);

        ctx.UserType.Should().Be(UserType.Person);
    }

    [Fact]
    public async Task GetUserInfoAsync_NotAuthenticated_ShouldReturnNull()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns((HttpContext?)null);

        var cache = new Mock<IDistributedCache>();
        var loader = new Mock<IUserInfoLoader>();

        var ctx = new HttpCurrentUserContext(accessor.Object, cache.Object, loader.Object);

        var result = await ctx.GetUserInfoAsync();
        result.Should().BeNull();

        cache.VerifyNoOtherCalls();
        loader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetUserInfoAsync_CacheHit_ShouldReturnCached()
    {
        var (_, cache, loader, ctx) = CreateWithClaims("u1", []);

        var cachedPayload = "cached-json";
        var cachedInfo = new UserInfoStub("u1", "Cached Name", UserType.Person);
        var cachedBytes = Encoding.UTF8.GetBytes(cachedPayload);

        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedBytes);

        loader.Setup(l => l.Deserialize(cachedPayload, UserType.Person))
            .Returns(cachedInfo);

        var result = await ctx.GetUserInfoAsync();

        result.Should().BeSameAs(cachedInfo);
        cache.Verify(c => c.GetAsync(It.Is<string>(s => s == $"user_info:u1"), It.IsAny<CancellationToken>()), Times.Once);
        loader.Verify(l => l.Deserialize(cachedPayload, UserType.Person), Times.Once);
        loader.Verify(l => l.LoadAsync(It.IsAny<string>(), It.IsAny<UserType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetUserInfoAsync_CacheMiss_ShouldLoadAndCache()
    {
        var (_, cache, loader, ctx) = CreateWithClaims("u1", []);

        var loadedInfo = new UserInfoStub("u1", "Fresh Name", UserType.Person);
        var serialized = "serialized-data";
        var serializedBytes = Encoding.UTF8.GetBytes(serialized);

        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        loader.Setup(l => l.LoadAsync("u1", UserType.Person, It.IsAny<CancellationToken>()))
            .ReturnsAsync(loadedInfo);

        loader.Setup(l => l.Serialize(loadedInfo))
            .Returns(serialized);

        var result = await ctx.GetUserInfoAsync();

        result.Should().BeSameAs(loadedInfo);
        loader.Verify(l => l.LoadAsync("u1", UserType.Person, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUserInfoAsync_LoaderReturnsNull_ShouldNotCache()
    {
        var (_, cache, loader, ctx) = CreateWithClaims("u1", []);

        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        loader.Setup(l => l.LoadAsync("u1", UserType.Person, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ICurrentUserInfo?)null);

        var result = await ctx.GetUserInfoAsync();

        result.Should().BeNull();
        // Loader was called once, no cache write needed to verify (SetStringAsync is extension method)
        loader.Verify(l => l.LoadAsync("u1", UserType.Person, It.IsAny<CancellationToken>()), Times.Once);
    }
}
