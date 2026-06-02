using TenE0.Core.Abstractions;
using TenE0.Core.Common;

namespace TenE0.Core.Tests.Common;

public sealed class ApiResultTests
{
    [Fact]
    public void Ok_ShouldSetSuccessAndData()
    {
        var result = ApiResult<string>.Ok("hello");

        result.success.Should().BeTrue();
        result.data.Should().Be("hello");
        result.errorMessage.Should().BeNull();
        result.errorCode.Should().BeNull();
    }

    [Fact]
    public void Fail_ShouldSetErrorFields()
    {
        var result = ApiResult<int>.Fail("something failed");

        result.success.Should().BeFalse();
        result.errorMessage.Should().Be("something failed");
        result.showType.Should().Be(2);
    }

    [Fact]
    public void Fail_WithCode_ShouldSetCode()
    {
        var result = ApiResult<int>.Fail("bad request", code: "VALIDATION_ERROR");

        result.errorCode.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public void Fail_WithNames_ShouldSetNameBound()
    {
        var names = new[] { "Email", "Password" };
        var result = ApiResult<int>.Fail("invalid fields", nameBound: names);

        result.nameBound.Should().BeEquivalentTo("Email", "Password");
    }

    [Fact]
    public void FromErrs_ValidErrs_ShouldStillFail()
    {
        var mockErrs = new Mock<IErrs>();
        mockErrs.Setup(e => e.IsValid).Returns(true);
        mockErrs.Setup(e => e.GetFirstError()).Returns((string?)null);
        mockErrs.Setup(e => e.Keys).Returns(Array.Empty<string>());

        var result = ApiResult<int>.FromErrs(mockErrs.Object);

        result.success.Should().BeFalse();
        result.errorMessage.Should().NotBeNull().And.NotBeNullOrEmpty("default error message should be set");
    }

    [Fact]
    public void FromErrs_WithError_ShouldUseFirstError()
    {
        var mockErrs = new Mock<IErrs>();
        mockErrs.Setup(e => e.IsValid).Returns(false);
        mockErrs.Setup(e => e.GetFirstError()).Returns("first error message");
        mockErrs.Setup(e => e.Keys).Returns(new[] { "FieldA" });

        var result = ApiResult<int>.FromErrs(mockErrs.Object);

        result.errorMessage.Should().Be("first error message");
    }

    [Fact]
    public void FromErrs_ShouldIncludeKeysAsNames()
    {
        var keys = new[] { "Username", "Email" };
        var mockErrs = new Mock<IErrs>();
        mockErrs.Setup(e => e.IsValid).Returns(false);
        mockErrs.Setup(e => e.GetFirstError()).Returns("validation failed");
        mockErrs.Setup(e => e.Keys).Returns(keys);

        var result = ApiResult<int>.FromErrs(mockErrs.Object);

        result.nameBound.Should().BeEquivalentTo("Username", "Email");
    }
}
