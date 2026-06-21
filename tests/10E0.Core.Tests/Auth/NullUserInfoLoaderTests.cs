using TenE0.Core.Abstractions;
using TenE0.Core.Auth;

namespace TenE0.Core.Tests.Auth;

[Trait("Category", "Unit")]
public sealed class NullUserInfoLoaderTests
{
    private readonly NullUserInfoLoader _sut = new();

    [Fact]
    public async Task LoadAsync_ReturnsNull()
    {
        var result = await _sut.LoadAsync("alice", UserType.Person, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public void Serialize_ReturnsEmptyString()
    {
        var info = new StubUserInfo("alice", "Alice", UserType.Person);

        var result = _sut.Serialize(info);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_ReturnsNull()
    {
        var result = _sut.Deserialize("anything", UserType.Person);

        result.Should().BeNull();
    }

    private sealed record StubUserInfo(string UserCode, string DisplayName, UserType UserType) : ICurrentUserInfo;
}
