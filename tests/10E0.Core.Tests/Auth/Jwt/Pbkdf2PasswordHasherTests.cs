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

    // ── #97: 防 timing attack 用的 DummyHash ──

    [Fact]
    public void DummyHash_ShouldBeNonEmptyBase64()
    {
        var dummy = _hasher.DummyHash;

        dummy.Should().NotBeNullOrEmpty();
        // 长度符合 version(1) + salt(16) + key(32) = 49 字节的 base64 编码
        Convert.FromBase64String(dummy).Length.Should().Be(1 + 16 + 32);
    }

    [Fact]
    public void DummyHash_ShouldBeStableAcrossInstances()
    {
        // 静态 readonly —— 进程内只算一次，多实例共享同一值
        var hasher1 = new Pbkdf2PasswordHasher();
        var hasher2 = new Pbkdf2PasswordHasher();

        hasher1.DummyHash.Should().Be(hasher2.DummyHash);
    }

    [Fact]
    public void Verify_AnyRealisticPasswordAgainstDummyHash_ShouldReturnFalse()
    {
        // #97 安全断言：dummy hash 是用全零盐 + 固定 placeholder 明文算出的。
        // 攻击者探测的真实业务密码不可能等于这个 placeholder（业务密码由用户自设/策略控制），
        // 因此 Verify(realPassword, dummy) 必然返回 false —— 业务结果仍是"用户名或密码错误"。
        var dummy = _hasher.DummyHash;

        _hasher.Verify("any-attempt-password", dummy).Should().BeFalse();
        _hasher.Verify("user-chosen-pass-123", dummy).Should().BeFalse();
        _hasher.Verify("hunter2", dummy).Should().BeFalse();
    }

    [Fact]
    public void Verify_EmptyOrNullAgainstDummyHash_ShouldReturnFalse()
    {
        var dummy = _hasher.DummyHash;

        _hasher.Verify("", dummy).Should().BeFalse();
    }

    [Fact]
    public void DummyHash_ShouldHaveDifferentSaltFromRealHashOfSameInput()
    {
        // #97 设计目标：dummy salt 是全零，真实 hash 的 salt 是随机 16 字节。
        // 同样的明文用不同 salt 算出不同的 key，确保业务流不会被 dummy 撞库。
        var realHash = _hasher.Hash("anyPassword");
        var dummy = _hasher.DummyHash;

        realHash.Should().NotBe(dummy);
        // 真实 hash 用真实 salt，能用对应密码解开
        _hasher.Verify("anyPassword", realHash).Should().BeTrue();
        // 同一个密码用 dummy 的全零 salt 去解，应该不匹配
        _hasher.Verify("anyPassword", dummy).Should().BeFalse();
    }
}
