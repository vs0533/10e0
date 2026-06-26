using TenE0.Core.Auditing;

namespace TenE0.Core.Tests.Auditing;

/// <summary>
/// <see cref="DefaultAuditFieldFilter"/> 单元测试 — 验证敏感关键字命中约定（issue #152 §6）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class DefaultAuditFieldFilterTests
{
    private readonly DefaultAuditFieldFilter _filter = new();

    [Theory]
    [InlineData("Password")]
    [InlineData("password")]
    [InlineData("PasswordHash")]
    [InlineData("Token")]
    [InlineData("AccessToken")]
    [InlineData("RefreshToken")]
    [InlineData("Apikey")]
    [InlineData("X-Secret")]
    [InlineData("SigningKey")]
    [InlineData("ClientSecret")]
    [InlineData("pwd")]
    public void IsSensitive_MatchesKnownSensitiveNames(string name)
    {
        _filter.IsSensitive(name).Should().BeTrue(
            $"{name} contains a sensitive keyword and must be masked");
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Code")]
    [InlineData("DisplayName")]
    [InlineData("Salary")]
    [InlineData("IsActive")]
    [InlineData("Id")]
    [InlineData("CreateTime")]
    [InlineData("Description")]
    public void IsSensitive_DoesNotMatchRegularNames(string name)
    {
        _filter.IsSensitive(name).Should().BeFalse(
            $"{name} is a regular field and must not be masked");
    }

    [Theory]
    [InlineData("anything")]
    [InlineData(null)]
    public void Mask_AlwaysReturnsPlaceholder(object? value)
    {
        _filter.Mask("Password", value).Should().Be("***");
    }
}
