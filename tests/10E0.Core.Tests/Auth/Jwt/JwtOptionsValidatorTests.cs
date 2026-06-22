using TenE0.Core.Auth.Jwt;

namespace TenE0.Core.Tests.Auth.Jwt;

/// <summary>
/// 安全回归测试：JwtOptionsValidator 必须拒绝占位符/空/太短的 SigningKey。
/// 来源：issue #92 [P0][Security] JWT SigningKey 硬编码 fallback 允许伪造 token。
/// </summary>
[Trait("Category", "Unit")]
public sealed class JwtOptionsValidatorTests
{
    private static JwtOptions MakeValidOptions() => new()
    {
        Issuer = "10E0.Test",
        Audience = "10E0.Test",
        SigningKey = "abcdefghijklmnopqrstuvwxyz012345", // 32 chars / 32 bytes ASCII
    };

    [Fact]
    public void Validate_ValidOptions_ReturnsSuccess()
    {
        // Arrange
        var sut = new JwtOptionsValidator();

        // Act
        var result = sut.Validate(null, MakeValidOptions());

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Validate_EmptyOrWhitespaceSigningKey_Fails(string? key)
    {
        // Arrange
        var sut = new JwtOptionsValidator();
        var options = MakeValidOptions();
        options.SigningKey = key!;

        // Act
        var result = sut.Validate(null, options);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("SigningKey is required"));
    }

    [Theory]
    [InlineData("dev-secret-CHANGE_ME-in-production-must-be-at-least-32-bytes-long")]
    [InlineData("my-TODO-jwt-signing-key-padded-to-32-bytes-x")]
    [InlineData("my-PLACEHOLDER-jwt-signing-key-padded-to-32x")]
    [InlineData("your-32-byte-default-jwt-signing-key-xx")]
    public void Validate_PlaceholderSigningKey_Fails(string key)
    {
        // Arrange
        var sut = new JwtOptionsValidator();
        var options = MakeValidOptions();
        options.SigningKey = key;

        // Act
        var result = sut.Validate(null, options);

        // Assert：占位符必须被拦截
        Assert.True(key.Length >= 32, $"test data precondition: key must be >= 32 bytes, was {key.Length}");
        result.Succeeded.Should().BeFalse($"key '{key}' (len {key.Length}) should fail validation; failures: {string.Join(" | ", result.Failures ?? Array.Empty<string>())}");
        result.Failures.Should().Contain(f => f.Contains("placeholder"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("16-byte-key-here!!")]
    [InlineData("31-bytes-padding-xxxxxxxxxx")]  // 31 chars
    public void Validate_ShortSigningKey_Fails(string key)
    {
        // Arrange
        var sut = new JwtOptionsValidator();
        var options = MakeValidOptions();
        options.SigningKey = key;

        // Act
        var result = sut.Validate(null, options);

        // Assert：HS256 推荐 ≥ 32 字节
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("at least 32 bytes"));
    }

    [Fact]
    public void Validate_EmptyIssuer_Fails()
    {
        // Arrange
        var sut = new JwtOptionsValidator();
        var options = MakeValidOptions();
        options.Issuer = "";

        // Act
        var result = sut.Validate(null, options);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("Issuer is required"));
    }

    [Fact]
    public void Validate_EmptyAudience_Fails()
    {
        // Arrange
        var sut = new JwtOptionsValidator();
        var options = MakeValidOptions();
        options.Audience = " ";

        // Act
        var result = sut.Validate(null, options);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("Audience is required"));
    }
}
