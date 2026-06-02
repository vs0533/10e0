using System.Security.Claims;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth;

namespace TenE0.Core.Tests.Auth;

public sealed class AmbientCurrentUserContextTests
{
    private AmbientCurrentUserContext CreateContext() => new();

    [Fact]
    public void InitialState_ShouldNotBeAuthenticated()
    {
        var ctx = CreateContext();
        ctx.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public void InitialState_UserCode_ShouldBeNull()
    {
        var ctx = CreateContext();
        ctx.UserCode.Should().BeNull();
    }

    [Fact]
    public void InitialState_RoleIds_ShouldBeEmpty()
    {
        var ctx = CreateContext();
        ctx.RoleIds.Should().BeEmpty();
    }

    [Fact]
    public void Impersonate_ShouldSetClaims()
    {
        var ctx = CreateContext();
        var principal = AmbientCurrentUserContext.BuildPrincipal("u001", ["admin"]);

        using (ctx.Impersonate(principal))
        {
            ctx.IsAuthenticated.Should().BeTrue();
            ctx.UserCode.Should().Be("u001");
        }
    }

    [Fact]
    public void Impersonate_ShouldSetRoles()
    {
        var ctx = CreateContext();
        var principal = AmbientCurrentUserContext.BuildPrincipal("u001", ["admin", "user"]);

        using (ctx.Impersonate(principal))
        {
            ctx.RoleIds.Should().ContainEquivalentOf("admin");
            ctx.RoleIds.Should().ContainEquivalentOf("user");
            ctx.RoleIds.Should().HaveCount(2);
        }
    }

    [Fact]
    public void Impersonate_ShouldSetUserType()
    {
        var principal = AmbientCurrentUserContext.BuildPrincipal("u001", [], UserType.Unit);
        var ctx = CreateContext();

        using (ctx.Impersonate(principal))
        {
            ctx.UserType.Should().Be(UserType.Unit);
        }
    }

    [Fact]
    public void Impersonate_Dispose_ShouldRestoreNull()
    {
        var ctx = CreateContext();
        var principal = AmbientCurrentUserContext.BuildPrincipal("u001", ["admin"]);

        {
            using (ctx.Impersonate(principal))
            {
                ctx.IsAuthenticated.Should().BeTrue();
            }
        }

        ctx.IsAuthenticated.Should().BeFalse();
        ctx.UserCode.Should().BeNull();
    }

    [Fact]
    public void Impersonate_Nested_ShouldRestoreCorrectly()
    {
        var ctx = CreateContext();
        var outer = AmbientCurrentUserContext.BuildPrincipal("outer", ["role_a"]);
        var inner = AmbientCurrentUserContext.BuildPrincipal("inner", ["role_b"]);

        using (ctx.Impersonate(outer))
        {
            ctx.UserCode.Should().Be("outer");

            using (ctx.Impersonate(inner))
            {
                ctx.UserCode.Should().Be("inner");
            }

            // After inner dispose, restore to outer
            ctx.UserCode.Should().Be("outer");
        }

        // After outer dispose, back to null
        ctx.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public void Impersonate_UserType_InvalidClaim_ShouldDefaultToPerson()
    {
        var claims = new List<Claim>
        {
            new(JwtClaims.Subject, "u001"),
            new(JwtClaims.UserType, "NotAValidUserType"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        var ctx = CreateContext();

        using (ctx.Impersonate(principal))
        {
            ctx.UserType.Should().Be(UserType.Person);
        }
    }

    [Fact]
    public void BuildPrincipal_ShouldCreateCorrectClaims()
    {
        var principal = AmbientCurrentUserContext.BuildPrincipal("user01", ["admin", "editor"], UserType.Unit);

        principal.FindFirstValue(JwtClaims.Subject).Should().Be("user01");
        principal.FindFirstValue(JwtClaims.UserType).Should().Be("Unit");
        principal.FindAll(JwtClaims.Role)
                 .Select(c => c.Value)
                 .Should().Contain("admin");
        principal.FindAll(JwtClaims.Role)
                 .Select(c => c.Value)
                 .Should().Contain("editor");
    }

    [Fact]
    public async Task GetUserInfoAsync_ShouldReturnNull()
    {
        var ctx = CreateContext();
        var principal = AmbientCurrentUserContext.BuildPrincipal("u001", []);

        using (ctx.Impersonate(principal))
        {
            var result = await ctx.GetUserInfoAsync();
            result.Should().BeNull();
        }
    }
}
