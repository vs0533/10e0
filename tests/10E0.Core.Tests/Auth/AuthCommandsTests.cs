using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Tests.Auth;

public sealed class AuthCommandsTests
{
    #region LoginCommand

    [Fact]
    public void LoginCommand_ImplementsICommand()
    {
        var cmd = new LoginCommand("alice", "secret", "192.168.1.1");

        cmd.Should().BeAssignableTo<ICommand<AuthResult>>();
    }

    [Fact]
    public void LoginCommand_SingleArgument_ShouldSetCorrectValues()
    {
        var cmd = new LoginCommand("alice", "secret");

        cmd.UserCode.Should().Be("alice");
        cmd.Password.Should().Be("secret");
        cmd.ClientIp.Should().BeNull();
    }

    [Fact]
    public void LoginCommand_WithClientIp_ShouldSetAllValues()
    {
        var cmd = new LoginCommand("alice", "secret", "10.0.0.1");

        cmd.UserCode.Should().Be("alice");
        cmd.Password.Should().Be("secret");
        cmd.ClientIp.Should().Be("10.0.0.1");
    }

    #endregion

    #region RefreshTokenCommand

    [Fact]
    public void RefreshTokenCommand_ImplementsICommand()
    {
        var cmd = new RefreshTokenCommand("some-token");

        cmd.Should().BeAssignableTo<ICommand<AuthResult>>();
    }

    [Fact]
    public void RefreshTokenCommand_WithoutClientIp_ShouldBeNull()
    {
        var cmd = new RefreshTokenCommand("some-token");

        cmd.RefreshToken.Should().Be("some-token");
        cmd.ClientIp.Should().BeNull();
    }

    [Fact]
    public void RefreshTokenCommand_WithClientIp_ShouldSetValue()
    {
        var cmd = new RefreshTokenCommand("some-token", "127.0.0.1");

        cmd.ClientIp.Should().Be("127.0.0.1");
    }

    #endregion

    #region LogoutCommand

    [Fact]
    public void LogoutCommand_ImplementsICommand()
    {
        var cmd = new LogoutCommand("token-to-invalidate");

        cmd.Should().BeAssignableTo<ICommand<Unit>>();
    }

    [Fact]
    public void LogoutCommand_ShouldStoreToken()
    {
        var cmd = new LogoutCommand("token-123");

        cmd.RefreshToken.Should().Be("token-123");
    }

    #endregion

    #region AuthResult

    [Fact]
    public void AuthResult_Constructor_ShouldCreateRecord()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new AuthResult(
            "access-token-123",
            now.AddMinutes(30),
            "refresh-token-456",
            now.AddDays(7),
            "alice",
            "Alice Smith",
            new[] { "admin", "user" });

        result.AccessToken.Should().Be("access-token-123");
        result.AccessTokenExpiresAt.Should().BeCloseTo(now.AddMinutes(30), TimeSpan.FromSeconds(1));
        result.RefreshToken.Should().Be("refresh-token-456");
        result.RefreshTokenExpiresAt.Should().BeCloseTo(now.AddDays(7), TimeSpan.FromSeconds(1));
        result.UserCode.Should().Be("alice");
        result.DisplayName.Should().Be("Alice Smith");
        result.Roles.Should().BeEquivalentTo("admin", "user");
    }

    [Fact]
    public void AuthResult_IsImmutableRecord()
    {
        var result = new AuthResult(
            "token",
            DateTimeOffset.UtcNow,
            "refresh",
            DateTimeOffset.UtcNow,
            "user",
            "Name",
            Array.Empty<string>());

        var withNewName = result with { DisplayName = "New Name" };

        withNewName.DisplayName.Should().Be("New Name");
        result.DisplayName.Should().Be("Name", "original should be unchanged");
    }

    #endregion
}
