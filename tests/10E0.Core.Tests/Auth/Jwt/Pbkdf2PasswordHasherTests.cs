using TenE0.Core.Auth.Jwt.Services;

namespace TenE0.Core.Tests.Auth.Jwt;

public sealed class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_ShouldNotThrowWithValidPassword()
    {
        Action act = () => _hasher.Hash("mySecurePassword123");

        act.Should().NotThrow();
    }

    [Fact]
    public void Hash_EmptyPassword_ShouldThrow()
    {
        Action act = () => _hasher.Hash("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Hash_NullPassword_ShouldThrow()
    {
        Action act = () => _hasher.Hash(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Hash_Twice_SamePassword_ShouldReturnDifferentValue()
    {
        var hash1 = _hasher.Hash("samePassword");
        var hash2 = _hasher.Hash("samePassword");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Verify_CorrectPassword_ShouldReturnTrue()
    {
        var password = "correctPassword42";
        var hash = _hasher.Hash(password);

        var result = _hasher.Verify(password, hash);

        result.Should().BeTrue();
    }

    [Fact]
    public void Verify_WrongPassword_ShouldReturnFalse()
    {
        var hash = _hasher.Hash("passwordA");

        var result = _hasher.Verify("passwordB", hash);

        result.Should().BeFalse();
    }

    [Fact]
    public void Verify_InvalidBase64_ShouldReturnFalse()
    {
        var result = _hasher.Verify("anyPassword", "not-valid-base64!!!");

        result.Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongBufferSize_ShouldReturnFalse()
    {
        var shortHash = Convert.ToBase64String(new byte[] { 1, 2, 3 });

        var result = _hasher.Verify("anyPassword", shortHash);

        result.Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongVersion_ShouldReturnFalse()
    {
        var bytes = new byte[1 + 16 + 32];
        bytes[0] = 2; // wrong version
        var badHash = Convert.ToBase64String(bytes);

        var result = _hasher.Verify("anyPassword", badHash);

        result.Should().BeFalse();
    }

    [Fact]
    public void Verify_EmptyPassword_ShouldReturnFalse()
    {
        var hash = _hasher.Hash("somePassword");

        var result = _hasher.Verify("", hash);

        result.Should().BeFalse();
    }

    [Fact]
    public void Verify_EmptyHash_ShouldReturnFalse()
    {
        var result = _hasher.Verify("anyPassword", "");

        result.Should().BeFalse();
    }
}
